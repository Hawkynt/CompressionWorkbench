#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixFs;

/// <summary>
/// TRUE in-place R/W modifier for Minix v1/v2/v3 filesystem images.
/// Performs O(touched bytes) random-access mutation: only the superblock,
/// the relevant inode/zone bitmap bytes, the affected inode slot, the root
/// directory zone, and the file's data zones are read or written. All other
/// bytes of the image remain byte-identical.
///
/// <para>The version is autodetected from the superblock magic at byte
/// offsets 1040 (V1/V2) or 1048 (V3) of the image:
///   <list type="bullet">
///     <item>0x137F — V1, 14-char names</item>
///     <item>0x138F — V1, 30-char names</item>
///     <item>0x2468 — V2, 14-char names</item>
///     <item>0x2478 — V2, 30-char names</item>
///     <item>0x4D5A — V3, 60-char names</item>
///   </list>
/// </para>
///
/// <para>Honest scope: file data is stored only via direct-zone pointers.
/// Larger files (single-indirect, double-indirect, triple-indirect
/// allocation) are deferred — callers asking for files larger than
/// <c>7 * blockSize</c> must fall back to rebuild. Subdirectory creation
/// is deferred for the same reason. Existing indirect-allocated file
/// content is still correctly freed on Remove, however.</para>
/// </summary>
public static class MinixFsInPlaceModifier {

  private const ushort MagicV1_14 = 0x137F;
  private const ushort MagicV1_30 = 0x138F;
  private const ushort MagicV2_14 = 0x2468;
  private const ushort MagicV2_30 = 0x2478;
  private const ushort MagicV3    = 0x4D5A;

  private const int SuperblockOffset = 1024;
  private const int MaxDirectZones = 7;

  // Inode-mode flags shared across versions.
  private const ushort InodeModeDir = 0x4000;
  private const ushort InodeModeReg = 0x8000;
  private const ushort DefaultFileMode = (ushort)(InodeModeReg | 0x01A4); // 0644
  private const uint RootInode = 1;

  private enum Version { V1_14, V1_30, V2_14, V2_30, V3 }

  private sealed record class Geometry(
    Version Version,
    int BlockSize,
    int InodeSize,        // 32 for V1, 64 for V2/V3
    int DirEntrySize,     // 16/32/64 depending on variant
    int NameLen,          // 14/30/60
    int ZonePtrSize,      // 2 for V1/V2 (per existing reader convention), 4 for V3
    int DirInoFieldSize,  // 2 for V1/V2, 4 for V3
    uint TotalInodes,
    ushort ImapBlocks,
    ushort ZmapBlocks,
    ushort FirstDataZone,
    uint TotalZones,
    long ImapOffset,
    long ZmapOffset,
    long InodeTableOffset
  );

  /// <summary>
  /// Adds a file to the existing image in-place. The file is stored via
  /// direct-zone pointers only.
  /// </summary>
  /// <exception cref="IOException">
  /// Raised when the image has no free inode/zone, the directory has no free
  /// slot, or the requested file is larger than <c>7 * blockSize</c>.
  /// </exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var geom = ReadGeometry(image);
    var zonesNeeded = data.Length == 0 ? 0 : (data.Length + geom.BlockSize - 1) / geom.BlockSize;
    if (zonesNeeded > MaxDirectZones)
      throw new IOException(
        $"MinixFs: file '{name}' needs {zonesNeeded} zones; only direct pointers are supported "
        + $"(max {MaxDirectZones * geom.BlockSize} bytes).");

    var imapBuf = ReadBytes(image, geom.ImapOffset, geom.ImapBlocks * geom.BlockSize);
    var zmapBuf = ReadBytes(image, geom.ZmapOffset, geom.ZmapBlocks * geom.BlockSize);

    var newInodeOpt = AllocateBit(imapBuf, 0, (int)geom.TotalInodes);
    if (newInodeOpt == null) throw new IOException("MinixFs: no free inodes.");
    var newInodeBit = newInodeOpt.Value;
    var newInodeNum = (uint)(newInodeBit + 1);

    var allocatedZones = new List<int>(zonesNeeded);
    for (var i = 0; i < zonesNeeded; i++) {
      var zone = AllocateBit(zmapBuf, geom.FirstDataZone, (int)geom.TotalZones);
      if (zone == null) {
        foreach (var z in allocatedZones) ClearBit(zmapBuf, z);
        ClearBit(imapBuf, newInodeBit);
        throw new IOException("MinixFs: not enough free zones.");
      }
      allocatedZones.Add(zone.Value);
    }

