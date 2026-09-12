#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixV1;

/// <summary>
/// TRUE in-place R/W modifier for original Minix v1 filesystem images. Performs
/// O(touched bytes) random-access mutation against the real mkfs.minix on-disk
/// layout: only the superblock zone-count field, the relevant inode/zone bitmap
/// bytes, the affected inode slot, the touched directory zone and the file's
/// data zones (plus a single-indirect pointer block when needed) are read or
/// written. Every other byte stays byte-identical.
///
/// <para>The v1 bitmaps follow the classic mkfs.minix convention: the inode
/// bitmap addresses inode <c>i</c> at bit <c>i</c> (bit 0 reserved), and the
/// zone bitmap addresses zone <c>Z</c> at bit <c>Z - firstDataZone + 1</c>
/// (bit 0 reserved). When the image has no free zone, fresh zones are appended
/// at the end of the image (the bitmap and <c>s_nzones</c> grow with it) — a
/// genuine in-place grow, not a re-pack.</para>
///
/// <para>Honest scope: files are stored through the 7 direct zones plus the one
/// single-indirect zone (up to 7 + 512 = 519 zones ≈ 531 KB). Anything larger,
/// or a request the bitmap cannot satisfy, throws <see cref="IOException"/> so
/// the descriptor can fall back to a full rebuild. Subdirectory creation is also
/// deferred to rebuild. Existing double-indirect content is still freed on
/// Remove.</para>
/// </summary>
public static class MinixV1InPlaceModifier {

  private const ushort MagicV1_14 = 0x137F;
  private const ushort MagicV1_30 = 0x138F;
  private const int BlockSize = 1024;
  private const int SuperblockOffset = 1024;
  private const int InodeSize = 32;
  private const int DirectZones = 7;
  private const int IndirectSlot = 7;
  private const int DoubleIndirectSlot = 8;
  private const int ZonePtrsPerBlock = BlockSize / 2; // 512 (16-bit pointers)
  private const int MaxDirectAndSingle = DirectZones + ZonePtrsPerBlock; // 519 data zones

  private const ushort ModeDir = 0x4000;
  private const ushort ModeReg = 0x8000;
  private const ushort DefaultFileMode = (ushort)(ModeReg | 0x01A4); // 0644
  private const uint RootInode = 1;

  private sealed record class Geometry(
    int NameLen,
    int DirEntrySize,
    uint TotalInodes,
    uint TotalZones,
    ushort ImapBlocks,
    ushort ZmapBlocks,
    ushort FirstDataZone,
    long ImapOffset,
    long ZmapOffset,
    long InodeTableOffset);

  /// <summary>Adds a regular file to the root directory in-place.</summary>
  /// <exception cref="IOException">No free inode, no free root-dir slot, or the
  /// payload exceeds the direct + single-indirect ceiling.</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    if (name.Contains('/') || name.Contains('\\'))
      throw new IOException("MinixV1: nested-path add is deferred to rebuild.");

    var geom = ReadGeometry(image);
    var dataZonesNeeded = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;
    if (dataZonesNeeded > MaxDirectAndSingle)
      throw new IOException(
        $"MinixV1: file '{name}' needs {dataZonesNeeded} zones; only direct + single-indirect "
        + $"({MaxDirectAndSingle} zones) are supported in-place.");

    var imapBuf = ReadBytes(image, geom.ImapOffset, geom.ImapBlocks * BlockSize);
    var zmapBuf = ReadBytes(image, geom.ZmapOffset, geom.ZmapBlocks * BlockSize);

    // Allocate the inode (bit index == inode number; bit 0 reserved).
    var inodeBit = AllocateInodeBit(imapBuf, geom);
    if (inodeBit == null) throw new IOException("MinixV1: no free inodes.");
    var newInodeNum = (uint)inodeBit.Value;

