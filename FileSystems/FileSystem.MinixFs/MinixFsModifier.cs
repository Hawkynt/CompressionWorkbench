#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixFs;

/// <summary>
/// In-place MinixFS v3 modifier — performs O(touched bytes) random-access I/O
/// against a Minix v3 image. Only the superblock, inode/zone bitmaps, the
/// affected inode slots, the root directory zone, and the file's data zones
/// are read or written.
///
/// <para>Layout (matching <see cref="MinixFsWriter"/>): 1024-byte blocks,
/// boot block (block 0), superblock (block 1), inode bitmap (1 block),
/// zone bitmap (1 block), inode table, data zones. Root inode = 1.</para>
/// </summary>
public static class MinixFsModifier {

  private const ushort MagicV3 = 0x4D5A;
  private const int SuperblockOffset = 1024;
  private const int V3InodeSize = 64;
  private const int V3DirEntrySize = 64;
  private const int MaxDirectZones = 7;
  private const ushort InodeModeDir = 0x4000;
  private const ushort InodeModeReg = 0x8000;
  private const ushort DefaultFileMode = InodeModeReg | 0x01A4; // 0644
  private const uint RootInode = 1;

  private sealed record class Geometry(
    int BlockSize,
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
  /// Adds a file to an existing Minix v3 image. The file is stored using direct
  /// zone pointers only (max 7 zones = 7168 bytes at 1024-byte blocks).
  /// </summary>
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

    // Read bitmaps.
    var imapBuf = ReadBlock(image, geom, geom.ImapOffset, geom.ImapBlocks * geom.BlockSize);
    var zmapBuf = ReadBlock(image, geom, geom.ZmapOffset, geom.ZmapBlocks * geom.BlockSize);

    // Allocate inode (1-based; bit 0 = inode 1).
    var newInodeOpt = AllocateBit(imapBuf, 0, (int)geom.TotalInodes);
    if (newInodeOpt == null)
      throw new IOException("MinixFs: no free inodes.");
    var newInode = newInodeOpt.Value;

    // Allocate data zones.
    var allocatedZones = new List<int>(zonesNeeded);
    for (var i = 0; i < zonesNeeded; i++) {
      var zone = AllocateBit(zmapBuf, geom.FirstDataZone, (int)geom.TotalZones);
      if (zone == null) {
        // Roll back.
        foreach (var z in allocatedZones) ClearBit(zmapBuf, z);
        ClearBit(imapBuf, newInode);
        throw new IOException("MinixFs: not enough free zones.");
      }
      allocatedZones.Add(zone.Value);
    }

    // Write file data into allocated zones.
    var written = 0;
    foreach (var zone in allocatedZones) {
      var toWrite = Math.Min(geom.BlockSize, data.Length - written);
      var blockBytes = new byte[geom.BlockSize];
      if (toWrite > 0) Array.Copy(data, written, blockBytes, 0, toWrite);
      WriteAtOffset(image, (long)zone * geom.BlockSize, blockBytes);
      written += toWrite;
    }

    // Build and write the new inode.
    var inodeBytes = new byte[V3InodeSize];
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes, DefaultFileMode);
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(2), 1); // nlinks
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(8), (uint)data.Length);
    for (var i = 0; i < allocatedZones.Count; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(24 + i * 4), (uint)allocatedZones[i]);
    WriteInode(image, geom, (uint)(newInode + 1), inodeBytes);

    // Append directory entry to root directory.
    var rootInode = ReadInode(image, geom, RootInode);
    var rootZone = BinaryPrimitives.ReadUInt32LittleEndian(rootInode.AsSpan(24));
    if (rootZone == 0)
      throw new IOException("MinixFs: root directory has no data zone.");

    var dirData = ReadBlock(image, geom, (long)rootZone * geom.BlockSize, geom.BlockSize);
    var slotFound = false;
    for (var off = 0; off + V3DirEntrySize <= dirData.Length; off += V3DirEntrySize) {
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(off));
      if (ino != 0) continue;
      // Write dir entry: uint32 inode + char[60] name.
      BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(off), (uint)(newInode + 1));
      var nameBytes = Encoding.ASCII.GetBytes(name.Length > 59 ? name[..59] : name);
      // Clear name field first.
      Array.Clear(dirData, off + 4, 60);
      nameBytes.CopyTo(dirData, off + 4);
      slotFound = true;
      break;
    }
    if (!slotFound)
      throw new IOException("MinixFs: root directory is full; no free slot for new entry.");

    WriteAtOffset(image, (long)rootZone * geom.BlockSize, dirData);

    // Persist bitmaps.
    WriteAtOffset(image, geom.ImapOffset, imapBuf);
    WriteAtOffset(image, geom.ZmapOffset, zmapBuf);
  }

  /// <summary>
  /// Removes a named file from an existing Minix v3 image. Returns false if not found.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var geom = ReadGeometry(image);

    // Read root directory.
    var rootInode = ReadInode(image, geom, RootInode);
    var rootZone = BinaryPrimitives.ReadUInt32LittleEndian(rootInode.AsSpan(24));
    if (rootZone == 0) return false;

    var dirData = ReadBlock(image, geom, (long)rootZone * geom.BlockSize, geom.BlockSize);
    var found = false;
    uint targetInodeNum = 0;

    for (var off = 0; off + V3DirEntrySize <= dirData.Length; off += V3DirEntrySize) {
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(off));
      if (ino == 0) continue;
      var entryName = ReadNullTermString(dirData, off + 4, 60);
      if (!entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

      targetInodeNum = ino;
      // Clear the directory entry.
      Array.Clear(dirData, off, V3DirEntrySize);
      found = true;
      break;
    }

    if (!found) return false;

    WriteAtOffset(image, (long)rootZone * geom.BlockSize, dirData);

    // Free the inode's data zones.
    var inode = ReadInode(image, geom, targetInodeNum);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    if ((mode & InodeModeDir) != 0) return false; // refuse to remove directories

    var imapBuf = ReadBlock(image, geom, geom.ImapOffset, geom.ImapBlocks * geom.BlockSize);
    var zmapBuf = ReadBlock(image, geom, geom.ZmapOffset, geom.ZmapBlocks * geom.BlockSize);

    for (var i = 0; i < MaxDirectZones; i++) {
      var zone = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(24 + i * 4));
      if (zone == 0) break;
      ClearBit(zmapBuf, (int)zone);
      if (wipeData)
        WriteAtOffset(image, (long)zone * geom.BlockSize, new byte[geom.BlockSize]);
    }

    // Free the inode.
    ClearBit(imapBuf, (int)(targetInodeNum - 1));
    WriteInode(image, geom, targetInodeNum, new byte[V3InodeSize]);

    // Persist bitmaps.
    WriteAtOffset(image, geom.ImapOffset, imapBuf);
    WriteAtOffset(image, geom.ZmapOffset, zmapBuf);

    return true;
  }

  // ── Geometry ──────────────────────────────────────────────────────────

  private static Geometry ReadGeometry(Stream image) {
    var sb = new byte[32];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(24));
    if (magic != MagicV3)
      throw new InvalidDataException($"MinixFs: invalid magic 0x{magic:X4}, expected 0x4D5A.");

    var totalInodes = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    var imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(6));
    var zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(8));
    var firstDataZone = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(10));
    var blockSizeField = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(28));
    var blockSize = blockSizeField == 0 ? 1024 : (int)blockSizeField;
    var totalZones = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20));

    var imapOffset = 2L * blockSize;
    var zmapOffset = imapOffset + (long)imapBlocks * blockSize;
    var inodeTableOffset = zmapOffset + (long)zmapBlocks * blockSize;

    return new Geometry(blockSize, totalInodes, imapBlocks, zmapBlocks, firstDataZone, totalZones,
      imapOffset, zmapOffset, inodeTableOffset);
  }

  private static byte[] ReadInode(Stream image, Geometry geom, uint inodeNum) {
    var buf = new byte[V3InodeSize];
    image.Position = geom.InodeTableOffset + (long)(inodeNum - 1) * V3InodeSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteInode(Stream image, Geometry geom, uint inodeNum, byte[] data) {
    image.Position = geom.InodeTableOffset + (long)(inodeNum - 1) * V3InodeSize;
    image.Write(data, 0, V3InodeSize);
  }

  private static byte[] ReadBlock(Stream image, Geometry geom, long offset, int size) {
    var buf = new byte[size];
    image.Position = offset;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteAtOffset(Stream image, long offset, byte[] data) {
    image.Position = offset;
    image.Write(data, 0, data.Length);
  }

  // ── Bitmap helpers ────────────────────────────────────────────────────

  private static bool TestBit(byte[] bitmap, int bit) =>
    (bitmap[bit / 8] & (1 << (bit % 8))) != 0;

  private static void SetBit(byte[] bitmap, int bit) =>
    bitmap[bit / 8] |= (byte)(1 << (bit % 8));

  private static void ClearBit(byte[] bitmap, int bit) =>
    bitmap[bit / 8] &= (byte)~(1 << (bit % 8));

  /// <summary>
  /// Allocates the first free bit at or above <paramref name="startBit"/>.
  /// Returns the bit index, or null if none available.
  /// </summary>
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
