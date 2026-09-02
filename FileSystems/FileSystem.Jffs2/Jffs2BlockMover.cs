#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Jffs2;

/// <summary>
/// Moves nodes inside a JFFS2 image.
/// </summary>
/// <remarks>
/// <para>JFFS2 is a log: nothing indexes where a node sits. Each node opens
/// with a header naming the inode it belongs to, where in that file its data
/// starts, and the version that decides which copy of an overlapping range
/// wins — and both the header and the data carry their own CRCs. A mount finds
/// a file by scanning for its nodes, so a node can be written anywhere and
/// moving one is the copy with nothing to repoint afterwards.</para>
///
/// <para>What the mover does have to hold to is the four-byte grid a node
/// starts on, and leaving the space behind reading as erased flash rather than
/// as zeros — a run of zeros is not a node header, but it is not free space to
/// the scanner either.</para>
/// </remarks>
public sealed class Jffs2BlockMover : IFilesystemBlockMover {

  /// <summary>The grid a node starts on. JFFS2 rounds every node up to it.</summary>
  private const int NodeAlignment = 4;

  /// <summary>Nothing to read: the alignment is the format's, not the image's.</summary>
  public void Init(Stream image) => ArgumentNullException.ThrowIfNull(image);

  /// <summary>Four bytes — the grid a node header must start on.</summary>
  public int BlockSize => NodeAlignment;

  /// <summary>The log starts at the first byte; there is no reserved head.</summary>
  public long FirstDataByte => 0;

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
      // Erased flash reads as 0xFF, and that is what the scanner treats as
      // space nothing has been written to. Zeros are neither a node nor free.
      Fill(image, srcOffset, length, 0xFF);
  }

  /// <inheritdoc />
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);

    if (newOffset % NodeAlignment != 0)
      throw new NotSupportedException(
        $"JFFS2: {newOffset} is not on a {NodeAlignment}-byte boundary, and a node header has to " +
        "start on one.");

    // Nothing else to do: the node carries its own header, its own CRCs and the
    // version that decides which copy of its range wins, and that is the only
    // record of what it holds.
  }

  /// <summary>Writes <paramref name="value" /> over a range, a buffer at a time.</summary>
  private static void Fill(Stream image, long offset, long length, byte value) {
    var buffer = new byte[(int)Math.Min(length, 64 * 1024)];
    buffer.AsSpan().Fill(value);

    image.Position = offset;
    var remaining = length;
    while (remaining > 0) {
      var take = (int)Math.Min(remaining, buffer.Length);
      image.Write(buffer, 0, take);
      remaining -= take;
    }
    image.Flush();
  }
}
