#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.SysV;

/// <summary>
/// Reader for AT&amp;T Bell Labs UNIX System V "s5fs" filesystem (1983,
/// distinguished from BSD's UFS).
///
/// On-disk layout (little-endian; documented in AT&amp;T System V Interface
/// Definition and in linux/fs/sysv/super.c):
///
///   Block 0   bootstrap   (ignored)
///   Block 1   superblock (1024 bytes at file offset 0x400)
///   Block 2.. inode list ("ilist")
///   block N.. data blocks
///
/// Superblock layout (offsets from block-start; we read the magic at +504 i.e.
/// file offset 1024+504 = 0x5F8):
///   u16 s_isize       (0)   size of ilist in blocks
///   u32 s_fsize       (2)   total blocks in volume
///   u16 s_nfree       (6)   number of free blocks in inline cache
///   u32 s_free[50]    (8)   free-block cache (208 bytes)
///   u16 s_ninode      (216) number of free inodes in inline cache
///   u16 s_inode[100]  (218) free-inode cache
///   u8  s_flock       (418)
///   u8  s_ilock       (419)
///   u8  s_fmod        (420)
///   u8  s_ronly       (421)
///   u32 s_time        (422) timestamp
///   ...
///   u32 s_magic       (504) magic number 0xFD187E20 for s5fs
///   u32 s_type        (508) block-size code: 1=512B, 2=1024B, 3=2048B
///
/// Inode (64 bytes — System V uses 64-byte inodes, larger than Minix's 32):
///   u16 di_mode       (0)
///   u16 di_nlink      (2)
///   u16 di_uid        (4)
///   u16 di_gid        (6)
///   u32 di_size       (8)
///   u8  di_addr[40]   (12)  thirteen 3-byte block addresses (10 direct,
///                            1 indirect, 1 double-indirect, 1 triple-indirect)
///   u32 di_atime      (52)
///   u32 di_mtime      (56)
///   u32 di_ctime      (60)
///
/// Directory entries are 16 bytes (ino:u16, name:14). Root inode is inode 2.
/// </summary>
public sealed class SysVReader : IDisposable {

  private readonly byte[] _data;
  private readonly List<SysVEntry> _entries = [];

  public IReadOnlyList<SysVEntry> Entries => this._entries;

  public uint Magic { get; private set; }
  public int BlockSize { get; private set; }
  public ushort IListBlocks { get; private set; }

  // Constants
  private const int SuperblockOffset = 512;
  internal const int InodeSize = 64;
  internal const uint MagicSysV = 0xFD187E20;
  internal const int RootInode = 2;

