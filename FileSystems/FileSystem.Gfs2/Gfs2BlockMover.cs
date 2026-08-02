#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.Gfs2;

/// <summary>
/// Moves a file's blocks inside a GFS2 volume, repoints the tree pointers that
/// name them, and moves the allocation with them.
/// </summary>
/// <remarks>
/// <para>A GFS2 file's out-of-line bytes are addressed one block at a time by
/// eight-byte pointers, either in the dinode itself or in the indirect blocks
/// below it. Moving a run of them is the copy, the pointers that named it, and
/// the two bits per block in the resource group that say whether the block is
/// taken. Without the bitmap half, the next file added would be allocated
/// straight on top of one that had moved.</para>
///
/// <para>A run is only ever reported when its blocks and its pointers are both
/// consecutive, which is what lets the whole run be repointed by counting
/// forward from the first.</para>
/// </remarks>
public sealed class Gfs2BlockMover : IFilesystemBlockMover {

  private const uint MetaMagic = 0x01161970u;
  private const uint MetaTypeRg = 2;
  private const int RgrpHeaderBytes = 128;
  private const int RbHeaderBytes = 24;

  /// <summary>Bitmap state of a block holding file data.</summary>
  private const int StateUsed = 1;

  /// <summary>Bitmap state of a block nothing holds.</summary>
  private const int StateFree = 0;

  private long _blockSize;
  private long _firstDataByte;
  private readonly List<(long Header, long Data0, long Data)> _groups = [];

  /// <summary>Reads the geometry and walks the resource groups.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    image.Position = 0;
    using var accessor = new ImageAccessor(image);
    var reader = new Gfs2Reader(image);
    this._blockSize = reader.BlockSize;
    if (this._blockSize < 512)
      throw new InvalidDataException("GFS2: the superblock does not name a usable block size.");

    var volumeBlocks = accessor.Length / this._blockSize;
    this._groups.Clear();

    var header = FindFirstRgrp(accessor, this._blockSize, volumeBlocks);
    if (header < 0)
      throw new InvalidDataException("GFS2: no resource group could be found.");

