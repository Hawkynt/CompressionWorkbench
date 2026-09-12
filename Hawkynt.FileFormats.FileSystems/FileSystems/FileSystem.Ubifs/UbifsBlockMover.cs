#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Ubifs;

/// <summary>
/// Moves a node inside a UBIFS log, which needs nothing else rewritten.
/// </summary>
/// <remarks>
/// <para>A node's position is recorded nowhere. This writer emits a linear log
/// and no index, and the reader replays that log by looking for the magic at
/// the head of each node and taking the highest sequence number for every inode
/// and block. So a node carries its own identity, and moving it repoints
/// nothing — the checksum in its header covers the node itself, which a move
/// does not change.</para>
///
/// <para>What a move must do is leave nothing behind. A copy of a node still
/// carrying its magic is a second node as far as the log is concerned, and the
/// replay would find both.</para>
/// </remarks>
public sealed class UbifsBlockMover : IFilesystemBlockMover {

  /// <summary>Bytes a node is aligned to, which is the unit the log walks in.</summary>
  private const int NodeAlignment = 8;

  private long _firstNodeOffset;

  /// <summary>Notes where the log's nodes begin.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var nodes = UbifsLayout.Nodes(image);
    this._firstNodeOffset = nodes.Count == 0 ? 0 : nodes.Min(n => n.Offset);
  }

  /// <summary>The eight bytes a node is aligned to.</summary>
  public int BlockSize => NodeAlignment;

  /// <summary>
  /// First byte a node may occupy. The superblock and the master nodes sit at
  /// the front and are found where they are, so nothing may be put in front of
  /// where the log already starts.
  /// </summary>
  public long FirstDataByte => this._firstNodeOffset;

  /// <summary>
  /// Each call moves the node it is given and nothing else, because nothing
  /// else names it.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A node may be held outside the image while the rest of the layout moves,
  /// which is what lets a full image be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
  /// <remarks>
  /// The source is always cleared, whatever the caller asked for: a node left
  /// where it was is one the log replays twice.
  /// </remarks>
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);

    // Whatever of the source the copy did not land on is cleared, so no magic
    // is left behind for the replay to find.
    var overlapStart = Math.Max(srcOffset, dstOffset);
    var overlapEnd = Math.Min(srcOffset + length, dstOffset + length);
    if (overlapStart >= overlapEnd) {
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
      return;
    }

    if (srcOffset < overlapStart)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, overlapStart - srcOffset);
    if (overlapEnd < srcOffset + length)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, overlapEnd, srcOffset + length - overlapEnd);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Nothing to do. A node is found by its magic and identified by what it
  /// carries, so no field anywhere says where it is.
  /// </remarks>
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);

    if (newOffset % NodeAlignment != 0)
      throw new NotSupportedException(
        $"UBIFS: {newOffset} is not on an eight-byte boundary, which is the unit the log walks in.");
  }
}
