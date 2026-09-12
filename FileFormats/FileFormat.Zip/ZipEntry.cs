namespace FileFormat.Zip;

/// <summary>
/// Represents a single entry in a ZIP archive.
/// </summary>
public sealed class ZipEntry {
  /// <summary>Gets or sets the file name (including path within the archive).</summary>
  public string FileName { get; set; } = string.Empty;

  /// <summary>Gets or sets the compression method.</summary>
  public ZipCompressionMethod CompressionMethod { get; set; }

  /// <summary>Gets or sets the CRC-32 of the uncompressed data.</summary>
  public uint Crc32 { get; set; }

  /// <summary>Gets or sets the compressed size in bytes.</summary>
  public long CompressedSize { get; set; }

  /// <summary>Gets or sets the uncompressed size in bytes.</summary>
  public long UncompressedSize { get; set; }

  /// <summary>Gets or sets the last modification date/time.</summary>
  public DateTime LastModified { get; set; } = new(1980, 1, 1);

  /// <summary>Gets or sets the extra field data.</summary>
  public byte[]? ExtraField { get; set; }

  /// <summary>Gets or sets the file comment.</summary>
  public string? Comment { get; set; }

  /// <summary>Gets or sets the external file attributes.</summary>
  public uint ExternalAttributes { get; set; }

  /// <summary>Gets or sets whether this entry is encrypted.</summary>
  public bool IsEncrypted { get; set; }

  /// <summary>Gets or sets the general-purpose bit flags from the ZIP header.</summary>
  internal ushort GeneralPurposeFlags { get; set; }

  /// <summary>Gets the offset of the local file header in the archive.</summary>
  internal long LocalHeaderOffset { get; set; }

  /// <summary>Gets whether this entry is a directory.</summary>
  public bool IsDirectory => FileName.EndsWith('/');

  /// <summary>Gets whether this entry requires ZIP64 extensions.</summary>
  internal bool IsZip64 =>
    CompressedSize > uint.MaxValue ||
    UncompressedSize > uint.MaxValue ||
    LocalHeaderOffset > uint.MaxValue;

  /// <summary>
  /// Converts a DateTime to MS-DOS date/time format.
  /// </summary>
  internal static (ushort Date, ushort Time) ToMsDosDateTime(DateTime dt) {
    if (dt.Year < 1980)
      dt = new DateTime(1980, 1, 1);

    var date = (ushort)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day);
    var time = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
    return (date, time);
  }

  /// <summary>
  /// Converts MS-DOS date/time to DateTime.
  /// </summary>
  internal static DateTime FromMsDosDateTime(ushort date, ushort time) {
    var year = ((date >> 9) & 0x7F) + 1980;
    var month = Math.Clamp((date >> 5) & 0x0F, 1, 12);
    var day = Math.Clamp(date & 0x1F, 1, 31);
    var hour = Math.Clamp((time >> 11) & 0x1F, 0, 23);
    var minute = Math.Clamp((time >> 5) & 0x3F, 0, 59);
    var second = Math.Clamp((time & 0x1F) * 2, 0, 59);

    try {
      return new DateTime(year, month, day, hour, minute, second);
    }
    catch (ArgumentOutOfRangeException) {
      return new DateTime(1980, 1, 1);
    }
  }
}
