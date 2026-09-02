#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.SquashFs;

/// <summary>
/// Moves a file's data blocks inside a SquashFS image and rewrites the inode
/// field that said where they began.
/// </summary>
/// <remarks>
/// <para>That field lives inside a metadata block the writer deflated, so it
/// cannot simply be written to: the block is taken apart, changed, and put
/// together again once the pass is over.</para>
///
/// <para>Which only works if the result still fits. A block's length is its own
/// header and every table after it is found by an offset in the superblock, so
/// a block that grew would move all of them; one that shrinks is padded back to
/// the length it had. A block that will not fit is refused, and the volume goes
/// through the rebuild instead.</para>
/// </remarks>
public sealed class SquashFsBlockMover : IFilesystemBlockMover {

  private SquashFsLayout.Layout? _layout;

  /// <summary>Every start block that changed, and what it changed to.</summary>
  private readonly Dictionary<uint, uint> _moved = [];

  /// <summary>Reads the inode table once and notes where each file's field is.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._layout = SquashFsLayout.Read(image);
    if (this._layout == null)
      throw new InvalidDataException("SquashFS: the inode table is not one this reads.");

    this._moved.Clear();
  }

  /// <summary>A byte. Data blocks are packed to the byte, compressed as they are.</summary>
  public int BlockSize => 1;

  /// <summary>First byte a file's data may occupy: past the superblock.</summary>
  public long FirstDataByte => 96;

  /// <summary>
  /// Each call notes where one file's data went; the table is written once the
  /// pass is over.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the image while the rest of the layout moves,
  /// which is what lets a full image be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
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
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._layout == null) this.Init(image);
    if (oldOffset == newOffset) return;

    if (oldOffset > uint.MaxValue || newOffset > uint.MaxValue)
      throw new NotSupportedException(
        "SquashFS: a starting block is four bytes, which this offset is past.");

    this._moved[(uint)oldOffset] = (uint)newOffset;
  }

  /// <summary>
  /// Writes the inode table again with every file's new starting block.
  /// </summary>
  /// <remarks>
  /// Called once a layout pass has finished. Each metadata block that changed
  /// is packed again and must come back no longer than it was, because its
  /// length is its own header and everything after it is found by an offset
  /// recorded elsewhere.
  /// </remarks>
  public void SettleInodeTable(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._layout == null || this._moved.Count == 0) return;

    var touched = new HashSet<int>();
    foreach (var field in this._layout.Fields) {
      if (field.BlockIndex < 0) continue;
      if (!this._moved.TryGetValue(field.StartBlock, out var moved)) continue;

      var block = this._layout.Blocks[field.BlockIndex];
      if (field.FieldOffset + 4 > block.Data.Length) continue;

      BinaryPrimitives.WriteUInt32LittleEndian(block.Data.AsSpan(field.FieldOffset), moved);
      touched.Add(field.BlockIndex);
    }

    foreach (var index in touched) {
      var block = this._layout.Blocks[index];
      var packed = SquashFsLayout.Repack(block)
        ?? throw new NotSupportedException(
          "SquashFS: the inode table does not pack back into the space it had, and a block that " +
          "grew would move every table after it.");

      image.Position = block.Offset + 2;
      image.Write(packed, 0, packed.Length);
    }

    image.Flush();
  }
}