    // Plan the data + single-indirect zones, growing the image if needed.
    var indirectNeeded = dataZonesNeeded > DirectZones;
    var totalZonesNeeded = dataZonesNeeded + (indirectNeeded ? 1 : 0);
    var allocated = AllocateZones(image, ref geom, zmapBuf, totalZonesNeeded);
    if (allocated == null) {
      ClearBit(imapBuf, inodeBit.Value);
      throw new IOException("MinixV1: not enough free zones (and image could not grow).");
    }

    var dataZones = allocated.GetRange(0, dataZonesNeeded);
    var indirectZone = indirectNeeded ? allocated[dataZonesNeeded] : 0;

    WriteFileData(image, data, dataZones, indirectZone);
    var inodeBytes = BuildInode(DefaultFileMode, (uint)data.Length, dataZones, indirectZone);
    WriteInode(image, geom, newInodeNum, inodeBytes);

    // Insert the directory entry into the root directory's first data zone.
    var rootInode = ReadInode(image, geom, RootInode);
    var rootZone = ReadFirstZone(rootInode);
    if (rootZone == 0) throw new IOException("MinixV1: root directory has no data zone.");
    var rootSize = BinaryPrimitives.ReadUInt32LittleEndian(rootInode.AsSpan(4));
    var dirData = ReadBytes(image, (long)rootZone * BlockSize, BlockSize);
    if (!TryInsertDirEntry(geom, dirData, newInodeNum, name, ref rootSize))
      throw new IOException("MinixV1: root directory is full; no free slot for new entry.");
    WriteAt(image, (long)rootZone * BlockSize, dirData);
    // Grow the directory inode's i_size so the reader walks the new entry.
    BinaryPrimitives.WriteUInt32LittleEndian(rootInode.AsSpan(4), rootSize);
    WriteInode(image, geom, RootInode, rootInode);

