namespace FileFormat.Cab;

/// <summary>
/// Represents a file entry stored inside a Microsoft Cabinet (CAB) archive.
/// </summary>
public sealed class CabEntry {
  /// <summary>
  /// Gets the name of the file as stored in the archive.
  /// </summary>
  public string FileName { get; }

  /// <summary>
  /// Gets the uncompressed size of the file in bytes.
  /// </summary>
  public uint UncompressedSize { get; }

  /// <summary>
  /// Gets the uncompressed byte offset of this file within its folder.
  /// </summary>
  public uint FolderOffset { get; }

  /// <summary>
  /// Gets the zero-based index of the folder that contains this file.
  /// </summary>
  public ushort FolderIndex { get; }

  /// <summary>
  /// Gets the MS-DOS encoded date field.
  /// </summary>
  public ushort Date { get; }

  /// <summary>
  /// Gets the MS-DOS encoded time field.
  /// </summary>
  public ushort Time { get; }

  /// <summary>
  /// Gets the file attribute flags.
  /// </summary>
  public ushort Attributes { get; }

  /// <summary>
  /// Gets the last-modified date/time derived from the MS-DOS date and time fields.
  /// Returns <c>null</c> when the encoded value is invalid.
  /// </summary>
  public DateTime? LastModified {
    get {
      try {
        var year  = 1980 + (this.Date >> 9);
        var month = (this.Date >> 5) & 0x0F;
        var day   = this.Date & 0x1F;
        var hour  = this.Time >> 11;
        var min   = (this.Time >> 5) & 0x3F;
        var sec   = (this.Time & 0x1F) * 2;

        if (month is < 1 or > 12 || day is < 1 or > 31)
          return null;

        return new DateTime((int)year, (int)month, (int)day,
                            (int)hour, (int)min, (int)sec);
      } catch (ArgumentOutOfRangeException) {
        return null;
      }
    }
  }

  /// <summary>
  /// Initializes a new <see cref="CabEntry"/>.
  /// </summary>
  /// <param name="fileName">The file name.</param>
  /// <param name="uncompressedSize">The uncompressed file size.</param>
  /// <param name="folderOffset">The uncompressed byte offset within the folder.</param>
  /// <param name="folderIndex">The zero-based folder index.</param>
  /// <param name="date">MS-DOS date field.</param>
  /// <param name="time">MS-DOS time field.</param>
  /// <param name="attributes">File attribute flags.</param>
  internal CabEntry(
    string fileName,
    uint uncompressedSize,
    uint folderOffset,
    ushort folderIndex,
    ushort date,
    ushort time,
    ushort attributes) {
    this.FileName        = fileName;
    this.UncompressedSize = uncompressedSize;
    this.FolderOffset    = folderOffset;
    this.FolderIndex     = folderIndex;
    this.Date            = date;
    this.Time            = time;
    this.Attributes      = attributes;
  }
}
