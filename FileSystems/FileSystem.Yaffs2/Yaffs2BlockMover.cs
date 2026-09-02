#pragma warning disable CS1591
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.Yaffs2;

/// <summary>
/// Moves chunks inside a YAFFS2 image.
/// </summary>
/// <remarks>
/// <para>YAFFS2 is a log: nothing indexes where a chunk sits. Each chunk names
/// itself in its spare area — the object it belongs to, which chunk of that
/// object it is, and the sequence number that decides which copy wins — and a
/// mount finds a file by scanning for its chunks rather than by following a
/// pointer to them. So a chunk can be written anywhere the flash has room, and
/// moving one is the copy of its data and spare together with nothing to
/// repoint afterwards.</para>
///
/// <para>That is why the allocation update here does nothing but check the
/// destination is on a chunk boundary. Landing a chunk half way into another
/// would put its spare where the next chunk's data belongs, and the scan would
/// read both as rubbish.</para>
/// </remarks>
public sealed class Yaffs2BlockMover : IFilesystemBlockMover {

  private int _stride;

  /// <summary>Reads the chunk and spare sizes this image was written with.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.CanSeek) image.Position = 0;

    using var accessor = new ImageAccessor(image);
    var scan = Yaffs2Scanner.Scan(accessor);
    if (!scan.ParseOk || scan.ChunkSize <= 0 || scan.SpareSize <= 0)
      throw new InvalidDataException("YAFFS2: no chunk layout could be read from this image.");

    this._stride = scan.ChunkSize + scan.SpareSize;
  }

  /// <summary>One chunk: its data plus the spare area that names it.</summary>
  public int BlockSize => this._stride;

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
    ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      // Erased flash reads as 0xFF, and the scanner reads an all-ones spare as
      // a chunk that was never written. Zeroing instead leaves an object id of
      // zero, which is a chunk it has to classify.
      Fill(image, srcOffset, length, 0xFF);
  }

  /// <inheritdoc />
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._stride == 0) this.Init(image);

    if (newOffset % this._stride != 0)
      throw new NotSupportedException(
        $"YAFFS2: {newOffset} is not on a {this._stride}-byte chunk boundary, so the spare area " +
        "would land where the next chunk's data belongs.");

    // Nothing else to do: the chunk's spare area travelled with it, and that is
    // the only record of what the chunk holds.
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
