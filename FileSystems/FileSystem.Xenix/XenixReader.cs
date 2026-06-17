#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Xenix;

/// <summary>
/// Reader for Microsoft / SCO Xenix System V file system (1980-1989, Microsoft's
/// licensed UNIX). Xenix is a System V variant with two superblock structures
/// ("Xenix-4 V" and "Xenix-5 V"); we target the more common Xenix V (3/V) layout.
///
/// On-disk layout (little-endian, 1024-byte blocks by default — adjustable via
/// s_type at sb+508):
///   Block 0      bootstrap
///   Block 1      superblock @ file offset 1024
///   Block 2..    inode table (64-byte inodes, 10 direct + 1+2+3 indirect ptrs
///                stored as 3-byte addresses)
///   data blocks  follow
///
/// Superblock field of interest:
///   u32 s_magic       (sb+504) 0xFD187E20  (same magic as s5fs — distinguished
///                                            from SysV/Coherent by extension)
///   u32 s_type        (sb+508) 1=512B/2=1024B/3=2048B blocks
///
/// Root inode = 2. Directory entry: u16 inode + 14-char name.
///
/// Spec source: SCO XENIX System V Programmer's Reference (1989) Appendix C;
/// Linux kernel fs/sysv/super.c which historically mounted Xenix volumes via
/// the sysv driver.
/// </summary>
public sealed class XenixReader : IDisposable {

  private readonly byte[] _data;
  private readonly List<XenixEntry> _entries = [];

  public IReadOnlyList<XenixEntry> Entries => this._entries;
  public uint Magic { get; private set; }
  public int BlockSize { get; private set; } = 1024;

  internal const int SuperblockOffset = 1024;
  private const int InodeSize = 64;
  // Genuine Xenix superblock magic (s_magic @ struct offset 0x3F8 = 0x2B5544),
  // as written by mkfs.xenix and matched verbatim by the Linux sysv driver's
  // detect_xenix(); s_type at 0x3FC selects the block size.
  internal const uint MagicXenix = 0x002B5544;
  internal const int MagicOffset = 0x3F8;
  internal const int TypeOffset = 0x3FC;
  private const int RootInode = 2;

  public XenixReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < SuperblockOffset + 1024)
      throw new InvalidDataException("Xenix: image too small for superblock.");
    var sb = this._data.AsSpan(SuperblockOffset);
    this.Magic = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(MagicOffset));
    if (this.Magic != MagicXenix)
      throw new InvalidDataException($"Xenix: invalid magic 0x{this.Magic:X8} (expected 0x{MagicXenix:X8}).");
    var typeCode = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(TypeOffset));
    this.BlockSize = typeCode switch {
      1 => 512,
      2 => 1024,
      3 => 2048,
      _ => 1024,
    };
    this.ReadDirectory(RootInode, "");
  }

  internal long BlockOffset(uint block) => (long)block * this.BlockSize;
  internal long InodeTableOffset() => 2L * this.BlockSize;

  private byte[]? ReadInode(uint inum) {
    if (inum == 0) return null;
    var offset = this.InodeTableOffset() + (long)(inum - 1) * InodeSize;
    if (offset < 0 || offset + InodeSize > this._data.Length) return null;
    return this._data.AsSpan((int)offset, InodeSize).ToArray();
  }

  private static uint Read24(ReadOnlySpan<byte> s) =>
    s[0] | ((uint)s[1] << 8) | ((uint)s[2] << 16);

  private static (ushort mode, uint size, uint[] zones) ParseInode(byte[] inode) {
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(8));
    var zones = new uint[13];
    for (var i = 0; i < 13; i++) zones[i] = Read24(inode.AsSpan(12 + i * 3));
    return (mode, size, zones);
  }

  private static bool IsDirectory(ushort mode) => (mode & 0xF000) == 0x4000;

  private byte[] ReadInodeData(uint inum) {
    var inode = this.ReadInode(inum);
    if (inode == null) return [];
    var (_, size, zones) = ParseInode(inode);
    return this.ReadZones(zones, size);
  }

  private byte[] ReadZones(uint[] zones, uint size) {
    if (size == 0) return [];
    using var ms = new MemoryStream();
    long remaining = size;
    for (var i = 0; i < 10 && remaining > 0; i++) {
      if (zones[i] == 0) break;
      this.AppendBlock(ms, zones[i], ref remaining);
    }
    if (remaining > 0 && zones[10] != 0) this.ReadIndirect(ms, zones[10], ref remaining, 1);
    if (remaining > 0 && zones[11] != 0) this.ReadIndirect(ms, zones[11], ref remaining, 2);
    if (remaining > 0 && zones[12] != 0) this.ReadIndirect(ms, zones[12], ref remaining, 3);
    return ms.ToArray();
  }

  private void AppendBlock(MemoryStream ms, uint block, ref long remaining) {
    var offset = this.BlockOffset(block);
    if (offset < 0 || offset + this.BlockSize > this._data.Length) return;
    var toRead = (int)Math.Min(remaining, this.BlockSize);
    ms.Write(this._data, (int)offset, toRead);
    remaining -= toRead;
  }

  private void ReadIndirect(MemoryStream ms, uint indirectBlock, ref long remaining, int level) {
    if (indirectBlock == 0 || remaining <= 0) return;
    var offset = this.BlockOffset(indirectBlock);
    if (offset + this.BlockSize > this._data.Length) return;
    var ptrsPerBlock = this.BlockSize / 3;
    for (var i = 0; i < ptrsPerBlock && remaining > 0; i++) {
      var ptr = Read24(this._data.AsSpan((int)offset + i * 3));
      if (ptr == 0) break;
      if (level == 1) this.AppendBlock(ms, ptr, ref remaining);
      else this.ReadIndirect(ms, ptr, ref remaining, level - 1);
    }
  }

  private void ReadDirectory(uint inum, string path) {
    var dirData = this.ReadInodeData(inum);
    if (dirData.Length == 0) return;
    var seen = new HashSet<uint>();
    const int entrySize = 16;
    for (var off = 0; off + entrySize <= dirData.Length; off += entrySize) {
      var ino = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off));
      if (ino == 0) continue;
      var name = ReadNullTermString(dirData, off + 2, 14);
      if (name is "." or "..") continue;
      this.ProcessDirEntry(ino, name, path, seen);
    }
  }

  private void ProcessDirEntry(uint ino, string name, string path, HashSet<uint> seen) {
    var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
    var inode = this.ReadInode(ino);
    if (inode == null) return;
    var (mode, size, _) = ParseInode(inode);
    var isDir = IsDirectory(mode);
    this._entries.Add(new XenixEntry {
      Name = fullPath,
      Size = isDir ? 0 : size,
      InodeNumber = (int)ino,
      IsDirectory = isDir,
    });
    if (isDir && seen.Add(ino)) this.ReadDirectory(ino, fullPath);
  }

  private static string ReadNullTermString(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return Encoding.ASCII.GetString(data, offset, end - offset);
  }

  public byte[] Extract(XenixEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var inode = this.ReadInode((uint)entry.InodeNumber);
    if (inode == null) return [];
    var (_, size, _) = ParseInode(inode);
    var data = this.ReadInodeData((uint)entry.InodeNumber);
    if (data.Length > size) return data.AsSpan(0, (int)size).ToArray();
    return data;
  }

  public void Dispose() { }
}
