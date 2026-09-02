#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Trsdos;

/// <summary>
/// Moves a file inside a TRSDOS volume and repoints its directory entry.
/// </summary>
/// <remarks>
/// <para>TRSDOS allocates in granules of five sectors and records a file's
/// first granule in one byte of its directory entry, so a file's start is only
/// expressible on a five-sector boundary. The mover reports that granule as its
/// allocation unit, which keeps the planner from proposing a position the entry
/// could not name.</para>
///
/// <para>The entry is found by the granule it still names rather than by the
/// file's name, so a duplicate name cannot send the wrong file somewhere.</para>
/// </remarks>
public sealed class TrsdosBlockMover : IFilesystemBlockMover {

  /// <summary>Sectors per granule — the unit a directory entry can name.</summary>
  private const int SectorsPerGranule = 5;

  /// <summary>Offset of the first-granule byte inside a directory entry.</summary>
  private const int EntryFirstGranuleOffset = 24;

  private long _directoryEntriesStart;
  private int _maxEntries;

  /// <summary>Finds the directory track this volume was laid out with.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var reader = new TrsdosReader(image);
    if (!reader.ValidVolume)
      throw new InvalidDataException("TRSDOS: no directory track found.");

    this._directoryEntriesStart = reader.DirectoryTrackOffset + TrsdosReader.SectorSize * 2;
    var directoryBytes = (reader.SectorsPerTrack - 2) * TrsdosReader.SectorSize;
    this._maxEntries = directoryBytes / TrsdosReader.DirectoryEntrySize;
  }

  /// <summary>
  /// A granule. A file starts on a granule boundary because that is all its
  /// directory entry can express.
  /// </summary>
  public int BlockSize => SectorsPerGranule * TrsdosReader.SectorSize;

  /// <summary>First byte a file may occupy: the granule after the directory track.</summary>
  public long FirstDataByte => 0;

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
    if (this._maxEntries == 0) this.Init(image);

    var oldSector = oldOffset / TrsdosReader.SectorSize;
    var newSector = newOffset / TrsdosReader.SectorSize;
    if (oldSector == newSector) return;

    if (newSector % SectorsPerGranule != 0)
      throw new NotSupportedException(
        $"TRSDOS: sector {newSector} is not a granule boundary, which is all a directory entry " +
        "can name.");

    var oldGranule = oldSector / SectorsPerGranule;
    var newGranule = newSector / SectorsPerGranule;
    if (oldGranule > byte.MaxValue || newGranule > byte.MaxValue)
      throw new NotSupportedException(
        $"TRSDOS: granule {Math.Max(oldGranule, newGranule)} is past the 255 a directory entry holds.");

    var record = new byte[TrsdosReader.DirectoryEntrySize];
    for (var i = 0; i < this._maxEntries; ++i) {
      var at = this._directoryEntriesStart + (long)i * TrsdosReader.DirectoryEntrySize;
      if (at + TrsdosReader.DirectoryEntrySize > image.Length) break;

      image.Position = at;
      image.ReadExactly(record);
      var attributes = record[0];
      if (attributes == 0x00 || (attributes & 0x80) != 0) continue;     // empty or killed
      if (record[EntryFirstGranuleOffset] != oldGranule) continue;

      record[EntryFirstGranuleOffset] = (byte)newGranule;
      image.Position = at + EntryFirstGranuleOffset;
      image.Write(record.AsSpan(EntryFirstGranuleOffset, 1));
      image.Flush();
      return;
    }

    throw new InvalidOperationException(
      $"TRSDOS: no directory entry names granule {oldGranule}, so '{fileName}' cannot be repointed.");
  }
}
