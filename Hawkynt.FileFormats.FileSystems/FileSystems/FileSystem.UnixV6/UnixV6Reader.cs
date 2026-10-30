#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.UnixV6;

/// <summary>One file or directory of a V6 volume.</summary>
public sealed record UnixV6Entry(string Name, long Size, bool IsDirectory, DateTime? LastModified, int Inode);

/// <summary>
/// Reads Sixth Edition UNIX volumes (PDP-11, 1975): 512-byte blocks, the
/// superblock (<c>struct filsys</c>) in block 1, 32-byte inodes from block 2 for
/// <c>s_isize</c> blocks, 16-byte directory entries, root inode 1.
/// <para>
/// A small file addresses up to eight data blocks directly. A large file
/// (<c>ILARG</c>) uses <c>i_addr[0..6]</c> as single-indirect blocks of 256 block
/// numbers and <c>i_addr[7]</c> as a double-indirect block for logical blocks from
/// 1792 on, the arithmetic of the V6 kernel's <c>bmap</c>. Sizes are 24 bits
/// (<c>i_size0</c>:<c>i_size1</c>); times are PDP-11 longs (high word first).
/// </para>
/// <para>References: V6 <c>/usr/sys/filsys.h</c>, <c>inode.h</c>, <c>ken/subr.c</c>
/// (TUHS); Lions, <i>A Commentary on the Sixth Edition UNIX Operating System</i>.</para>
/// </summary>
public sealed class UnixV6Reader : IDisposable {
  internal const int BlockSize = 512;
  internal const int InodeSize = 32;
  internal const int RootInode = 1;
  internal const int EntrySize = 16;
  internal const int NameLength = 14;
  private const ushort IAlloc = 0x8000;   // 0100000
  private const ushort IFmt = 0x6000;     // 060000
  private const ushort IFDir = 0x4000;    // 040000
  private const ushort ILarg = 0x1000;    // 010000
  private const int MaxDepth = 128;

  private readonly ImageAccessor _img;
  private readonly List<UnixV6Entry> _entries = [];
  private readonly HashSet<int> _visited = [];
  private readonly int _inodeCount;
  private readonly int _volumeBlocks;

  public IReadOnlyList<UnixV6Entry> Entries => _entries;

  /// <summary>Size of the volume in blocks (<c>s_fsize</c>).</summary>
  public int VolumeBlocks => _volumeBlocks;