    // Write payload into the allocated data zones.
    var written = 0;
    foreach (var zone in allocatedZones) {
      var toWrite = Math.Min(geom.BlockSize, data.Length - written);
      var blockBytes = new byte[geom.BlockSize];
      if (toWrite > 0) Array.Copy(data, written, blockBytes, 0, toWrite);
      WriteAt(image, (long)zone * geom.BlockSize, blockBytes);
      written += toWrite;
    }

    // Construct + commit the new inode.
    var inodeBytes = BuildInode(geom, DefaultFileMode, (uint)data.Length, allocatedZones);
    WriteInode(image, geom, newInodeNum, inodeBytes);

    // Insert directory entry into the root directory's first data zone.
    var rootInode = ReadInode(image, geom, RootInode);
    var rootZone = ReadInodeFirstZone(geom, rootInode);
    if (rootZone == 0) throw new IOException("MinixFs: root directory has no data zone.");

    var dirData = ReadBytes(image, (long)rootZone * geom.BlockSize, geom.BlockSize);
    if (!TryInsertDirEntry(geom, dirData, newInodeNum, name))
      throw new IOException("MinixFs: root directory is full; no free slot for new entry.");

    WriteAt(image, (long)rootZone * geom.BlockSize, dirData);

    WriteAt(image, geom.ImapOffset, imapBuf);
    WriteAt(image, geom.ZmapOffset, zmapBuf);
  }

  /// <summary>
  /// Removes a named file from the image in-place. Returns <c>false</c> if not found.
  /// Frees every direct-pointer data zone the inode lists; indirect-allocated zones
  /// (single/double/triple) are also enumerated and freed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var geom = ReadGeometry(image);

    var rootInode = ReadInode(image, geom, RootInode);
    var rootZone = ReadInodeFirstZone(geom, rootInode);
    if (rootZone == 0) return false;

    var dirData = ReadBytes(image, (long)rootZone * geom.BlockSize, geom.BlockSize);
    if (!TryFindAndClearDirEntry(geom, dirData, name, out var targetInodeNum)) return false;

    WriteAt(image, (long)rootZone * geom.BlockSize, dirData);

    var inode = ReadInode(image, geom, targetInodeNum);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    if ((mode & InodeModeDir) != 0) return false; // refuse to remove directories

    var imapBuf = ReadBytes(image, geom.ImapOffset, geom.ImapBlocks * geom.BlockSize);
    var zmapBuf = ReadBytes(image, geom.ZmapOffset, geom.ZmapBlocks * geom.BlockSize);

    // Free every reachable data zone: direct + indirect (single/double/triple).
    var inodeZones = ReadInodeZones(geom, inode);
    var directCount = MaxDirectZones;
    for (var i = 0; i < directCount; i++) {
      var z = inodeZones[i];
      if (z == 0) continue;
      ClearBit(zmapBuf, (int)z);
      if (wipeData) WriteAt(image, (long)z * geom.BlockSize, new byte[geom.BlockSize]);
    }

    // Single indirect.
    if (inodeZones.Length > directCount && inodeZones[directCount] != 0)
      FreeIndirect(image, geom, zmapBuf, inodeZones[directCount], level: 1, wipeData);

    // Double indirect.
    if (inodeZones.Length > directCount + 1 && inodeZones[directCount + 1] != 0)
      FreeIndirect(image, geom, zmapBuf, inodeZones[directCount + 1], level: 2, wipeData);

    // Triple indirect (V3 only).
    if (geom.Version == Version.V3 && inodeZones.Length > directCount + 2 && inodeZones[directCount + 2] != 0)
      FreeIndirect(image, geom, zmapBuf, inodeZones[directCount + 2], level: 3, wipeData);

    ClearBit(imapBuf, (int)(targetInodeNum - 1));
    WriteInode(image, geom, targetInodeNum, new byte[geom.InodeSize]);

    WriteAt(image, geom.ImapOffset, imapBuf);
    WriteAt(image, geom.ZmapOffset, zmapBuf);

    return true;
  }

  /// <summary>
  /// Replaces the data of a named file in-place. If the new data fits in the
  /// inode's already-allocated direct zones, the data is rewritten verbatim
  /// (zones unchanged); otherwise the file is fully reallocated — old direct
  /// zones are freed and new zones (still direct-only) are allocated.
  /// </summary>
  /// <returns><c>true</c> on success, <c>false</c> if the file does not exist
  /// or the new data exceeds the direct-pointer ceiling (7 * blockSize).</returns>
  public static bool Replace(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var geom = ReadGeometry(image);

    var rootInode = ReadInode(image, geom, RootInode);
    var rootZone = ReadInodeFirstZone(geom, rootInode);
    if (rootZone == 0) return false;

    var dirData = ReadBytes(image, (long)rootZone * geom.BlockSize, geom.BlockSize);
    if (!TryFindDirEntry(geom, dirData, name, out var targetInodeNum)) return false;

    var inode = ReadInode(image, geom, targetInodeNum);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    if ((mode & InodeModeDir) != 0) return false;

    var inodeZones = ReadInodeZones(geom, inode);
    var existingZones = new List<int>(MaxDirectZones);
    for (var i = 0; i < MaxDirectZones; i++) {
      var z = inodeZones[i];
      if (z == 0) continue;
      existingZones.Add((int)z);
    }

    var zonesNeeded = data.Length == 0 ? 0 : (data.Length + geom.BlockSize - 1) / geom.BlockSize;
    if (zonesNeeded > MaxDirectZones) return false;

    var imapBuf = ReadBytes(image, geom.ImapOffset, geom.ImapBlocks * geom.BlockSize);
    var zmapBuf = ReadBytes(image, geom.ZmapOffset, geom.ZmapBlocks * geom.BlockSize);

    List<int> targetZones;
    if (zonesNeeded <= existingZones.Count) {
      // Fits in already-allocated direct zones — reuse them, free the surplus.
      targetZones = existingZones.GetRange(0, zonesNeeded);
      for (var i = zonesNeeded; i < existingZones.Count; i++) {
        ClearBit(zmapBuf, existingZones[i]);
        WriteAt(image, (long)existingZones[i] * geom.BlockSize, new byte[geom.BlockSize]);
      }
    } else {
      // Need more zones than currently allocated: keep existing + allocate extras.
      targetZones = new List<int>(existingZones);
      var extras = zonesNeeded - existingZones.Count;
      for (var i = 0; i < extras; i++) {
        var zone = AllocateBit(zmapBuf, geom.FirstDataZone, (int)geom.TotalZones);
        if (zone == null) {
          // Roll back the allocations made so far.
          for (var k = existingZones.Count; k < targetZones.Count; k++)
            ClearBit(zmapBuf, targetZones[k]);
          return false;
        }
        targetZones.Add(zone.Value);
      }
    }

    // Rewrite payload.
    var written = 0;
    foreach (var zone in targetZones) {
      var toWrite = Math.Min(geom.BlockSize, data.Length - written);
      var blockBytes = new byte[geom.BlockSize];
      if (toWrite > 0) Array.Copy(data, written, blockBytes, 0, toWrite);
      WriteAt(image, (long)zone * geom.BlockSize, blockBytes);
      written += toWrite;
    }

    var newInode = BuildInode(geom, mode, (uint)data.Length, targetZones);
    WriteInode(image, geom, targetInodeNum, newInode);

    WriteAt(image, geom.ImapOffset, imapBuf);
    WriteAt(image, geom.ZmapOffset, zmapBuf);
    return true;
  }

  // ── Geometry / superblock detection ───────────────────────────────────

  private static Geometry ReadGeometry(Stream image) {
    var sb = new byte[32];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);

    var magic16 = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(16));
    var magic24 = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(24));

    Version version;
    int inodeSize, dirEntrySize, nameLen, zonePtrSize, dirInoFieldSize;
    uint totalInodes;
    ushort imapBlocks, zmapBlocks, firstDataZone;
    int blockSize;
    uint totalZones;

    if (magic24 == MagicV3) {
      version = Version.V3;
      inodeSize = 64;
      dirEntrySize = 64;
      nameLen = 60;
      zonePtrSize = 4;
      dirInoFieldSize = 4;
      totalInodes = BinaryPrimitives.ReadUInt32LittleEndian(sb);
      imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(6));
      zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(8));
      firstDataZone = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(10));
      var blockSizeField = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(28));
      blockSize = blockSizeField == 0 ? 1024 : blockSizeField;
      totalZones = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20));
    } else if (magic16 is MagicV1_14 or MagicV1_30 or MagicV2_14 or MagicV2_30) {
      version = magic16 switch {
        MagicV1_14 => Version.V1_14,
        MagicV1_30 => Version.V1_30,
        MagicV2_14 => Version.V2_14,
        _ => Version.V2_30,
      };
      // The existing reader/writer convention in this codebase treats V1 and
      // V2 identically: 32-byte inodes with 16-bit zone pointers. We match
      // that convention so the on-disk artefacts we mutate read back through
      // the existing MinixFsReader unchanged.
      inodeSize = 32;
      zonePtrSize = 2;
      dirInoFieldSize = 2;
      nameLen = version is Version.V1_30 or Version.V2_30 ? 30 : 14;
      dirEntrySize = 2 + nameLen;
      totalInodes = BinaryPrimitives.ReadUInt16LittleEndian(sb);
      var nzones = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(2));
      imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(4));
      zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(6));
      firstDataZone = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(8));
      blockSize = 1024;
      totalZones = nzones;
    } else {
      throw new InvalidDataException(
        $"MinixFs: invalid magic. Got 0x{magic16:X4} at offset 16, 0x{magic24:X4} at offset 24.");
    }

    var imapOffset = 2L * blockSize;
    var zmapOffset = imapOffset + (long)imapBlocks * blockSize;
    var inodeTableOffset = zmapOffset + (long)zmapBlocks * blockSize;

    return new Geometry(version, blockSize, inodeSize, dirEntrySize, nameLen, zonePtrSize,
      dirInoFieldSize, totalInodes, imapBlocks, zmapBlocks, firstDataZone, totalZones,
      imapOffset, zmapOffset, inodeTableOffset);
  }

  // ── Inode helpers ─────────────────────────────────────────────────────

  private static byte[] ReadInode(Stream image, Geometry geom, uint inodeNum) {
    var buf = new byte[geom.InodeSize];
    image.Position = geom.InodeTableOffset + (long)(inodeNum - 1) * geom.InodeSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteInode(Stream image, Geometry geom, uint inodeNum, byte[] data) {
    image.Position = geom.InodeTableOffset + (long)(inodeNum - 1) * geom.InodeSize;
    image.Write(data, 0, geom.InodeSize);
  }

  // V3 inode zone pointer count = 10; V1/V2 (per this codebase) = 9.
  private static int InodeZoneSlotCount(Geometry geom) =>
    geom.Version == Version.V3 ? 10 : 9;

  // Layout-aware zone-pointer field offset within an inode.
  private static int InodeZoneFieldOffset(Geometry geom) =>
    geom.Version == Version.V3 ? 24 : 14;

  private static uint[] ReadInodeZones(Geometry geom, byte[] inode) {
    var slots = InodeZoneSlotCount(geom);
    var off = InodeZoneFieldOffset(geom);
    var zones = new uint[slots];
    for (var i = 0; i < slots; i++) {
      zones[i] = geom.ZonePtrSize == 4
        ? BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(off + i * 4))
        : BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(off + i * 2));
    }
    return zones;
  }

  private static uint ReadInodeFirstZone(Geometry geom, byte[] inode) {
    var off = InodeZoneFieldOffset(geom);
    return geom.ZonePtrSize == 4
      ? BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(off))
      : BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(off));
  }

  // Builds a brand-new inode with the supplied mode/size/zones. For V1/V2 we
  // use the 32-byte layout (mode/uid/size/time/gid/nlinks/zones[9] as 16-bit);
  // for V3 we use the 64-byte modern layout. Time fields are left zero — the
  // reader does not surface them.
  private static byte[] BuildInode(Geometry geom, ushort mode, uint size, List<int> directZones) {
    var inode = new byte[geom.InodeSize];
    BinaryPrimitives.WriteUInt16LittleEndian(inode, mode);
    if (geom.Version == Version.V3) {
      // V3 layout
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(2), 1); // nlinks
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(8), size);
      for (var i = 0; i < directZones.Count; i++)
        BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(24 + i * 4), (uint)directZones[i]);
    } else {
      // V1/V2 layout (codebase convention)
      // uid (2..4), size (4..8), time (8..12), gid (12), nlinks (13), zones (14..32)
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(4), size);
      inode[13] = 1; // nlinks
      for (var i = 0; i < directZones.Count; i++)
        BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(14 + i * 2), (ushort)directZones[i]);
    }
    return inode;
  }

  // ── Directory-entry helpers ───────────────────────────────────────────

  private static bool TryInsertDirEntry(Geometry geom, byte[] dirData, uint inodeNum, string name) {
    for (var off = 0; off + geom.DirEntrySize <= dirData.Length; off += geom.DirEntrySize) {
      var existingIno = ReadDirInode(geom, dirData, off);
      if (existingIno != 0) continue;
      // Empty slot — write entry.
      WriteDirInode(geom, dirData, off, inodeNum);
      Array.Clear(dirData, off + geom.DirInoFieldSize, geom.NameLen);
      var bytes = Encoding.ASCII.GetBytes(name.Length > geom.NameLen - 1 ? name[..(geom.NameLen - 1)] : name);
      bytes.CopyTo(dirData, off + geom.DirInoFieldSize);
      return true;
    }
    return false;
  }

  private static bool TryFindAndClearDirEntry(Geometry geom, byte[] dirData, string name, out uint inodeNum) {
    for (var off = 0; off + geom.DirEntrySize <= dirData.Length; off += geom.DirEntrySize) {
      var ino = ReadDirInode(geom, dirData, off);
      if (ino == 0) continue;
      var entryName = ReadNullTermString(dirData, off + geom.DirInoFieldSize, geom.NameLen);
      if (!entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
      inodeNum = ino;
      // Zero the whole entry — both inode field and name field.
      Array.Clear(dirData, off, geom.DirEntrySize);
      return true;
    }
    inodeNum = 0;
    return false;
  }

  private static bool TryFindDirEntry(Geometry geom, byte[] dirData, string name, out uint inodeNum) {
    for (var off = 0; off + geom.DirEntrySize <= dirData.Length; off += geom.DirEntrySize) {
      var ino = ReadDirInode(geom, dirData, off);
      if (ino == 0) continue;
      var entryName = ReadNullTermString(dirData, off + geom.DirInoFieldSize, geom.NameLen);
      if (!entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
      inodeNum = ino;
      return true;
    }
    inodeNum = 0;
    return false;
  }

  private static uint ReadDirInode(Geometry geom, byte[] dirData, int offset) =>
    geom.DirInoFieldSize == 4
      ? BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(offset))
      : BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(offset));

  private static void WriteDirInode(Geometry geom, byte[] dirData, int offset, uint inodeNum) {
    if (geom.DirInoFieldSize == 4)
      BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(offset), inodeNum);
    else
      BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(offset), (ushort)inodeNum);
  }

  // ── Indirect-block traversal for Remove ──────────────────────────────

  private static void FreeIndirect(Stream image, Geometry geom, byte[] zmapBuf, uint indirectZone, int level, bool wipeData) {
    if (indirectZone == 0) return;
    var ptrSize = geom.ZonePtrSize;
    var perBlock = geom.BlockSize / ptrSize;
    var block = ReadBytes(image, (long)indirectZone * geom.BlockSize, geom.BlockSize);
    for (var i = 0; i < perBlock; i++) {
      var ptr = ptrSize == 4
        ? BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i * 4))
        : BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(i * 2));
      if (ptr == 0) continue;
      if (level == 1) {
        ClearBit(zmapBuf, (int)ptr);
        if (wipeData) WriteAt(image, (long)ptr * geom.BlockSize, new byte[geom.BlockSize]);
      } else {
        FreeIndirect(image, geom, zmapBuf, ptr, level - 1, wipeData);
      }
    }
    ClearBit(zmapBuf, (int)indirectZone);
    if (wipeData) WriteAt(image, (long)indirectZone * geom.BlockSize, new byte[geom.BlockSize]);
  }

  // ── Bitmap / byte helpers ─────────────────────────────────────────────

  private static byte[] ReadBytes(Stream image, long offset, int size) {
    var buf = new byte[size];
    image.Position = offset;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteAt(Stream image, long offset, byte[] data) {
    image.Position = offset;
    image.Write(data, 0, data.Length);
  }

  private static bool TestBit(byte[] bitmap, int bit) =>
    (bitmap[bit / 8] & (1 << (bit % 8))) != 0;

  private static void SetBit(byte[] bitmap, int bit) =>
    bitmap[bit / 8] |= (byte)(1 << (bit % 8));

  private static void ClearBit(byte[] bitmap, int bit) =>
    bitmap[bit / 8] &= (byte)~(1 << (bit % 8));

  private static int? AllocateBit(byte[] bitmap, int startBit, int maxBit) {
    for (var bit = startBit; bit < maxBit && bit / 8 < bitmap.Length; bit++) {
      if (TestBit(bitmap, bit)) continue;
      SetBit(bitmap, bit);
      return bit;
    }
    return null;
  }

  private static string ReadNullTermString(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return Encoding.ASCII.GetString(data, offset, end - offset);
  }
}
