#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.ReiserFs;

/// <summary>
/// Moves a file's blocks inside a ReiserFS volume, repoints the pointers that
/// name them, and moves the allocation with them.
/// </summary>
/// <remarks>
/// <para>A ReiserFS file's out-of-line bytes are addressed one block at a time
/// by an array of four-byte pointers in an indirect item. Moving a run of them
/// is the copy, the pointers that named it, and the bitmap bits that say which
/// blocks are taken. Without the bitmap half, the next file added would be
/// allocated straight on top of one that had moved.</para>
///
/// <para>The pointers are found by the block the first of them still names, so
/// two files sharing a leaf name cannot send the wrong one somewhere. A run is
/// only ever reported when its blocks and its pointers are both consecutive,
/// which is what lets the whole run be repointed by counting forward.</para>
/// </remarks>
public sealed class ReiserFsBlockMover : IFilesystemBlockMover {

  /// <summary>Byte offset of the superblock inside the image.</summary>
  private const int SuperblockOffset = 64 * 1024;

  /// <summary>Block the first bitmap occupies.</summary>
  private const int FirstBitmapBlock = 17;

  private int _blockSize;
  private long _blockCount;
  private long _firstDataByte;

  /// <summary>Reads the geometry and where file data may start.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var superblock = new byte[64];
    if (image.Length < SuperblockOffset + superblock.Length)
      throw new InvalidDataException("ReiserFS: the image is too short to hold a superblock.");
    image.Position = SuperblockOffset;
    image.ReadExactly(superblock);

    this._blockCount = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(0));
    var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(44));
    this._blockSize = blockSize == 0 ? 4096 : blockSize;

    image.Position = 0;
    using var reader = new ReiserFsReader(image);
    var first = long.MaxValue;
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      foreach (var (offset, length, _) in reader.EnumerateDataExtents(entry))
        if (length > 0) first = Math.Min(first, offset);
    }
    this._firstDataByte = first == long.MaxValue
      ? (long)(FirstBitmapBlock + 1) * this._blockSize
      : first;
  }

  /// <summary>Block size in bytes, as the superblock records it.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>First byte a file may occupy: past the superblock, journal and bitmaps.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <summary>
  /// Each call repoints the run it is given and nothing else, so an owner
  /// scattered over several runs is simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

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
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => this.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length, releaseOldSpace: true);

  /// <inheritdoc />
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset,
      long length, bool releaseOldSpace) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._blockSize == 0) this.Init(image);

    if (newOffset % this._blockSize != 0)
      throw new NotSupportedException(
        $"ReiserFS: {newOffset} is not on a {this._blockSize}-byte block boundary, which is all a " +
        "block pointer can name.");

    var oldBlock = oldOffset / this._blockSize;
    var newBlock = newOffset / this._blockSize;
    if (oldBlock == newBlock) return;

    var blocks = (int)((length + this._blockSize - 1) / this._blockSize);
    var pointerOffset = this.FindPointerNaming(image, fileName, oldOffset, blocks);
    if (pointerOffset < 0)
      throw new InvalidOperationException(
        $"ReiserFS: no pointer run names block {oldBlock}, so '{fileName}' cannot be repointed.");

    Span<byte> pointer = stackalloc byte[4];
    for (var i = 0; i < blocks; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(pointer, (uint)(newBlock + i));
      image.Position = pointerOffset + (long)i * 4;
      image.Write(pointer);
    }

    // The bitmap says which blocks are taken; leaving it behind would let the
    // next file added to the volume be allocated straight on top of this one.
    if (releaseOldSpace) this.SetBits(image, oldBlock, blocks, allocated: false);
    this.SetBits(image, newBlock, blocks, allocated: true);
    image.Flush();
  }

  /// <summary>
  /// The byte offset of the first pointer of the run that starts at
  /// <paramref name="offset" /> and covers <paramref name="blocks" />, or -1.
  /// </summary>
  private long FindPointerNaming(Stream image, string fileName, long offset, int blocks) {
    image.Position = 0;
    using var reader = new ReiserFsReader(image);
    // Several records can name one place while a run is being held out of the
    // volume: the run's own, which still points where it was, and whatever has
    // since moved in. The one being moved is the one named here.
    var candidates = reader.Entries
      .Where(e => !e.IsDirectory)
      .OrderByDescending(e => string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase));

    foreach (var entry in candidates) {
      foreach (var (runOffset, runLength, pointerOffset) in reader.EnumerateDataExtents(entry)) {
        if (runOffset != offset) continue;
        if (runLength < (long)blocks * this._blockSize) continue;
        return pointerOffset;
      }
    }
    return -1;
  }

  /// <summary>
  /// Flips <paramref name="count" /> allocation bits from
  /// <paramref name="startBlock" />. A set bit means allocated.
  /// </summary>
  private void SetBits(Stream image, long startBlock, int count, bool allocated) {
    var blocksPerBitmap = (long)this._blockSize * 8;

    for (var i = 0; i < count; ++i) {
      var block = startBlock + i;
      if (block < 0 || block >= this._blockCount) break;

      var bitmapIndex = block / blocksPerBitmap;
      var bitmapBlock = bitmapIndex == 0 ? FirstBitmapBlock : bitmapIndex * blocksPerBitmap;
      var bit = block % blocksPerBitmap;
      var at = bitmapBlock * this._blockSize + (bit >> 3);
      if (at >= image.Length) break;

      image.Position = at;
      var current = image.ReadByte();
      if (current < 0) break;

      var mask = 1 << (int)(bit & 7);
      var updated = allocated ? current | mask : current & ~mask;
      if (updated == current) continue;

      image.Position = at;
      image.WriteByte((byte)updated);
    }
  }
}