  public UnixV6Reader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    _img = new ImageAccessor(stream, leaveOpen);
    if (_img.Length < 3 * BlockSize) throw new InvalidDataException("UNIX V6: image too small for a superblock and an i-list.");
    var sb = _img.Read(BlockSize, BlockSize);
    if (Check(sb, _img.Length, _img.Read(2 * BlockSize, InodeSize)) is { } problem)
      throw new InvalidDataException("UNIX V6: " + problem);
    _inodeCount = BinaryPrimitives.ReadUInt16LittleEndian(sb) * (BlockSize / InodeSize);
    _volumeBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(2));
    ReadDirectory(RootInode, "", 0);
  }

  /// <summary>Returns why the superblock and root inode do not describe a V6 volume, or null.</summary>
  internal static string? Check(ReadOnlySpan<byte> sb, long imageLength, ReadOnlySpan<byte> root) {
    var isize = BinaryPrimitives.ReadUInt16LittleEndian(sb);
    var fsize = BinaryPrimitives.ReadUInt16LittleEndian(sb[2..]);
    var nfree = BinaryPrimitives.ReadInt16LittleEndian(sb[4..]);
    var ninode = BinaryPrimitives.ReadInt16LittleEndian(sb[206..]);
    if (nfree is < 0 or > 100) return $"s_nfree {nfree} outside 0..100";
    if (ninode is < 0 or > 100) return $"s_ninode {ninode} outside 0..100";
    if (isize == 0 || fsize <= isize + 2) return $"s_isize {isize} / s_fsize {fsize} implausible";
    if ((isize + 2L) * BlockSize > imageLength) return "the i-list runs past the end of the image";
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(root);
    var size = (root[5] << 16) | BinaryPrimitives.ReadUInt16LittleEndian(root[6..]);
    if ((mode & (IAlloc | IFmt)) != (IAlloc | IFDir)) return "inode 1 is not an allocated directory";
    if (size == 0 || size % EntrySize != 0) return "the root directory size is not a whole number of entries";
    return null;
  }

  private long InodeOffset(int ino) => 2L * BlockSize + (long)(ino - 1) * InodeSize;

  private void ReadDirectory(int ino, string basePath, int depth) {
    if (depth > MaxDepth || !_visited.Add(ino)) return;
    byte[] data;
    try {
      data = ReadData(ino);
    } catch (InvalidDataException) {
      return; // a directory with blocks outside the image lists as empty
    }
    for (var pos = 0; pos + EntrySize <= data.Length; pos += EntrySize) {
      var child = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
      if (child == 0 || child > _inodeCount) continue;
      var raw = data.AsSpan(pos + 2, NameLength);
      var nul = raw.IndexOf((byte)0);
      var name = Encoding.Latin1.GetString(nul < 0 ? raw : raw[..nul]);
      if (name.Length == 0 || name is "." or ".." || name.Contains('/')) continue;

      var inode = _img.Read(InodeOffset(child), InodeSize);
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
      if ((mode & IAlloc) == 0) continue;
      var type = mode & IFmt;
      if (type is not (0 or IFDir)) continue; // character and block devices carry no data
      var isDir = type == IFDir;
      var size = (inode[5] << 16) | BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(6));
      var mtime = (BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(28)) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(30));
      var path = basePath.Length == 0 ? name : basePath + "/" + name;
      _entries.Add(new UnixV6Entry(path, isDir ? 0 : size, isDir,
        mtime > 0 ? DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime : null, child));
      if (isDir) ReadDirectory(child, path, depth + 1);
    }
  }

  public byte[] Extract(UnixV6Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.IsDirectory ? [] : ReadData(entry.Inode);
  }

  private byte[] ReadData(int ino) {
    var inode = _img.Read(InodeOffset(ino), InodeSize);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var size = (inode[5] << 16) | BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(6));
    var large = (mode & ILarg) != 0;
    var addr = new int[8];
    for (var i = 0; i < 8; ++i) addr[i] = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(8 + i * 2));

    var blocks = (size + BlockSize - 1) / BlockSize;
    if (!large && blocks > 8) throw new InvalidDataException($"UNIX V6: small inode {ino} declares {size} bytes, more than eight blocks.");
    var result = new byte[size];
    for (var bn = 0; bn < blocks; ++bn) {
      int nb;
      if (!large) nb = addr[bn];
      else {
        var i = bn >> 8;
        if (i < 7) nb = Pointer(addr[i], bn & 0xFF);
        else nb = Pointer(Pointer(addr[7], i - 7), bn & 0xFF);
      }
      if (nb == 0) continue; // a hole reads as zeros
      var count = Math.Min(BlockSize, size - bn * BlockSize);
      Block(nb, result.AsSpan(bn * BlockSize, count));
    }
    return result;
  }

  private int Pointer(int block, int slot) {
    if (block == 0) return 0;
    Span<byte> two = stackalloc byte[2];
    Block(block, two, slot * 2);
    return BinaryPrimitives.ReadUInt16LittleEndian(two);
  }

  private void Block(int block, Span<byte> destination, int within = 0) {
    var offset = (long)block * BlockSize + within;
    if (block >= _volumeBlocks || offset + destination.Length > _img.Length)
      throw new InvalidDataException($"UNIX V6: block {block} lies outside the volume.");
    _img.Read(offset, destination);
  }

  public void Dispose() => _img.Dispose();
}
