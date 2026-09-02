#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Qnx6;

/// <summary>
/// Reader for the QNX6 ("Neutrino") file system. QNX6 has a layered design:
/// two superblocks at fixed offsets (primary at 0x2000, secondary at the end
/// of the volume) for consistency checking, and a B-tree of "rootnodes"
/// pointing to inode and longfilename data.
///
/// On-disk layout (little-endian):
///   Block 0     bootblock (8 KiB reserved)
///   0x2000      primary superblock (qnx6_super_block — 512 bytes)
///   ...         inode tree, data blocks
///
/// Superblock (qnx6_super_block, linux/fs/qnx6/qnx6.h):
///   +0x00  sb_magic       u32  0x68191122
///   +0x04  sb_checksum    u32
///   +0x08  sb_serial      u64
///   +0x10  sb_ctime       u32  creation time
///   +0x14  sb_atime       u32  last mount time
///   +0x18  sb_flags       u32
///   +0x1C  sb_version1    u16
///   +0x1E  sb_version2    u16
///   +0x20  sb_volumeid    16   volume UUID
///   +0x30  sb_blocksize   u32  e.g. 1024
///   +0x34  sb_num_inodes  u32
///   +0x38  sb_free_inodes u32
///   +0x3C  sb_num_blocks  u32
///   +0x40  sb_free_blocks u32
///   +0x44  sb_num_levels  u16  tree depth
///   +0x46  sb_indir_levs  u16
///   +0x48  sb_inode_root  qnx6_root_node (40 bytes — size + 16 ptrs + 4 levels)
///
/// Inode (128 bytes per qnx6_inode_entry):
///   +0x00  di_size        u64
///   +0x08  di_uid         u32
///   +0x0C  di_gid         u32
///   +0x10  di_ftime       u32
///   +0x14  di_mtime       u32
///   +0x18  di_atime       u32
///   +0x1C  di_ctime       u32
///   +0x20  di_mode        u16
///   +0x22  di_ext_mode    u16
///   +0x24  di_block_ptr[16] u32  direct
///   +0x64  di_filelevels  u8
///   +0x65  di_status      u8
///   +0x66  di_unknown     14
///
/// Spec source: linux/fs/qnx6/{qnx6.h,super.c,inode.c,dir.c} (driver since
/// kernel 2.6.39).
/// </summary>
public sealed class Qnx6Reader : IDisposable {

  /// <summary>
  /// Random-access view over the image. Copying it into a byte[] capped the
  /// reader at the array limit, which QNX6's block addresses do not.
  /// </summary>
  private readonly ImageAccessor _data;
  private readonly List<Qnx6Entry> _entries = [];

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<Qnx6Entry> Entries => this._entries;

    /// <summary>
  /// Gets or sets the magic.
  /// </summary>
public uint Magic { get; private set; }
    /// <summary>
  /// Gets or sets the block size.
  /// </summary>
public int BlockSize { get; private set; } = 1024;

  internal const uint MagicQnx6 = 0x68191122;
  internal const int SuperblockOffset = 0x2000;
  internal const int InodeSize = 128;

