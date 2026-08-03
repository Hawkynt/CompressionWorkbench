#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Tux3;

/// <summary>
/// Moves a whole record inside the container, which needs nothing else
/// rewritten.
/// </summary>
/// <remarks>
/// A record's data sits behind the header naming it, and the reader finds the
/// next record by adding this one's length to a cursor. So nothing records a
/// position and nothing has to be repointed — but the walk only reaches a
/// record that is still in order with nothing before it, which is what the
/// guard checks by reading every payload back afterwards.
/// </remarks>
public sealed class Tux3BlockMover : IFilesystemBlockMover {

  /// <summary>A byte: records are packed to the byte, not to any larger unit.</summary>
  public int BlockSize => 1;

  /// <summary>First byte a record may occupy: past the container's header.</summary>
  public long FirstDataByte => Tux3RecordMap.FirstRecord;

  /// <summary>Each call moves the record it is given and nothing else.</summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A record may be held outside the container while the rest of the layout
  /// moves, which is what lets a full one be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
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
  /// <remarks>
  /// Nothing to do: a record carries its own name and lengths, and where it
  /// sits is recorded nowhere.
  /// </remarks>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
  }
}
