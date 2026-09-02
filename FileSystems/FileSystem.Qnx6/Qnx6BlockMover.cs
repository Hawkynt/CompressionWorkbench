#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Qnx6;

/// <summary>
/// Moves a file's blocks inside a QNX6 volume and repoints its inode.
/// </summary>
/// <remarks>
/// <para>A file here is one contiguous run of blocks and its inode's first
/// direct pointer says where that run starts, so relocating a file is the copy
/// plus one four-byte write. Nothing else records the position: QNX6 tracks
/// free space in a bitmap the reader does not maintain, and the superblock's
/// counts describe how much is free rather than which blocks are.</para>
///
/// <para>The inode is found by the block it still names rather than by the
/// file's name, so two entries pointing at the same inode cannot send the wrong
/// one somewhere.</para>
/// </remarks>
public sealed class Qnx6BlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the first direct block pointer inside an inode.</summary>
  private const int InodeFirstBlockOffset = 0x24;

  /// <summary>Offset of the inode table's block pointer inside the superblock.</summary>
  private const int SuperblockInodeRootPointer = 0x48 + 8;

  /// <summary>Offset of the block size inside the superblock.</summary>
  private const int SuperblockBlockSize = 0x30;

  private int _blockSize;
  private long _inodeTableOffset;
  private int _inodeCount;
  private long _firstDataByte;

  /// <summary>Reads the geometry and the inode table's position from the superblock.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var superblock = new byte[512];
    image.Position = Qnx6Reader.SuperblockOffset;
    image.ReadExactly(superblock);

    var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(SuperblockBlockSize));
    if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0) blockSize = 1024;
    this._blockSize = blockSize;

    var inodeTableBlock = BinaryPrimitives.ReadUInt32LittleEndian(
      superblock.AsSpan(SuperblockInodeRootPointer));
    // A pointer in the superblock counts from the filesystem's own block zero,
    // which is past the boot and superblock areas.
    this._inodeTableOffset = Qnx6Geometry.ByteOffsetOf(inodeTableBlock, blockSize);

    var inodeCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(0x34));
    this._inodeCount = Math.Max(1, inodeCount);

    // The first byte a file may occupy is the one past everything the volume
    // needs to describe itself: the superblock, the inode table, and the root
    // directory block the root inode names.
    image.Position = 0;
    using var reader = new Qnx6Reader(image);
    var firstUsed = long.MaxValue;
    foreach (var entry in reader.Entries)
      if (reader.TryGetDataExtent(entry, out var offset, out _))
        firstUsed = Math.Min(firstUsed, offset);
    this._firstDataByte = firstUsed == long.MaxValue
      ? this._inodeTableOffset + (long)this._inodeCount * Qnx6Reader.InodeSize
      : firstUsed;
  }

  /// <summary>Block size in bytes, as the superblock records it.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>First byte a file may occupy: past the inode table and the root directory.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <inheritdoc />
  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._blockSize == 0) this.Init(image);

    if (newOffset % this._blockSize != 0)
      throw new NotSupportedException(
        $"QNX6: {newOffset} is not on a {this._blockSize}-byte block boundary, which is all an " +
        "inode's block pointer can name.");

    // Inode pointers are filesystem blocks, so a byte offset has to be turned
    // back into one before it is written down.
    var before = Qnx6Geometry.BlocksBefore(this._blockSize);
    var oldBlock = (uint)(oldOffset / this._blockSize - before);
    var newBlock = (uint)(newOffset / this._blockSize - before);
    if (oldBlock == newBlock) return;

    Span<byte> pointer = stackalloc byte[4];
    for (var inode = 1; inode <= this._inodeCount; ++inode) {
      var at = this._inodeTableOffset + (long)(inode - 1) * Qnx6Reader.InodeSize;
      if (at + Qnx6Reader.InodeSize > image.Length) break;

      image.Position = at + InodeFirstBlockOffset;
      image.ReadExactly(pointer);
      if (BinaryPrimitives.ReadUInt32LittleEndian(pointer) != oldBlock) continue;

      BinaryPrimitives.WriteUInt32LittleEndian(pointer, newBlock);
      image.Position = at + InodeFirstBlockOffset;
      image.Write(pointer);
      image.Flush();
      return;
    }

    throw new InvalidOperationException(
      $"QNX6: no inode names block {oldBlock}, so '{fileName}' cannot be repointed.");
  }
}
