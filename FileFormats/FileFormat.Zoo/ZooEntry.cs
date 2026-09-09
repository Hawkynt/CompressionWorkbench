namespace FileFormat.Zoo;

/// <summary>
/// Compression method used for a <see cref="ZooEntry"/>.
/// </summary>
public enum ZooCompressionMethod : byte {
  /// <summary>File data is stored verbatim with no compression.</summary>
  Store = ZooConstants.MethodStore,

  /// <summary>File data is compressed using normal Zoo LZW (9–13 bit, LSB-first).</summary>
  Lzw = ZooConstants.MethodLzw,
}

/// <summary>
/// Represents a single file entry in a Zoo archive.
/// </summary>
public sealed class ZooEntry {
  /// <summary>Gets or sets the short filename (up to 12 characters).</summary>
  public string FileName { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the portable long pathname carried by a type-2 directory entry.
  /// </summary>
  public string? LongFileName { get; set; }

  /// <summary>Gets the effective display name.</summary>
  public string EffectiveName => !string.IsNullOrEmpty(this.LongFileName) ? this.LongFileName! : this.FileName;

  /// <summary>Gets or sets the compression method.</summary>
  public ZooCompressionMethod CompressionMethod { get; set; }

  /// <summary>Gets or sets the CRC-16 of the uncompressed data.</summary>
  public ushort Crc16 { get; set; }

  /// <summary>Gets or sets the uncompressed size in bytes.</summary>
  public uint OriginalSize { get; set; }

  /// <summary>Gets or sets the compressed size in bytes.</summary>
  public uint CompressedSize { get; set; }

  /// <summary>Gets or sets the last modification date/time.</summary>
  public DateTime LastModified { get; set; } = new(1980, 1, 1);

  /// <summary>Gets or sets whether this entry has been marked as deleted.</summary>
  public bool IsDeleted { get; set; }

  /// <summary>Gets or sets the minimum Zoo major version required to extract this entry.</summary>
  public byte MajorVersion { get; set; } = ZooConstants.ExtractMajorVersion;

  /// <summary>Gets or sets the minimum Zoo minor version required to extract this entry.</summary>
  public byte MinorVersion { get; set; } = ZooConstants.ExtractMinorVersion;

  /// <summary>Offset of this entry's directory header within the archive stream.</summary>
  internal long HeaderOffset { get; set; }

  /// <summary>Offset at which the compressed member data begins.</summary>
  internal long DataOffset { get; set; }

  /// <summary>Canonical directory-entry byte length.</summary>
  internal int DirectorySize { get; set; }

  /// <summary>Encodes a <see cref="DateTime"/> as MS-DOS date/time words.</summary>
  internal static (ushort Date, ushort Time) ToMsDosDateTime(DateTime dt) {
    if (dt.Year < 1980)
      dt = new DateTime(1980, 1, 1);

    var date = (ushort)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day);
    var time = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
    return (date, time);
  }

  /// <summary>Decodes MS-DOS date/time words.</summary>
  internal static DateTime FromMsDosDateTime(ushort date, ushort time) {
    var year = ((date >> 9) & 0x7F) + 1980;
    var month = Math.Clamp((date >> 5) & 0x0F, 1, 12);
    var day = Math.Clamp(date & 0x1F, 1, 31);
    var hour = Math.Clamp((time >> 11) & 0x1F, 0, 23);
    var min = Math.Clamp((time >> 5) & 0x3F, 0, 59);
    var sec = Math.Clamp((time & 0x1F) * 2, 0, 59);

    try {
      return new DateTime(year, month, day, hour, min, sec);
    } catch (ArgumentOutOfRangeException) {
      return new DateTime(1980, 1, 1);
    }
  }
}
