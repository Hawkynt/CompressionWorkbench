namespace FileFormat.Spark;

/// <summary>
/// Represents the metadata for a single entry in a Spark archive.
/// </summary>
public sealed class SparkEntry {
  /// <summary>Gets or sets the filename (up to 13 chars for standard ARC, longer for Spark extensions).</summary>
  public string FileName { get; init; } = string.Empty;

  /// <summary>Gets or sets the compression method byte.</summary>
  public byte Method { get; init; }

  /// <summary>Gets or sets the uncompressed (original) size in bytes.</summary>
  public uint OriginalSize { get; init; }

  /// <summary>Gets or sets the compressed size in bytes.</summary>
  public uint CompressedSize { get; init; }

  /// <summary>Gets or sets the CRC-16 of the uncompressed data.</summary>
  public ushort Crc16 { get; init; }

  /// <summary>Gets or sets the last-modified timestamp.</summary>
  public DateTime LastModified { get; init; }

  /// <summary>Gets or sets whether this entry represents a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>
  /// Gets or sets the RISC OS load address.
  /// For typed files, bits 8-19 contain the file type and bits 20-31 are 0xFFF.
  /// </summary>
  public uint LoadAddress { get; init; }

  /// <summary>Gets or sets the RISC OS execution address.</summary>
  public uint ExecAddress { get; init; }

  /// <summary>Gets or sets the RISC OS file attributes.</summary>
  public uint FileAttributes { get; init; }

  /// <summary>Gets or sets the offset in the stream where the compressed data begins (internal use).</summary>
  internal long DataOffset { get; init; }

  /// <summary>
  /// Gets the RISC OS file type extracted from the load address, or <see langword="null"/>
  /// if the load address does not encode a file type (top 12 bits must be 0xFFF).
  /// </summary>
  public int? RiscOsFileType {
    get {
      if ((this.LoadAddress & 0xFFF00000) == 0xFFF00000)
        return (int)((this.LoadAddress >> 8) & 0xFFF);
      return null;
    }
  }

  internal static DateTime DosDateTimeToDateTime(ushort date, ushort time) {
    if (date == 0)
      return DateTime.MinValue;

    int year = ((date >> 9) & 0x7F) + 1980;
    int month = (date >> 5) & 0x0F;
    int day = date & 0x1F;
    int hour = (time >> 11) & 0x1F;
    int minute = (time >> 5) & 0x3F;
    int second = (time & 0x1F) * 2;

    if (month < 1 || month > 12 || day < 1 || day > 31)
      return DateTime.MinValue;

    try {
      return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
    } catch (ArgumentOutOfRangeException) {
      return DateTime.MinValue;
    }
  }

  internal static (ushort date, ushort time) DateTimeToDosDateTime(DateTime dt) {
    if (dt == DateTime.MinValue)
      return (0, 0);

    int year = dt.Year - 1980;
    if (year < 0) year = 0;
    if (year > 127) year = 127;

    ushort date = (ushort)(((year & 0x7F) << 9) | ((dt.Month & 0x0F) << 5) | (dt.Day & 0x1F));
    ushort time = (ushort)(((dt.Hour & 0x1F) << 11) | ((dt.Minute & 0x3F) << 5) | ((dt.Second / 2) & 0x1F));
    return (date, time);
  }
}
