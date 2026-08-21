#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Nss;

/// <summary>
/// Moves a file's blocks inside an NSS container and rewrites the directory
/// field that said where they were.
/// </summary>
/// <remarks>
/// A file's position is one eight-byte field and nothing else — no chain, no
/// tree, no order implied by anything. So a move is a copy and one number, and
/// the only thing a file may not do is start before the anchors and the
/// directory end.
/// </remarks>
public sealed class NssBlockMover : IFilesystemBlockMover {

  /// <summary>Where the field naming each file sits, keyed by where it is now.</summary>
  private readonly Dictionary<long, long> _offsetField = [];

  private NssVolume? _volume;

  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    this._volume = new NssVolume(image);
    if (!this._volume.Valid)
      throw new InvalidDataException($"NSS: {this._volume.Status}.");

    this._offsetField.Clear();
    foreach (var file in this._volume.Files)
      this._offsetField[file.Offset] = file.OffsetField;
  }

  public int BlockSize => NssLayout.BlockSize;

  /// <summary>First byte a file may occupy: past the anchors and the directory.</summary>
  public long FirstDataByte => NssLayout.FirstDataBlock * NssLayout.BlockSize;

  /// <summary>Each call rewrites the one field naming the file it is given.</summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A file may be held outside the container while the rest of the layout
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
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._volume == null) this.Init(image);
    if (oldOffset == newOffset) return;

    if (newOffset % NssLayout.BlockSize != 0)
      throw new NotSupportedException(
        $"NSS: a file starts on a block, so it cannot start at byte {newOffset}.");

    if (newOffset < this.FirstDataByte)
      throw new NotSupportedException(
        "NSS: a file cannot start before the anchors and the directory end.");

    if (newOffset + length > this._volume!.ImageLength)
      throw new NotSupportedException("NSS: a file cannot reach past the end of the container.");

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
        $"NSS: the directory names no file at {oldOffset}, so '{fileName}' cannot be repointed.");

    Span<byte> value = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(value, newOffset);
    image.Position = field;
    image.Write(value);

    image.Flush();
  }
}
