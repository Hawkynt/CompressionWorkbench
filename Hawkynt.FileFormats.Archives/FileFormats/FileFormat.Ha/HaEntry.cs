namespace FileFormat.Ha;

/// <summary>
/// Represents a single entry in an Ha archive.
/// </summary>
public sealed class HaEntry {
  /// <summary>Gets the filename stored in the archive (may include path with '/' separators).</summary>
  public string FileName { get; init; } = string.Empty;

  /// <summary>Gets the compression method byte (0=Store, 1=HSC, 2=ASC, 14=Directory).</summary>
  public int Method { get; init; }

  /// <summary>Gets the uncompressed size in bytes.</summary>
  public uint OriginalSize { get; init; }

  /// <summary>Gets the compressed size in bytes.</summary>
  public uint CompressedSize { get; init; }

  /// <summary>Gets the CRC-32 (IEEE polynomial) of the uncompressed data.</summary>
  public uint Crc32 { get; init; }

  /// <summary>Gets the last-modification date/time.</summary>
  public DateTime LastModified { get; init; }

  /// <summary>Gets whether this entry is a directory.</summary>
  public bool IsDirectory => this.Method == HaConstants.MethodDirectory;

  /// <summary>Offset in the stream where the compressed data begins. Used internally by the reader.</summary>
  internal long DataOffset { get; init; }

  // ── MS-DOS date/time helpers ──────────────────────────────────────────────

  internal static uint EncodeMsDosDateTime(DateTime dt) {
    if (dt.Year < 1980)
      dt = new DateTime(1980, 1, 1);

    var time = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
    var date = (ushort)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day);

    // Low word = time, high word = date (same layout as ZIP).
    return (uint)((date << 16) | time);
  }

  internal static DateTime DecodeMsDosDateTime(uint dosDateTime) {
    var time = (ushort)(dosDateTime & 0xFFFF);
    var date = (ushort)(dosDateTime >> 16);

    var year  = ((date >> 9) & 0x7F) + 1980;
    var month = Math.Clamp((date >> 5) & 0x0F, 1, 12);
    var day   = Math.Clamp(date & 0x1F, 1, 31);
    var hour  = Math.Clamp((time >> 11) & 0x1F, 0, 23);
    var min   = Math.Clamp((time >> 5)  & 0x3F, 0, 59);
    var sec   = Math.Clamp((time & 0x1F) * 2,   0, 59);

    try {
      return new DateTime(year, month, day, hour, min, sec);
    } catch (ArgumentOutOfRangeException) {
      return new DateTime(1980, 1, 1);
    }
  }
}