    WriteAt(image, geom.ImapOffset, imapBuf);
    WriteAt(image, geom.ZmapOffset, zmapBuf);
  }

  /// <summary>Removes a named regular file from the root directory in-place.</summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var geom = ReadGeometry(image);
    var rootInode = ReadInode(image, geom, RootInode);
    var rootZone = ReadFirstZone(rootInode);
    if (rootZone == 0) return false;

    var dirData = ReadBytes(image, (long)rootZone * BlockSize, BlockSize);
    if (!TryFindAndClearDirEntry(geom, dirData, name, out var targetInode)) return false;

    var inode = ReadInode(image, geom, targetInode);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    if ((mode & 0xF000) == ModeDir) return false; // refuse to remove directories

    WriteAt(image, (long)rootZone * BlockSize, dirData);

    var imapBuf = ReadBytes(image, geom.ImapOffset, geom.ImapBlocks * BlockSize);
    var zmapBuf = ReadBytes(image, geom.ZmapOffset, geom.ZmapBlocks * BlockSize);

    var zones = ReadInodeZones(inode);
    for (var i = 0; i < DirectZones; i++)
      FreeZone(image, geom, zmapBuf, zones[i], wipeData);

    if (zones[IndirectSlot] != 0) FreeIndirect(image, geom, zmapBuf, zones[IndirectSlot], 1, wipeData);
    if (zones[DoubleIndirectSlot] != 0) FreeIndirect(image, geom, zmapBuf, zones[DoubleIndirectSlot], 2, wipeData);

    ClearBit(imapBuf, (int)targetInode); // inode bit == inode number
    WriteInode(image, geom, targetInode, new byte[InodeSize]);

    WriteAt(image, geom.ImapOffset, imapBuf);
    WriteAt(image, geom.ZmapOffset, zmapBuf);
    return true;
  }

  /// <summary>Replaces a named regular file's data in-place.</summary>
  public static bool Replace(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var geom = ReadGeometry(image);
    var rootInode = ReadInode(image, geom, RootInode);
    var rootZone = ReadFirstZone(rootInode);
    if (rootZone == 0) return false;

    var dirData = ReadBytes(image, (long)rootZone * BlockSize, BlockSize);
    if (!TryFindDirEntry(geom, dirData, name, out var targetInode)) return false;

    var inode = ReadInode(image, geom, targetInode);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    if ((mode & 0xF000) == ModeDir) return false;

    var dataZonesNeeded = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;
    if (dataZonesNeeded > MaxDirectAndSingle) return false;

    var imapBuf = ReadBytes(image, geom.ImapOffset, geom.ImapBlocks * BlockSize);
    var zmapBuf = ReadBytes(image, geom.ZmapOffset, geom.ZmapBlocks * BlockSize);

    // Free every zone the inode currently owns, then re-allocate from scratch.
    var oldZones = ReadInodeZones(inode);
    for (var i = 0; i < DirectZones; i++)
      FreeZone(image, geom, zmapBuf, oldZones[i], wipeData: true);
    if (oldZones[IndirectSlot] != 0) FreeIndirect(image, geom, zmapBuf, oldZones[IndirectSlot], 1, true);
    if (oldZones[DoubleIndirectSlot] != 0) FreeIndirect(image, geom, zmapBuf, oldZones[DoubleIndirectSlot], 2, true);

    var indirectNeeded = dataZonesNeeded > DirectZones;
    var totalZonesNeeded = dataZonesNeeded + (indirectNeeded ? 1 : 0);
    var allocated = AllocateZones(image, ref geom, zmapBuf, totalZonesNeeded);
    if (allocated == null) return false;

    var dataZones = allocated.GetRange(0, dataZonesNeeded);
    var indirectZone = indirectNeeded ? allocated[dataZonesNeeded] : 0;

    WriteFileData(image, data, dataZones, indirectZone);
    var newInode = BuildInode(mode, (uint)data.Length, dataZones, indirectZone);
    WriteInode(image, geom, targetInode, newInode);

    WriteAt(image, geom.ImapOffset, imapBuf);
    WriteAt(image, geom.ZmapOffset, zmapBuf);
    return true;
  }

  // ── Geometry ────────────────────────────────────────────────────────────

  private static Geometry ReadGeometry(Stream image) {
    var sb = new byte[32];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);

    var ninodes = BinaryPrimitives.ReadUInt16LittleEndian(sb);
    var nzones = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(2));
    var imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(4));
    var zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(6));
    var firstDataZone = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(8));
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(16));
    if (magic != MagicV1_14 && magic != MagicV1_30)
      throw new InvalidDataException($"MinixV1: invalid magic 0x{magic:X4}.");

    var nameLen = magic == MagicV1_30 ? 30 : 14;
    var imapOffset = 2L * BlockSize;
    var zmapOffset = imapOffset + (long)imapBlocks * BlockSize;
    var inodeTableOffset = zmapOffset + (long)zmapBlocks * BlockSize;

    return new Geometry(nameLen, 2 + nameLen, ninodes, nzones, imapBlocks, zmapBlocks,
      firstDataZone, imapOffset, zmapOffset, inodeTableOffset);
  }

  // ── Zone allocation (with in-place image grow) ────────────────────────────

  // Allocates <paramref name="count"/> fresh zones. Reuses free bitmap bits
  // first; when none remain it grows the image by appending zones at EOF and
  // bumps s_nzones. Returns null only when the zone bitmap itself is exhausted.
  private static List<int>? AllocateZones(Stream image, ref Geometry geom, byte[] zmapBuf, int count) {
    var result = new List<int>(count);
    for (var i = 0; i < count; i++) {
      var zone = AllocateFreeZoneBit(zmapBuf, geom);
      if (zone == null) {
        var grown = GrowImageByOneZone(image, ref geom, zmapBuf);
        if (grown == null) {
          foreach (var z in result) ClearZoneBit(zmapBuf, geom, z);
          return null;
        }
        zone = grown;
      }
      result.Add(zone.Value);
    }
    return result;
  }

  // Appends a single zone at the end of the image, marks it allocated in the
  // bitmap, increments s_nzones in the superblock, and returns the new absolute
  // zone number. Returns null if the zone bitmap has no spare bit for it.
  private static int? GrowImageByOneZone(Stream image, ref Geometry geom, byte[] zmapBuf) {
    var newZone = (int)geom.TotalZones; // next absolute zone == old count
    var bit = newZone - geom.FirstDataZone + 1;
    if (bit / 8 >= zmapBuf.Length) return null; // bitmap block full

    // Physically extend the image so the new zone exists.
    var newLength = (long)(newZone + 1) * BlockSize;
    if (image.Length < newLength) image.SetLength(newLength);

    SetBit(zmapBuf, bit);
    geom = geom with { TotalZones = (uint)(newZone + 1) };
    image.Position = SuperblockOffset + 2;
    var nz = new byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(nz, (ushort)geom.TotalZones);
    image.Write(nz, 0, 2);
    return newZone;
  }

  private static int? AllocateFreeZoneBit(byte[] zmapBuf, Geometry geom) {
    // Zone Z occupies bit (Z - firstDataZone + 1); scan from the first data zone.
    var maxBit = (int)(geom.TotalZones - geom.FirstDataZone + 1);
    for (var bit = 1; bit < maxBit && bit / 8 < zmapBuf.Length; bit++) {
      if (TestBit(zmapBuf, bit)) continue;
      SetBit(zmapBuf, bit);
      return geom.FirstDataZone + bit - 1;
    }
    return null;
  }

  private static int? AllocateInodeBit(byte[] imapBuf, Geometry geom) {
    for (var ino = 1; ino <= geom.TotalInodes && ino / 8 < imapBuf.Length; ino++) {
      if (TestBit(imapBuf, ino)) continue;
      SetBit(imapBuf, ino);
      return ino;
    }
    return null;
  }

  private static void ClearZoneBit(byte[] zmapBuf, Geometry geom, int zone) =>
    ClearBit(zmapBuf, zone - geom.FirstDataZone + 1);

  private static void FreeZone(Stream image, Geometry geom, byte[] zmapBuf, uint zone, bool wipeData) {
    if (zone == 0) return;
    ClearZoneBit(zmapBuf, geom, (int)zone);
    if (wipeData) WriteAt(image, (long)zone * BlockSize, new byte[BlockSize]);
  }

  private static void FreeIndirect(Stream image, Geometry geom, byte[] zmapBuf, uint indirectZone, int level, bool wipeData) {
    if (indirectZone == 0) return;
    var block = ReadBytes(image, (long)indirectZone * BlockSize, BlockSize);
    for (var i = 0; i < ZonePtrsPerBlock; i++) {
      var ptr = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(i * 2));
      if (ptr == 0) continue;
      if (level == 1) FreeZone(image, geom, zmapBuf, ptr, wipeData);
      else FreeIndirect(image, geom, zmapBuf, ptr, level - 1, wipeData);
    }
    FreeZone(image, geom, zmapBuf, indirectZone, wipeData);
  }

  // ── Data write ────────────────────────────────────────────────────────────

  private static void WriteFileData(Stream image, byte[] data, List<int> dataZones, int indirectZone) {
    var written = 0;
    foreach (var zone in dataZones) {
      var toWrite = Math.Min(BlockSize, data.Length - written);
      var block = new byte[BlockSize];
      if (toWrite > 0) Array.Copy(data, written, block, 0, toWrite);
      WriteAt(image, (long)zone * BlockSize, block);
      written += toWrite;
    }
    if (indirectZone != 0) {
      var ptrBlock = new byte[BlockSize];
      var singleCount = dataZones.Count - DirectZones;
      for (var i = 0; i < singleCount; i++)
        BinaryPrimitives.WriteUInt16LittleEndian(ptrBlock.AsSpan(i * 2), (ushort)dataZones[DirectZones + i]);
      WriteAt(image, (long)indirectZone * BlockSize, ptrBlock);
    }
  }

  // ── Inode helpers ─────────────────────────────────────────────────────────

  private static byte[] ReadInode(Stream image, Geometry geom, uint inodeNum) {
    var buf = new byte[InodeSize];
    image.Position = geom.InodeTableOffset + (long)(inodeNum - 1) * InodeSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteInode(Stream image, Geometry geom, uint inodeNum, byte[] data) {
    image.Position = geom.InodeTableOffset + (long)(inodeNum - 1) * InodeSize;
    image.Write(data, 0, InodeSize);
  }

  // V1 inode (32 bytes): mode(0) uid(2) size(4,u32) time(8) gid(12) nlinks(13)
  //   zones[9](14..31, u16 each) — 7 direct + single + double.
  private static uint[] ReadInodeZones(byte[] inode) {
    var zones = new uint[9];
    for (var i = 0; i < 9; i++)
      zones[i] = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(14 + i * 2));
    return zones;
  }

  private static uint ReadFirstZone(byte[] inode) =>
    BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(14));

  private static byte[] BuildInode(ushort mode, uint size, List<int> directAndSingleData, int indirectZone) {
    var inode = new byte[InodeSize];
    BinaryPrimitives.WriteUInt16LittleEndian(inode, mode);
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(4), size);
    inode[13] = 1; // nlinks
    var directCount = Math.Min(DirectZones, directAndSingleData.Count);
    for (var i = 0; i < directCount; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(14 + i * 2), (ushort)directAndSingleData[i]);
    if (indirectZone != 0)
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(14 + IndirectSlot * 2), (ushort)indirectZone);
    return inode;
  }

  // ── Directory helpers ──────────────────────────────────────────────────────

  // Inserts an entry into the first free slot. A slot is "free" when its inode
  // field is zero AND it lies beyond the directory's current i_size, OR it is a
  // hole created by an earlier removal (inode zero) within i_size. When the
  // chosen slot extends past i_size, i_size grows by one entry so the reader
  // (which honours i_size) walks the new entry.
  private static bool TryInsertDirEntry(Geometry geom, byte[] dirData, uint inodeNum, string name, ref uint dirSize) {
    for (var off = 0; off + geom.DirEntrySize <= dirData.Length; off += geom.DirEntrySize) {
      if (BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off)) != 0) continue;
      BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(off), (ushort)inodeNum);
      Array.Clear(dirData, off + 2, geom.NameLen);
      var maxName = geom.NameLen - 1;
      var bytes = Encoding.ASCII.GetBytes(name.Length > maxName ? name[..maxName] : name);
      bytes.CopyTo(dirData, off + 2);
      var slotEnd = (uint)(off + geom.DirEntrySize);
      if (slotEnd > dirSize) dirSize = slotEnd;
      return true;
    }
    return false;
  }

  private static bool TryFindAndClearDirEntry(Geometry geom, byte[] dirData, string name, out uint inodeNum) {
    for (var off = 0; off + geom.DirEntrySize <= dirData.Length; off += geom.DirEntrySize) {
      var ino = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off));
      if (ino == 0) continue;
      if (!ReadName(dirData, off + 2, geom.NameLen).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
      inodeNum = ino;
      Array.Clear(dirData, off, geom.DirEntrySize);
      return true;
    }
    inodeNum = 0;
    return false;
  }

  private static bool TryFindDirEntry(Geometry geom, byte[] dirData, string name, out uint inodeNum) {
    for (var off = 0; off + geom.DirEntrySize <= dirData.Length; off += geom.DirEntrySize) {
      var ino = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off));
      if (ino == 0) continue;
      if (!ReadName(dirData, off + 2, geom.NameLen).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
      inodeNum = ino;
      return true;
    }
    inodeNum = 0;
    return false;
  }

  // ── Byte / bitmap helpers ──────────────────────────────────────────────────

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

  private static bool TestBit(byte[] bitmap, int bit) => (bitmap[bit / 8] & (1 << (bit % 8))) != 0;
  private static void SetBit(byte[] bitmap, int bit) => bitmap[bit / 8] |= (byte)(1 << (bit % 8));
  private static void ClearBit(byte[] bitmap, int bit) => bitmap[bit / 8] &= (byte)~(1 << (bit % 8));

  private static string ReadName(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return Encoding.ASCII.GetString(data, offset, end - offset);
  }
}