    var guard = 0;
    while (header >= 0 && header < volumeBlocks && guard++ < 1_000_000) {
      var head = accessor.Read(header * this._blockSize, RgrpHeaderBytes);
      if (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0, 4)) != MetaMagic) break;
      if (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4, 4)) != MetaTypeRg) break;

      var skip = (long)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(36, 4));
      var span = skip > 0 ? skip : volumeBlocks - header;
      if (span <= 1) break;

      var riLength = ResolveHeaderBlocks(span, this._blockSize);
      if (riLength <= 0 || riLength >= span) break;
      this._groups.Add((header, header + riLength, span - riLength));

      if (skip == 0) break;
      header += skip;
    }

    var first = long.MaxValue;
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      foreach (var (offset, length, _) in reader.EnumerateDataExtents(entry))
        if (length > 0) first = Math.Min(first, offset);
    }
    this._firstDataByte = first == long.MaxValue
      ? (this._groups.Count > 0 ? this._groups[0].Data0 * this._blockSize : this._blockSize)
      : first;
  }

  /// <summary>Block size in bytes, as the superblock records it.</summary>
  public int BlockSize => (int)this._blockSize;

  /// <summary>First byte a file may occupy: past the structures and the group bitmaps.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._blockSize == 0) this.Init(image);

    if (newOffset % this._blockSize != 0)
      throw new NotSupportedException(
        $"GFS2: {newOffset} is not on a {this._blockSize}-byte block boundary, which is all a " +
        "tree pointer can name.");

    var oldBlock = oldOffset / this._blockSize;
    var newBlock = newOffset / this._blockSize;
    if (oldBlock == newBlock) return;

    var blocks = (int)((length + this._blockSize - 1) / this._blockSize);
    var pointerOffset = this.FindPointerNaming(image, oldOffset, blocks);
    if (pointerOffset < 0)
      throw new InvalidOperationException(
        $"GFS2: no pointer run names block {oldBlock}, so '{fileName}' cannot be repointed.");

    Span<byte> pointer = stackalloc byte[8];
    for (var i = 0; i < blocks; ++i) {
      BinaryPrimitives.WriteUInt64BigEndian(pointer, (ulong)(newBlock + i));
      image.Position = pointerOffset + (long)i * 8;
      image.Write(pointer);
    }

    // The group bitmaps say which blocks are taken; leaving them behind would
    // let the next file added be allocated straight on top of this one.
    for (var i = 0; i < blocks; ++i) {
      this.SetState(image, oldBlock + i, StateFree);
      this.SetState(image, newBlock + i, StateUsed);
    }
    image.Flush();
  }

  /// <summary>
  /// The byte offset of the first pointer of the run that starts at
  /// <paramref name="offset" /> and covers <paramref name="blocks" />, or -1.
  /// </summary>
  private long FindPointerNaming(Stream image, long offset, int blocks) {
    image.Position = 0;
    var reader = new Gfs2Reader(image);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      foreach (var (runOffset, runLength, pointerOffset) in reader.EnumerateDataExtents(entry)) {
        if (runOffset != offset) continue;
        if (runLength < (long)blocks * this._blockSize) continue;
        return pointerOffset;
      }
    }
    return -1;
  }

  /// <summary>Writes a block's two-bit allocation state into its group's bitmap.</summary>
  private void SetState(Stream image, long block, int state) {
    foreach (var (header, data0, data) in this._groups) {
      if (block < data0 || block >= data0 + data) continue;

      var dataIndex = block - data0;
      var rgrpBitmapBytes = this._blockSize - RgrpHeaderBytes;
      var rbBitmapBytes = this._blockSize - RbHeaderBytes;
      var byteOffset = dataIndex / 4;
      var shift = (int)(dataIndex % 4) * 2;

      long at;
      if (byteOffset < rgrpBitmapBytes) {
        at = header * this._blockSize + RgrpHeaderBytes + byteOffset;
      } else {
        var rest = byteOffset - rgrpBitmapBytes;
        at = (header + 1 + rest / rbBitmapBytes) * this._blockSize + RbHeaderBytes + rest % rbBitmapBytes;
      }
      if (at < 0 || at >= image.Length) return;

      image.Position = at;
      var current = image.ReadByte();
      if (current < 0) return;

      var updated = (current & ~(0x3 << shift)) | ((state & 0x3) << shift);
      if (updated == current) return;

      image.Position = at;
      image.WriteByte((byte)updated);
      return;
    }
  }

  /// <summary>The first block that holds a resource group header, or -1.</summary>
  private static long FindFirstRgrp(ImageAccessor image, long blockSize, long volumeBlocks) {
    var limit = Math.Min(volumeBlocks, 1L << 16);
    for (var block = 0L; block < limit; ++block) {
      var head = image.Read(block * blockSize, 8);
      if (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0, 4)) != MetaMagic) continue;
      if (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4, 4)) == MetaTypeRg) return block;
    }
    return -1;
  }

  /// <summary>
  /// How many blocks of a group hold its bitmap: four data blocks per byte, the
  /// first <c>blockSize - 128</c> bytes in the header block and the rest in the
  /// RB blocks after it.
  /// </summary>
  private static long ResolveHeaderBlocks(long span, long blockSize) {
    var rgrpBitmapBytes = blockSize - RgrpHeaderBytes;
    var rbBitmapBytes = blockSize - RbHeaderBytes;
    for (var headerBlocks = 1L; headerBlocks < span; ++headerBlocks) {
      var data = span - headerBlocks;
      var need = (data + 3) / 4;
      var have = rgrpBitmapBytes + (headerBlocks - 1) * rbBitmapBytes;
      if (have >= need) return headerBlocks;
    }
    return -1;
  }
}
