#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Nilfs2;

/// <summary>
/// Moves a payload inside the base segment of a NILFS volume and rewrites the
/// directory field that said where it began.
/// </summary>
/// <remarks>
/// <para>A payload's position is an offset from the start of the segment
/// describing it, so a move is that one field — and the payload has to stay
/// inside its own segment's area. It cannot go below where the payloads start,
/// which would be a negative offset, and it cannot reach past the first
/// appended segment, whose header the reader finds by carrying on from where
/// the base payloads end.</para>
///
/// <para>That is where the holes are anyway. Removing a file writes a tombstone
/// into a new segment and leaves the bytes it had in the base area unclaimed,
/// which is exactly the space a pass closes up.</para>
/// </remarks>
public sealed class Nilfs2BlockMover : IFilesystemBlockMover {

  private Nilfs2Layout.Layout? _layout;

  /// <summary>Where the field naming each payload sits, keyed by where it is now.</summary>
  private readonly Dictionary<long, long> _offsetField = [];

  /// <summary>Reads the base segment once and notes which field names each payload.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._layout = Nilfs2Layout.Read(image);
    if (this._layout == null)
      throw new InvalidDataException("Nilfs2: the volume is not one this reads.");

    this._offsetField.Clear();
    foreach (var payload in this._layout.Payloads)
      this._offsetField[payload.Offset] = payload.OffsetField;
  }

  /// <summary>
  /// A byte. Payloads are packed to the byte here, so nothing rounds them to
  /// anything larger.
  /// </summary>
  public int BlockSize => 1;

  /// <summary>First byte a payload may occupy: where the base segment's payloads start.</summary>
  public long FirstDataByte => this._layout?.PayloadStart ?? 0;

  /// <summary>Where the area a payload may occupy ends.</summary>
  public long PayloadEnd => this._layout?.PayloadEnd ?? 0;

  /// <summary>
  /// Each call rewrites the field naming the payload it is given and leaves the
  /// others alone.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A payload may be held outside the volume while the rest of the layout
  /// moves, which is what lets a full area be rearranged at all.
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

    if (newOffset < this._layout!.PayloadStart)
      throw new NotSupportedException(
        "Nilfs2: a payload cannot start before its segment's own payloads do — the format writes " +
        "the position as an offset from there, and it has no way to say a negative one.");

    if (newOffset + length > this._layout.PayloadEnd)
      throw new NotSupportedException(
        "Nilfs2: a payload cannot reach past the next segment's header, which the reader finds by " +
        "carrying on from where these payloads end.");

    // Keyed by where the run STARTED, and never re-keyed. The pass names a run by
    // its original address even for one it lifted out and put back later, and by
    // the time it does something else has very likely been laid down there. Moving
    // the key to the run's new address made this index answer "who lives here now"
    // instead of "who started here": a run that landed on another's old address
    // took over that other's record, and the two files swapped contents. It only
    // shows when files are the same length, because that is when the layout has
    // reason to put one where another was.
    if (!this._offsetField.TryGetValue(oldOffset, out var field))
      throw new InvalidOperationException(
        $"Nilfs2: the directory names no payload at {oldOffset}, so '{fileName}' cannot be repointed.");

    Span<byte> value = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(value, newOffset - this._layout.PayloadStart);
    image.Position = field;
    image.Write(value);

    image.Flush();
  }
}
