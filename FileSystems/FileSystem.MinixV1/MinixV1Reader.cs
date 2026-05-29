#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixV1;

/// <summary>
/// Reads original Minix v1 filesystem images (1987, Tanenbaum's
/// "Operating Systems: Design and Implementation"). The v1 layout uses
/// 16-bit zone numbers, 16-bit inode counts, 32-byte inodes (7 direct +
/// 1 indirect + 1 double-indirect zone pointer), and 1024-byte blocks.
/// Two magic flavors: 0x137F (14-byte directory names) and 0x138F
/// (30-byte directory names — Coherent / Minix patched variant).
/// </summary>
public sealed class MinixV1Reader : IDisposable {
  private readonly byte[] _data;
  private readonly List<MinixV1Entry> _entries = [];

  public IReadOnlyList<MinixV1Entry> Entries => _entries;

  public ushort Magic { get; private set; }
  public int NameLength { get; private set; }

  private ushort _imapBlocks;
  private ushort _zmapBlocks;
  private const int BlockSize = 1024;
  private const int SuperblockOffset = 1024;
  private const int InodeSize = 32;

  private const ushort MagicV1_14 = 0x137F;
  private const ushort MagicV1_30 = 0x138F;

  public MinixV1Reader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 32)
      throw new InvalidDataException("MinixV1: image too small for superblock.");

    var sb = _data.AsSpan(SuperblockOffset);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(16));
    if (magic != MagicV1_14 && magic != MagicV1_30)
      throw new InvalidDataException($"MinixV1: invalid magic 0x{magic:X4} at superblock+16 (expected 0x137F or 0x138F).");

    this.Magic = magic;
    this.NameLength = magic == MagicV1_30 ? 30 : 14;

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

  // V1 inode (32 bytes):
  //   u16 mode   (0)
  //   u16 uid    (2)
  //   u32 size   (4)
  //   u32 time   (8)
  //   u8  gid    (12)
  //   u8  nlinks (13)
  //   u16[9] zones (14..31) — 7 direct + 1 indirect + 1 double indirect
  private static (ushort mode, uint size, uint[] zones) ParseInode(byte[] inode) {
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4));
    var zones = new uint[9];
    for (var i = 0; i < 9; i++)
      zones[i] = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(14 + i * 2));
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

    // 7 direct zones
    for (var i = 0; i < 7 && remaining > 0; i++) {
      if (zones[i] == 0) break;
      AppendZone(ms, zones[i], ref remaining);
    }

    // single indirect
    if (remaining > 0 && zones[7] != 0)
      ReadIndirect(ms, zones[7], ref remaining, 1);

    // double indirect
    if (remaining > 0 && zones[8] != 0)
      ReadIndirect(ms, zones[8], ref remaining, 2);

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

    var ptrsPerBlock = BlockSize / 2; // 16-bit zone pointers in V1
    for (var i = 0; i < ptrsPerBlock && remaining > 0; i++) {
      var ptr = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan((int)offset + i * 2));
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
    _entries.Add(new MinixV1Entry {
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

  public byte[] Extract(MinixV1Entry entry) {
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
