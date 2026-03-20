namespace FileFormat.Arc;

/// <summary>
/// Represents the metadata for a single entry in an ARC archive.
/// </summary>
public sealed class ArcEntry {
  /// <summary>Gets or sets the filename stored in the archive (up to 12 characters).</summary>
  public string FileName { get; set; } = string.Empty;

  /// <summary>Gets or sets the compression method code.</summary>
  public byte Method { get; set; } = ArcConstants.MethodStored;

  /// <summary>Gets or sets the compressed size in bytes.</summary>
  public uint CompressedSize { get; set; }

  /// <summary>Gets or sets the uncompressed (original) size in bytes.</summary>
  public uint OriginalSize { get; set; }

  /// <summary>Gets or sets the MS-DOS date stamp.</summary>
  public ushort DosDate { get; set; }

  /// <summary>Gets or sets the MS-DOS time stamp.</summary>
  public ushort DosTime { get; set; }

  /// <summary>Gets or sets the CRC-16 of the uncompressed data.</summary>
  public ushort Crc16 { get; set; }

  /// <summary>
  /// Gets the last modified time decoded from the MS-DOS date/time fields,
  /// or <see cref="DateTimeOffset.MinValue"/> if the fields are zero.
  /// </summary>
  public DateTimeOffset LastModified {
    get => DosDateTimeToDateTimeOffset(this.DosDate, this.DosTime);
    set {
      (this.DosDate, this.DosTime) = DateTimeOffsetToDosDateTime(value);
    }
  }

  private static DateTimeOffset DosDateTimeToDateTimeOffset(ushort date, ushort time) {
    if (date == 0)
      return DateTimeOffset.MinValue;

    int year = ((date >> 9) & 0x7F) + 1980;
    int month = (date >> 5) & 0x0F;
    int day = date & 0x1F;
    int hour = (time >> 11) & 0x1F;
    int minute = (time >> 5) & 0x3F;
    int second = (time & 0x1F) * 2;

    if (month < 1 || month > 12 || day < 1 || day > 31)
      return DateTimeOffset.MinValue;

    try {
      return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
    } catch (ArgumentOutOfRangeException) {
      return DateTimeOffset.MinValue;
    }
  }

  private static (ushort date, ushort time) DateTimeOffsetToDosDateTime(DateTimeOffset dt) {
    if (dt == DateTimeOffset.MinValue)
      return (0, 0);

    var local = dt.ToLocalTime();
    int year = local.Year - 1980;
    if (year < 0) year = 0;
    if (year > 127) year = 127;

    ushort date = (ushort)(((year & 0x7F) << 9) | ((local.Month & 0x0F) << 5) | (local.Day & 0x1F));
    ushort time = (ushort)(((local.Hour & 0x1F) << 11) | ((local.Minute & 0x3F) << 5) | ((local.Second / 2) & 0x1F));
    return (date, time);
  }
}