    /// <summary>
  /// Initializes a new instance of <see cref="Qnx6Reader"/>.
  /// </summary>
public Qnx6Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._data = new ImageAccessor(stream, leaveOpen: true);
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < SuperblockOffset + 0x48 + 40)
      throw new InvalidDataException("QNX6: image too small for superblock.");

    var sb = this._data.Read(SuperblockOffset, 512).AsSpan();
    this.Magic = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    if (this.Magic != MagicQnx6)
      throw new InvalidDataException($"QNX6: invalid magic 0x{this.Magic:X8} (expected 0x{MagicQnx6:X8}).");

    this.BlockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x30));
    if (this.BlockSize == 0 || (this.BlockSize & (this.BlockSize - 1)) != 0)
      this.BlockSize = 1024;

    // The inode root node points at the inode table block(s). For our Stage 1
    // reader we assume the inode table sits at the first block pointer
    // (sb+0x48+8 — root_node size field is 8 bytes, then the first ptr is u32).
    // A block pointer here counts from the filesystem's own block zero, which
    // sits past the boot and superblock areas — not from the start of the
    // image.
    var inodeTablePtr = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x48 + 8));
    var inodeTableOffset = Qnx6Geometry.ByteOffsetOf(inodeTablePtr, this.BlockSize);
    if (inodeTableOffset + InodeSize > this._data.Length) return;

    // Root directory is inode 1 (after the reserved inode 0). For our
    // synthetic images we walk inode 1's first direct block as a directory
    // entry table (QNX6 directory entries are 32 bytes: u32 inode +
    // u8 name_len + 27 bytes of name).
    this.WalkDirectoryFromInode(inodeTableOffset, inodeNumber: 1, path: "");
  }

  private void WalkDirectoryFromInode(long inodeTableOffset, uint inodeNumber, string path) {
    var inodeOff = inodeTableOffset + (long)(inodeNumber - 1) * InodeSize;
    if (inodeOff + InodeSize > this._data.Length) return;
    var inode = this._data.Read(inodeOff, InodeSize).AsSpan();
    var size = BinaryPrimitives.ReadUInt64LittleEndian(inode);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode.Slice(0x20));
    var isDir = (mode & 0xF000) == 0x4000;
    if (!isDir) return;

    // Walk first direct block as directory.
    var firstBlock = this.FirstDataBlockOf(inode);
    if (firstBlock < 0) return;
    var blockOff = Qnx6Geometry.ByteOffsetOf(firstBlock, this.BlockSize);
    if (blockOff + this.BlockSize > this._data.Length) return;

    const int entrySize = 32;
    var bytesToScan = (int)Math.Min(size, (ulong)this.BlockSize);
    for (var off = 0; off + entrySize <= bytesToScan; off += entrySize) {
      var entry = this._data.Read(blockOff + off, entrySize).AsSpan();
      var childInum = BinaryPrimitives.ReadUInt32LittleEndian(entry);
      if (childInum == 0) continue;
      var nameLen = entry[4];
      if (nameLen == 0 || nameLen > 27) continue;
      var name = Encoding.ASCII.GetString(entry.Slice(5, nameLen));
      if (name is "." or "..") continue;

      var childOff = inodeTableOffset + (long)(childInum - 1) * InodeSize;
      if (childOff + InodeSize > this._data.Length) continue;
      var childInode = this._data.Read(childOff, InodeSize).AsSpan();
      var childSize = BinaryPrimitives.ReadUInt64LittleEndian(childInode);
      var childMode = BinaryPrimitives.ReadUInt16LittleEndian(childInode.Slice(0x20));
      var childIsDir = (childMode & 0xF000) == 0x4000;
      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
      this._entries.Add(new Qnx6Entry {
        Name = fullPath,
        Size = childIsDir ? 0 : (long)childSize,
        InodeNumber = childInum,
        IsDirectory = childIsDir,
      });
    }
  }

  /// <summary>
  /// Where an entry's bytes live. Files this writer emits occupy one contiguous
  /// run from the inode's first block. Returns false for a directory or an
  /// inode with no data.
  /// </summary>
  public bool TryGetDataExtent(Qnx6Entry entry, out long offset, out long length) {
    ArgumentNullException.ThrowIfNull(entry);
    offset = 0;
    length = 0;
    if (entry.IsDirectory || entry.Size <= 0) return false;
    var sb = this._data.Read(SuperblockOffset, 512).AsSpan();
    var inodeTablePtr = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x48 + 8));
    var inodeTableOffset = Qnx6Geometry.ByteOffsetOf(inodeTablePtr, this.BlockSize);
    var inodeOff = inodeTableOffset + (long)(entry.InodeNumber - 1) * InodeSize;
    if (inodeOff + InodeSize > this._data.Length) return false;
    var inode = this._data.Read(inodeOff, InodeSize).AsSpan();
    var firstBlock = this.FirstDataBlockOf(inode);
    if (firstBlock < 0) return false;
    offset = Qnx6Geometry.ByteOffsetOf(firstBlock, this.BlockSize);
    if (offset < 0 || offset >= this._data.Length) return false;
    // Whole blocks: the tail of the last one is slack, not another file's.
    length = Math.Min((entry.Size + this.BlockSize - 1) / this.BlockSize * this.BlockSize,
      this._data.Length - offset);
    return length > 0;
  }


  /// <summary>
  /// Where an inode's first block actually is.
  /// </summary>
  /// <remarks>
  /// An inode's pointers name single blocks, and when a file needs more than
  /// the sixteen an inode holds they name blocks of pointers instead — how many
  /// levels deep is in the inode. This writer lays a file down as one run, so
  /// the first block is enough to find the rest; what it is not is always
  /// pointer zero.
  /// </remarks>
  private long FirstDataBlockOf(ReadOnlySpan<byte> inode) {
    var levels = inode[0x64];
    var pointer = BinaryPrimitives.ReadUInt32LittleEndian(inode.Slice(0x24));
    for (var level = 0; level < levels; ++level) {
      var table = Qnx6Geometry.ByteOffsetOf(pointer, this.BlockSize);
      if (table < 0 || table + 4 > this._data.Length) return -1;
      pointer = BinaryPrimitives.ReadUInt32LittleEndian(this._data.Read(table, 4));
    }

    return pointer;
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(Qnx6Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var sb = this._data.Read(SuperblockOffset, 512).AsSpan();
    var inodeTablePtr = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x48 + 8));
    var inodeTableOffset = Qnx6Geometry.ByteOffsetOf(inodeTablePtr, this.BlockSize);
    var inodeOff = inodeTableOffset + (long)(entry.InodeNumber - 1) * InodeSize;
    if (inodeOff + InodeSize > this._data.Length) return [];
    var inode = this._data.Read(inodeOff, InodeSize).AsSpan();
    var firstBlock = this.FirstDataBlockOf(inode);
    if (firstBlock < 0) return [];
    var blockOff = Qnx6Geometry.ByteOffsetOf(firstBlock, this.BlockSize);
    if (blockOff < 0 || blockOff >= this._data.Length) return [];
    var take = (int)Math.Min(entry.Size, this._data.Length - blockOff);
    return this._data.Read(blockOff, take).AsSpan().ToArray();
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
