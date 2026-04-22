#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixFs;

/// <summary>
/// Reads Minix filesystem images (v1, v2, v3). Parses the superblock, inode table,
/// and directory entries. Supports direct, single-indirect, double-indirect, and
/// triple-indirect zone pointers (v3). V1/V2 support direct and single/double indirect.
/// </summary>
public sealed class MinixFsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<MinixFsEntry> _entries = [];

  public IReadOnlyList<MinixFsEntry> Entries => _entries;

  // Superblock fields
  private uint _ninodes;
  private ushort _imapBlocks;
  private ushort _zmapBlocks;
  private ushort _firstdatazone;
  private int _blockSize;
#pragma warning disable CS0414
  private ushort _magic;
#pragma warning restore CS0414

  // Version
  private enum MinixVersion { V1_14, V1_30, V2_14, V2_30, V3 }
  private MinixVersion _version;

  // Magic constants
  private const ushort MagicV1_14 = 0x137F;
  private const ushort MagicV1_30 = 0x138F;
  private const ushort MagicV2_14 = 0x2468;
  private const ushort MagicV2_30 = 0x2478;
  private const ushort MagicV3    = 0x4D5A;

  private const int SuperblockOffset = 1024;

  // V3 inode size (bytes)
  private const int V3InodeSize = 64;
  // V1 inode size (bytes)
  private const int V1InodeSize = 32;

  public MinixFsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    // Need at least boot block + superblock
    if (_data.Length < SuperblockOffset + 32)
      throw new InvalidDataException("MinixFs: image too small for superblock.");

    var sb = _data.AsSpan(SuperblockOffset);

    // Detect version by reading magic at both possible offsets.
    // V1/V2 superblock: s_magic at byte offset 16 within superblock.
    // V3 superblock: s_magic at byte offset 24 within superblock.
    var magic16 = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(16));
    var magic24 = _data.Length >= SuperblockOffset + 30
      ? BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(24))
      : (ushort)0;

    if (magic24 == MagicV3) {
      _magic = MagicV3;
      _version = MinixVersion.V3;
      ParseSuperblockV3(sb);
    } else if (magic16 == MagicV1_14) {
      _magic = MagicV1_14;
      _version = MinixVersion.V1_14;
      ParseSuperblockV1(sb);
    } else if (magic16 == MagicV1_30) {
      _magic = MagicV1_30;
      _version = MinixVersion.V1_30;
      ParseSuperblockV1(sb);
    } else if (magic16 == MagicV2_14) {
      _magic = MagicV2_14;
      _version = MinixVersion.V2_14;
      ParseSuperblockV1(sb); // V2 uses same superblock layout as V1 for detection purposes
    } else if (magic16 == MagicV2_30) {
      _magic = MagicV2_30;
      _version = MinixVersion.V2_30;
      ParseSuperblockV1(sb);
    } else {
      throw new InvalidDataException(
        $"MinixFs: invalid magic. Got 0x{magic16:X4} at offset 16, 0x{magic24:X4} at offset 24.");
    }

    // Traverse from root inode (inode 1 for Minix, but in the spec root = inode 1)
    // Minix uses 1-based inode numbers; root is inode 1.
    ReadDirectory(1, "");
  }

  // V3 superblock layout (all LE):
  //   uint32 s_ninodes        (0)
  //   uint16 s_pad0           (4)
  //   uint16 s_imap_blocks    (6)
  //   uint16 s_zmap_blocks    (8)
  //   uint16 s_firstdatazone  (10)
  //   uint16 s_log_zone_size  (12)
  //   uint16 s_pad1           (14)
  //   uint32 s_max_size       (16)
  //   uint32 s_zones          (20)
  //   uint16 s_magic          (24)
  //   uint16 s_pad2           (26)
  //   uint16 s_blocksize      (28)
  //   uint8  s_disk_version   (30)
  private void ParseSuperblockV3(ReadOnlySpan<byte> sb) {
    _ninodes       = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    _imapBlocks    = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));
    _zmapBlocks    = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(8));
    _firstdatazone = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(10));
    var blockSizeField = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(28));
    _blockSize = blockSizeField == 0 ? 1024 : blockSizeField;
  }

  // V1/V2 superblock layout (all LE):
  //   uint16 s_ninodes        (0)
  //   uint16 s_nzones         (2)
  //   uint16 s_imap_blocks    (4)
  //   uint16 s_zmap_blocks    (6)
  //   uint16 s_firstdatazone  (8)
  //   uint16 s_log_zone_size  (10)
  //   uint32 s_max_size       (12)
  //   uint16 s_magic          (16)
  //   uint16 s_state          (18)
  private void ParseSuperblockV1(ReadOnlySpan<byte> sb) {
    _ninodes       = BinaryPrimitives.ReadUInt16LittleEndian(sb);
    _imapBlocks    = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(4));
    _zmapBlocks    = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));
    _firstdatazone = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(8));
    _blockSize = 1024; // V1/V2 always use 1024-byte blocks
  }

  // Returns byte offset in image for a given zone number
  private long ZoneOffset(uint zone) => (long)zone * _blockSize;

  // Computes byte offset of inode table start.
  // Layout after boot block (1024) + superblock (1024):
  //   inode bitmap  (_imapBlocks * _blockSize)
  //   zone bitmap   (_zmapBlocks * _blockSize)
  //   inode table
  private long InodeTableOffset() =>
    2L * _blockSize + (long)_imapBlocks * _blockSize + (long)_zmapBlocks * _blockSize;

  // Read raw inode bytes for a 1-based inode number
  private byte[]? ReadInode(uint inodeNum) {
    if (inodeNum == 0) return null;
    var inodeSize = _version == MinixVersion.V3 ? V3InodeSize : V1InodeSize;
    var tableStart = InodeTableOffset();
    var offset = tableStart + (long)(inodeNum - 1) * inodeSize;
    if (offset + inodeSize > _data.Length) return null;
    return _data.AsSpan((int)offset, inodeSize).ToArray();
  }

  // Returns (mode, size, zones[]) from inode bytes
  private (ushort mode, uint size, uint[] zones) ParseInodeV3(byte[] inode) {
    // Real Minix3 inode layout (little-endian, 64 bytes total):
    //   uint16 i_mode   [0]
    //   uint16 i_nlinks [2]
    //   uint16 i_uid    [4]
    //   uint16 i_gid    [6]
    //   uint32 i_size   [8]
    //   uint32 i_atime  [12]
    //   uint32 i_mtime  [16]
    //   uint32 i_ctime  [20]
    //   uint32 i_zone[10] [24..63]
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(8));
    // zones at offset 24: uint32[10]
    var zones = new uint[10];
    for (var i = 0; i < 10; i++)
      zones[i] = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(24 + i * 4));
    return (mode, size, zones);
  }

  private (ushort mode, uint size, uint[] zones) ParseInodeV1(byte[] inode) {
    // V1 inode (32 bytes):
    //   uint16 mode   (0)
    //   uint16 uid    (2)
    //   uint32 size   (4)
    //   uint32 time   (8)
    //   uint8  gid    (12)
    //   uint8  nlinks (13)
    //   uint16[9] zones (14..31) — 7 direct + 1 indirect + 1 double indirect
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4));
    var zones = new uint[9];
    for (var i = 0; i < 9; i++)
      zones[i] = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(14 + i * 2));
    return (mode, size, zones);
  }

  private bool IsDirectory(ushort mode) => (mode & 0xF000) == 0x4000;
  private bool IsRegularFile(ushort mode) => (mode & 0xF000) == 0x8000;

  // Collect all data bytes for an inode (follows zone pointers)
  private byte[] ReadInodeData(uint inodeNum) {
    var inode = ReadInode(inodeNum);
    if (inode == null) return [];

    if (_version == MinixVersion.V3) {
      var (_, size, zones) = ParseInodeV3(inode);
      return ReadZones(zones, size, isV3: true);
    } else {
      var (_, size, zones) = ParseInodeV1(inode);
      return ReadZones(zones, size, isV3: false);
    }
  }

  private byte[] ReadZones(uint[] zones, uint size, bool isV3) {
    if (size == 0) return [];
    using var ms = new MemoryStream();
    var remaining = (long)size;

    var directCount = isV3 ? 7 : 7;
    var indirectIdx = directCount;
    var dindirectIdx = directCount + 1;
    var tindirectIdx = isV3 ? directCount + 2 : -1;

    // Direct zones
    for (var i = 0; i < directCount && remaining > 0; i++) {
      if (zones[i] == 0) break;
      AppendZone(ms, zones[i], ref remaining);
    }

    // Single indirect
    if (remaining > 0 && indirectIdx < zones.Length && zones[indirectIdx] != 0)
      ReadIndirect(ms, zones[indirectIdx], ref remaining, 1);

    // Double indirect
    if (remaining > 0 && dindirectIdx < zones.Length && zones[dindirectIdx] != 0)
      ReadIndirect(ms, zones[dindirectIdx], ref remaining, 2);

    // Triple indirect (V3 only)
    if (remaining > 0 && tindirectIdx >= 0 && tindirectIdx < zones.Length && zones[tindirectIdx] != 0)
      ReadIndirect(ms, zones[tindirectIdx], ref remaining, 3);

    return ms.ToArray();
  }

  private void AppendZone(MemoryStream ms, uint zone, ref long remaining) {
    var offset = ZoneOffset(zone);
    if (offset + _blockSize > _data.Length) return;
    var toRead = (int)Math.Min(remaining, _blockSize);
    ms.Write(_data, (int)offset, toRead);
    remaining -= toRead;
  }

  private void ReadIndirect(MemoryStream ms, uint indirectZone, ref long remaining, int level) {
    if (indirectZone == 0 || remaining <= 0) return;
    var offset = ZoneOffset(indirectZone);
    if (offset + _blockSize > _data.Length) return;

    var ptrsPerBlock = _blockSize / 4;
    for (var i = 0; i < ptrsPerBlock && remaining > 0; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)offset + i * 4));
      if (ptr == 0) break;
      if (level == 1)
        AppendZone(ms, ptr, ref remaining);
      else
        ReadIndirect(ms, ptr, ref remaining, level - 1);
    }
  }

  private void ReadDirectory(uint inodeNum, string path) {
    var dirData = ReadInodeData(inodeNum);
    if (dirData.Length == 0) return;

    var seen = new HashSet<uint>();

    if (_version == MinixVersion.V3) {
      // V3 dir entry: uint32 inode (4) + char[60] name (60) = 64 bytes
      const int entrySize = 64;
      for (var off = 0; off + entrySize <= dirData.Length; off += entrySize) {
        var ino = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(off));
        if (ino == 0) continue;
        var name = ReadNullTermString(dirData, off + 4, 60);
        if (name is "." or "..") continue;
        ProcessDirEntry(ino, name, path, seen);
      }
    } else {
      // V1 14-char or 30-char
      var nameLen = _version is MinixVersion.V1_30 or MinixVersion.V2_30 ? 30 : 14;
      var entrySize = 2 + nameLen;
      for (var off = 0; off + entrySize <= dirData.Length; off += entrySize) {
        var ino = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off));
        if (ino == 0) continue;
        var name = ReadNullTermString(dirData, off + 2, nameLen);
        if (name is "." or "..") continue;
        ProcessDirEntry(ino, name, path, seen);
      }
    }
  }

  private void ProcessDirEntry(uint ino, string name, string path, HashSet<uint> seen) {
    var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
    var inode = ReadInode(ino);
    if (inode == null) return;

    ushort mode;
    uint size;
    if (_version == MinixVersion.V3) {
      (mode, size, _) = ParseInodeV3(inode);
    } else {
      (mode, size, _) = ParseInodeV1(inode);
    }

    var isDir = IsDirectory(mode);
    _entries.Add(new MinixFsEntry {
      Name = fullPath,
      Size = isDir ? 0 : (int)size,
      InodeNumber = (int)ino,
      IsDirectory = isDir,
    });

    if (isDir && seen.Add(ino))
      ReadDirectory(ino, fullPath);
  }

  private static string ReadNullTermString(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return Encoding.ASCII.GetString(data, offset, end - offset);
  }

  public byte[] Extract(MinixFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];

    var inode = ReadInode((uint)entry.InodeNumber);
    if (inode == null) return [];

    uint size;
    if (_version == MinixVersion.V3) {
      (_, size, _) = ParseInodeV3(inode);
    } else {
      (_, size, _) = ParseInodeV1(inode);
    }

    var data = ReadInodeData((uint)entry.InodeNumber);
    if (data.Length > size)
      return data.AsSpan(0, (int)size).ToArray();
    return data;
  }

  public void Dispose() { }
}
