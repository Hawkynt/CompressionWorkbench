namespace FileFormat.Uharc;

/// <summary>
/// One file or directory of a UHARC 0.6 archive.
/// </summary>
public sealed class UharcEntry {

  /// <summary>Gets the stored path, with '/' separators.</summary>
  public string FileName { get; init; } = string.Empty;

  /// <summary>Gets the uncompressed size in bytes (0 for a directory).</summary>
  public long Size { get; init; }

  /// <summary>Gets the CRC-32 (IEEE) of the uncompressed data.</summary>
  public uint Crc32 { get; init; }

  /// <summary>Gets the last-modification time, or null when the stored DOS stamp is invalid.</summary>
  public DateTime? LastModified { get; init; }

  /// <summary>Gets whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Gets the per-file attribute/method byte as stored in the directory.</summary>
  public byte Attributes { get; init; }

  /// <summary>Offset of the file's bytes inside the decoded solid stream.</summary>
  internal long StreamOffset { get; init; }

  internal static DateTime? FromDos(ushort date, ushort time) {
    var day = date & 0x1F;
    var month = (date >> 5) & 0x0F;
    var year = 1980 + (date >> 9);
    var second = (time & 0x1F) * 2;
    var minute = (time >> 5) & 0x3F;
    var hour = time >> 11;
    if (day == 0 || month is 0 or > 12 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
      return null;

    return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
  }
}
