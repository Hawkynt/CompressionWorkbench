#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixV2;

/// <summary>
/// Reads Minix v2 filesystem images (1991). v2 extended the original
/// Minix layout to support large files: 64-byte inodes (replacing v1's
/// 32-byte), 32-bit zone numbers, and a triple-indirect block. The
/// superblock layout is the same as v1 (1024-byte blocks). Magic 0x2468
/// (14-byte names) or 0x2478 (30-byte names).
/// </summary>
public sealed class MinixV2Reader : IDisposable {
  private readonly byte[] _data;
  private readonly List<MinixV2Entry> _entries = [];

  public IReadOnlyList<MinixV2Entry> Entries => _entries;

  public ushort Magic { get; private set; }
  public int NameLength { get; private set; }

  private ushort _imapBlocks;
  private ushort _zmapBlocks;
  private const int BlockSize = 1024;
  private const int SuperblockOffset = 1024;
  private const int InodeSize = 64;

  private const ushort MagicV2_14 = 0x2468;
  private const ushort MagicV2_30 = 0x2478;

  public MinixV2Reader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 32)
      throw new InvalidDataException("MinixV2: image too small for superblock.");

    var sb = _data.AsSpan(SuperblockOffset);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(16));
    if (magic != MagicV2_14 && magic != MagicV2_30)
      throw new InvalidDataException($"MinixV2: invalid magic 0x{magic:X4} at superblock+16 (expected 0x2468 or 0x2478).");

    this.Magic = magic;
    this.NameLength = magic == MagicV2_30 ? 30 : 14;

    _imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(4));
    _zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));

    ReadDirectory(1, "");
  }

  private long ZoneOffset(uint zone) => (long)zone * BlockSize;

  private long InodeTableOffset() =>
    2L * BlockSize + (long)_imapBlocks * BlockSize + (long)_zmapBlocks * BlockSize;

  private byte[]? ReadInode(uint inodeNum) {
    if (inodeNum == 0) return null;
    var offset = InodeTableOffset() + (long)(inodeNum - 1) * InodeSize;
    if (offset + InodeSize > _data.Length) return null;
    return _data.AsSpan((int)offset, InodeSize).ToArray();
  }

  // V2 inode (64 bytes):
  //   u16 mode      (0)
  //   u16 nlinks    (2)
  //   u16 uid       (4)
  //   u16 gid       (6)
  //   u32 size      (8)
  //   u32 atime     (12)
  //   u32 mtime     (16)
  //   u32 ctime     (20)
  //   u32[10] zones (24..63) — 7 direct + 1 ind + 1 dind + 1 tind
  private static (ushort mode, uint size, uint[] zones) ParseInode(byte[] inode) {
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(8));
    var zones = new uint[10];
    for (var i = 0; i < 10; i++)
      zones[i] = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(24 + i * 4));
    return (mode, size, zones);
  }

  private static bool IsDirectory(ushort mode) => (mode & 0xF000) == 0x4000;

  private byte[] ReadInodeData(uint inodeNum) {
    var inode = ReadInode(inodeNum);
    if (inode == null) return [];
    var (_, size, zones) = ParseInode(inode);
    return ReadZones(zones, size);
  }

  private byte[] ReadZones(uint[] zones, uint size) {
    if (size == 0) return [];
    using var ms = new MemoryStream();
    var remaining = (long)size;

    for (var i = 0; i < 7 && remaining > 0; i++) {
      if (zones[i] == 0) break;
      AppendZone(ms, zones[i], ref remaining);
    }

    if (remaining > 0 && zones[7] != 0)
      ReadIndirect(ms, zones[7], ref remaining, 1);
    if (remaining > 0 && zones[8] != 0)
      ReadIndirect(ms, zones[8], ref remaining, 2);
    if (remaining > 0 && zones[9] != 0)
      ReadIndirect(ms, zones[9], ref remaining, 3);

    return ms.ToArray();
  }

  private void AppendZone(MemoryStream ms, uint zone, ref long remaining) {
    var offset = ZoneOffset(zone);
    if (offset + BlockSize > _data.Length) return;
    var toRead = (int)Math.Min(remaining, BlockSize);
    ms.Write(_data, (int)offset, toRead);
    remaining -= toRead;
  }

  private void ReadIndirect(MemoryStream ms, uint indirectZone, ref long remaining, int level) {
    if (indirectZone == 0 || remaining <= 0) return;
    var offset = ZoneOffset(indirectZone);
    if (offset + BlockSize > _data.Length) return;

    var ptrsPerBlock = BlockSize / 4; // 32-bit zone pointers in V2
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
    var entrySize = 2 + this.NameLength;
    for (var off = 0; off + entrySize <= dirData.Length; off += entrySize) {
      var ino = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off));
      if (ino == 0) continue;
      var name = ReadNullTermString(dirData, off + 2, this.NameLength);
      if (name is "." or "..") continue;
      ProcessDirEntry(ino, name, path, seen);
    }
  }

  private void ProcessDirEntry(uint ino, string name, string path, HashSet<uint> seen) {
    var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
    var inode = ReadInode(ino);
    if (inode == null) return;

    var (mode, size, _) = ParseInode(inode);
    var isDir = IsDirectory(mode);
    _entries.Add(new MinixV2Entry {
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

  public byte[] Extract(MinixV2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];

    var inode = ReadInode((uint)entry.InodeNumber);
    if (inode == null) return [];

    var (_, size, _) = ParseInode(inode);
    var data = ReadInodeData((uint)entry.InodeNumber);
    if (data.Length > size)
      return data.AsSpan(0, (int)size).ToArray();
    return data;
  }

  public void Dispose() { }
}
