#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Coherent;

/// <summary>
/// Moves a file's blocks inside a Coherent volume and rewrites the three bytes
/// that named each of them.
/// </summary>
/// <remarks>
/// <para>A block is named once: by a zone slot in the inode for the first ten,
/// and by an entry in an indirect block for the rest. So a move is the copy
/// plus three bytes, in the order a PDP-11 wrote them.</para>
///
/// <para>There is no free list to keep in step. The writer leaves the
/// superblock's free-block cache empty — a volume it produces is read-only in
/// practice — so nothing else records where a block is.</para>
/// </remarks>
public sealed class CoherentBlockMover : IFilesystemBlockMover {

  private CoherentLayout.Layout? _layout;

  /// <summary>Where the pointer naming each block sits, keyed by the block.</summary>
  private readonly Dictionary<uint, long> _pointerOf = [];

  /// <summary>Reads the inodes once and notes which three bytes name each block.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._layout = CoherentLayout.Read(image);
    if (this._layout == null)
      throw new InvalidDataException("Coherent: the volume is not one this reads.");

    this._pointerOf.Clear();
    foreach (var pointer in this._layout.Pointers)
      this._pointerOf[pointer.Block] = pointer.PointerOffset;
  }

  /// <summary>A block, which is what a zone address counts in.</summary>
  public int BlockSize => this._layout?.BlockSize ?? 0;

  /// <summary>First byte a file may occupy: past the superblock and the inode table.</summary>
  public long FirstDataByte => this._layout?.FirstDataOffset ?? 0;

  /// <summary>
  /// Each call rewrites the pointer naming the block it is given, so a file
  /// spread over the volume is simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A block may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
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

    var blockSize = this._layout!.BlockSize;
    if (newOffset % blockSize != 0)
      throw new NotSupportedException(
        $"Coherent: {newOffset} is not on a {blockSize}-byte block boundary, which is all a zone " +
        "address can name.");

    var from = (uint)(oldOffset / blockSize);
    var to = (uint)(newOffset / blockSize);
    if (from == to) return;
    if (to > 0xFFFFFF)
      throw new NotSupportedException(
        $"Coherent: block {to} is past the three bytes a zone address holds.");

    var count = (int)((length + blockSize - 1) / blockSize);

    // Every pointer the run needs is read before any is written: a run's own
    // blocks can be named from inside it, and a pointer patched early would
    // otherwise be read back as if it had always said that.
    var rewrites = new List<(long At, uint Block)>();
    for (var k = 0; k < count; ++k) {
      if (!this._pointerOf.TryGetValue(from + (uint)k, out var at))
        throw new InvalidOperationException(
          $"Coherent: nothing names block {from + (uint)k}, so '{fileName}' cannot be repointed.");

      rewrites.Add((at, to + (uint)k));
    }

    var moved = new byte[3];
    foreach (var (at, block) in rewrites) {
      // A pointer that lived inside the run travelled with it.
      var pointerAt = at >= (long)from * blockSize && at < (long)(from + count) * blockSize
        ? at - (long)from * blockSize + (long)to * blockSize
        : at;

      CoherentLayout.Write24(moved, block);
      image.Position = pointerAt;
      image.Write(moved, 0, 3);
    }

    // Re-key: the pointers that lived inside the run are at its new home now,
    // and the blocks they name have moved with it.
    var updated = new Dictionary<uint, long>();
    foreach (var (block, at) in this._pointerOf) {
      var pointerAt = at >= (long)from * blockSize && at < (long)(from + count) * blockSize
        ? at - (long)from * blockSize + (long)to * blockSize
        : at;
      var target = block >= from && block < from + count ? block - from + to : block;
      updated[target] = pointerAt;
    }

    this._pointerOf.Clear();
    foreach (var (block, at) in updated) this._pointerOf[block] = at;
    image.Flush();
  }
}