  public SysVReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < SuperblockOffset + 512)
      throw new InvalidDataException("SysV: image too small for superblock.");

    var sb = this._data.AsSpan(SuperblockOffset);
    this.Magic = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(504));
    if (this.Magic != MagicSysV)
      throw new InvalidDataException($"SysV: invalid magic 0x{this.Magic:X8} (expected 0x{MagicSysV:X8}).");

    var typeCode = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(508));
    this.BlockSize = typeCode switch {
      1 => 512,
      2 => 1024,
      3 => 2048,
      _ => 1024,
    };
    this.IListBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(0));

    this.ReadDirectory(RootInode, "");
  }

  internal long BlockOffset(uint block) => (long)block * this.BlockSize;

  // ilist starts at block 2 regardless of block size.
  internal long InodeTableOffset() => 2L * this.BlockSize;

  private byte[]? ReadInode(uint inum) {
    if (inum == 0) return null;
    var offset = this.InodeTableOffset() + (long)(inum - 1) * InodeSize;
    if (offset < 0 || offset + InodeSize > this._data.Length) return null;
    return this._data.AsSpan((int)offset, InodeSize).ToArray();
  }

  // 24-bit block addresses are stored as 3 bytes; sysv uses a quirky
  // PDP-11/VAX byte ordering (low-mid-high) per
  // linux/fs/sysv/sysv.h __fs32 helpers — most variants are little-endian
  // 24-bit, which is what we implement.
  private static uint Read24(ReadOnlySpan<byte> s) =>
    s[0] | ((uint)s[1] << 8) | ((uint)s[2] << 16);

  private static (ushort mode, uint size, uint[] zones) ParseInode(byte[] inode) {
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(8));
    var zones = new uint[13];
    for (var i = 0; i < 13; i++)
      zones[i] = Read24(inode.AsSpan(12 + i * 3));
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

    // 10 direct
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
    var ptrsPerBlock = this.BlockSize / 3; // 24-bit ptrs
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
    this._entries.Add(new SysVEntry {
      Name = fullPath,
      Size = isDir ? 0 : size,
      InodeNumber = (int)ino,
      IsDirectory = isDir,
    });
    if (isDir && seen.Add(ino))
      this.ReadDirectory(ino, fullPath);
  }

  private static string ReadNullTermString(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return Encoding.ASCII.GetString(data, offset, end - offset);
  }

  /// <summary>
  /// Where on disk everything the volume holds actually sits: each file's data
  /// blocks under its name, and every directory block and indirect block as
  /// structure, with the byte offset of the pointer that names each run.
  /// </summary>
  /// <remarks>
  /// This filesystem tracks its free space with a chained cache in the
  /// superblock rather than a bitmap, so what is taken can only be answered by
  /// walking the inodes. Doing so also answers by whom, which is what anything
  /// moving a file needs.
  /// </remarks>
  public IEnumerable<(long Offset, long Length, long PointerOffset, string? Owner)> EnumerateLayout() {
    // The root's own blocks belong to nobody in the entry list — it is what the
    // list was walked from — so it is claimed first. Leaving it out reported
    // the root directory as space nothing holds, and a wipe then zeroed it.
    var inodes = new List<(int Number, bool IsDirectory, string Name)> { (RootInode, true, "") };
    foreach (var entry in this._entries)
      inodes.Add((entry.InodeNumber, entry.IsDirectory, entry.Name));

    foreach (var (inodeNumber, isDirectory, name) in inodes) {
      var inode = this.ReadInode((uint)inodeNumber);
      if (inode == null) continue;
      var (_, size, zones) = ParseInode(inode);
      if (size == 0) continue;

      var inodeOffset = this.InodeTableOffset() + (long)(inodeNumber - 1) * InodeSize;
      var owner = isDirectory ? null : name;
      var remaining = (long)size;

      var runFirstBlock = 0u;
      var runPointer = -1L;
      var runBlocks = 0;

      foreach (var (block, pointerOffset, isPointerBlock) in this.EnumerateBlockPointers(zones, inodeOffset)) {
        // An indirect block is the volume's own bookkeeping wherever it sits,
        // and it is never part of a file's run.
        if (isPointerBlock) {
          yield return (this.BlockOffset(block), this.BlockSize, -1, null);
          continue;
        }

        if (remaining <= 0) break;
        remaining -= this.BlockSize;

        // A run continues only while the blocks stay consecutive and so do the
        // pointers naming them: a move rewrites the pointers in order, so a gap
        // in either would put the wrong blocks under the wrong pointers.
        if (runPointer >= 0 && block == runFirstBlock + runBlocks
            && pointerOffset == runPointer + (long)runBlocks * 3) {
          ++runBlocks;
          continue;
        }

        if (runPointer >= 0)
          yield return (this.BlockOffset(runFirstBlock), (long)runBlocks * this.BlockSize, runPointer, owner);

        runFirstBlock = block;
        runPointer = pointerOffset;
        runBlocks = 1;
      }

      if (runPointer >= 0)
        yield return (this.BlockOffset(runFirstBlock), (long)runBlocks * this.BlockSize, runPointer, owner);
    }
  }

  /// <summary>
  /// The blocks a file's pointers name, in order, each with the byte offset of
  /// the pointer itself and whether the block is a pointer block rather than
  /// data.
  /// </summary>
  private IEnumerable<(uint Block, long PointerOffset, bool IsPointerBlock)> EnumerateBlockPointers(
      uint[] zones, long inodeOffset) {
    for (var i = 0; i < 10; ++i) {
      if (zones[i] == 0) yield break;
      yield return (zones[i], inodeOffset + 12 + (long)i * 3, false);
    }

    for (var level = 1; level <= 3; ++level) {
      var root = zones[9 + level];
      if (root == 0) continue;
      yield return (root, -1, true);
      foreach (var triple in this.EnumerateIndirectPointers(root, level))
        yield return triple;
    }
  }

  /// <summary>The blocks an indirect block names, descending as many levels as asked.</summary>
  private IEnumerable<(uint Block, long PointerOffset, bool IsPointerBlock)> EnumerateIndirectPointers(
      uint block, int levels) {
    var blockOffset = this.BlockOffset(block);
    if (blockOffset < 0 || blockOffset + this.BlockSize > this._data.Length) yield break;

    for (var i = 0; i < this.BlockSize / 4; ++i) {
      var at = blockOffset + (long)i * 4;
      var pointer = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan((int)at, 4));
      if (pointer == 0) continue;
      if (levels <= 1) {
        yield return (pointer, at, false);
        continue;
      }
      yield return (pointer, -1, true);
      foreach (var triple in this.EnumerateIndirectPointers(pointer, levels - 1))
        yield return triple;
    }
  }

  /// <summary>Bytes a pointer to a data block occupies inside an inode.</summary>
  public const int InodePointerBytes = 3;

  /// <summary>Bytes a pointer occupies inside an indirect block.</summary>
  public const int IndirectPointerBytes = 4;

  /// <summary>
  /// First byte a file may occupy. <c>s_isize</c> is the number of the first
  /// data block, not how many blocks the inode list spans — reading it the
  /// other way put the boundary past the root directory and the first file,
  /// and everything below it was then reported as the volume's own.
  /// </summary>
  public long FirstDataByte => (long)this.IListBlocks * this.BlockSize;

  public byte[] Extract(SysVEntry entry) {
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
