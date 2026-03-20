namespace FileFormat.Zoo;

/// <summary>
/// Compression method used for a <see cref="ZooEntry"/>.
/// </summary>
public enum ZooCompressionMethod : byte {
  /// <summary>File data is stored verbatim with no compression.</summary>
  Store = ZooConstants.MethodStore,

  /// <summary>File data is compressed using LZW (9–13 bit, LSB-first).</summary>
  Lzw = ZooConstants.MethodLzw,
}

/// <summary>
/// Represents a single file entry in a Zoo archive.
/// </summary>
public sealed class ZooEntry {
  /// <summary>Gets or sets the short filename (up to 12 characters, DOS 8.3 style).</summary>
  public string FileName { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the long filename.  When non-null and non-empty the entry is
  /// written as type 2 (long-name entry); otherwise it is written as type 1.
  /// </summary>
  public string? LongFileName { get; set; }

  /// <summary>Gets the effective display name: <see cref="LongFileName"/> when available, otherwise <see cref="FileName"/>.</summary>
  public string EffectiveName => !string.IsNullOrEmpty(this.LongFileName) ? this.LongFileName! : this.FileName;

  /// <summary>Gets or sets the compression method.</summary>
  public ZooCompressionMethod CompressionMethod { get; set; }

  /// <summary>Gets or sets the CRC-16 (ARC polynomial) of the uncompressed data.</summary>
  public ushort Crc16 { get; set; }

  /// <summary>Gets or sets the uncompressed size in bytes.</summary>
  public uint OriginalSize { get; set; }

  /// <summary>Gets or sets the compressed size in bytes.</summary>
  public uint CompressedSize { get; set; }

  /// <summary>Gets or sets the last-modification date/time.</summary>
  public DateTime LastModified { get; set; } = new DateTime(1980, 1, 1);

  /// <summary>Gets or sets whether this entry has been marked as deleted.</summary>
  public bool IsDeleted { get; set; }

  /// <summary>Gets or sets the major version of the tool that created the entry.</summary>
  public byte MajorVersion { get; set; } = ZooConstants.MajorVersion;

  /// <summary>Gets or sets the minor version of the tool that created the entry.</summary>
  public byte MinorVersion { get; set; } = ZooConstants.MinorVersion;

  /// <summary>
  /// Offset of this entry's directory header within the archive stream.
  /// Set by the reader; not relevant for the writer.
  /// </summary>
  internal long HeaderOffset { get; set; }

  /// <summary>
  /// Offset at which the compressed data begins within the archive stream.
  /// Set by the writer before writing and by the reader while parsing.
  /// </summary>
  internal long DataOffset { get; set; }

  // ── MS-DOS date/time helpers (shared with the writer) ────────────────────

  /// <summary>Encodes a <see cref="DateTime"/> as a pair of MS-DOS date and time words.</summary>
  internal static (ushort Date, ushort Time) ToMsDosDateTime(DateTime dt) {
    if (dt.Year < 1980)
      dt = new DateTime(1980, 1, 1);

    ushort date = (ushort)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day);
    ushort time = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
    return (date, time);
  }

  /// <summary>Decodes a pair of MS-DOS date and time words into a <see cref="DateTime"/>.</summary>
  internal static DateTime FromMsDosDateTime(ushort date, ushort time) {
    int year  = ((date >> 9) & 0x7F) + 1980;
    int month = Math.Clamp((date >> 5) & 0x0F, 1, 12);
    int day   = Math.Clamp(date & 0x1F, 1, 31);
    int hour  = Math.Clamp((time >> 11) & 0x1F, 0, 23);
    int min   = Math.Clamp((time >> 5)  & 0x3F, 0, 59);
    int sec   = Math.Clamp((time & 0x1F) * 2,   0, 59);

    try {
      return new DateTime(year, month, day, hour, min, sec);
    } catch (ArgumentOutOfRangeException) {
      return new DateTime(1980, 1, 1);
    }
  }
}
