#pragma warning disable CS1591
using Compression.Registry;
using static FileSystem.CpcDsk.CpcDskAmsdos;

namespace FileSystem.CpcDsk;

/// <summary>
/// Says why an AMSDOS disk is laid out again rather than shuffled in place.
/// </summary>
/// <remarks>
/// <para>A planner moves runs of bytes and then has the filesystem write down
/// where they went. On an AMSDOS disk the only place that can be written down is
/// a directory entry's allocation list, which holds block numbers — and a block
/// is a kilobyte, while a track of nine 512-byte sectors is four and a half of
/// them. Blocks therefore straddle track boundaries, and in a DSK image a track
/// boundary is a 256-byte Track-Info block sitting between the two halves. A
/// block is not a contiguous stretch of the file, so a run of bytes the planner
/// can move is not a thing the directory can name.</para>
///
/// <para>An earlier version papered over this by calling one sector one block,
/// which made every run contiguous and every block number wrong. Refusing is the
/// honest answer: <see cref="CpcDskFormatDescriptor" /> lays the disk out again
/// instead, which reallocates the blocks in order and is what defragmenting a
/// CP/M disk means anyway. On a 180-kilobyte disk it costs nothing.</para>
/// </remarks>
public sealed class CpcDskBlockMover : IFilesystemBlockMover {

  private const string Reason =
    "CPC DSK: an AMSDOS allocation block is a kilobyte and a track holds four and a half of them, "
    + "so blocks straddle the Track-Info blocks in the image and cannot be moved as byte runs. "
    + "The disk is laid out again instead.";

  /// <summary>Reads the geometry, and reports that the disk cannot be shuffled in place.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    throw new NotSupportedException(Reason);
  }

  /// <summary>An allocation block: the unit any legal layout is expressed in.</summary>
  public int BlockSize => BlockSize_;

  private const int BlockSize_ = CpcDskAmsdos.BlockSize;

  /// <inheritdoc />
  /// <summary>
  /// Gets the allocation block size.
  /// </summary>
  public int AllocationBlockSize => BlockSize_;

  /// <summary>The first byte past the directory, which is where the files begin.</summary>
  public long FirstDataByte => DiskInfoSize + TrackInfoSize + (long)FirstDataBlock * BlockSize_;

  /// <inheritdoc />
  /// <summary>
  /// Gets a value indicating whether repoints runs independently.
  /// </summary>
  public bool RepointsRunsIndependently => false;

  /// <inheritdoc />
  /// <summary>
  /// Gets a value indicating whether supports held runs.
  /// </summary>
  public bool SupportsHeldRuns => false;

  /// <inheritdoc />
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length,
      bool zeroSource = false) => throw new NotSupportedException(Reason);

  /// <inheritdoc />
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName,
      long oldOffset, long newOffset, long length) => throw new NotSupportedException(Reason);
}
