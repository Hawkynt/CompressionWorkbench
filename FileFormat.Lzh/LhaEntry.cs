namespace FileFormat.Lzh;

/// <summary>
/// Represents an entry in an LHA/LZH archive.
/// </summary>
public sealed class LhaEntry {
  /// <summary>Gets or sets the file name.</summary>
  public string FileName { get; set; } = string.Empty;

  /// <summary>Gets or sets the compression method (e.g. "-lh5-").</summary>
  public string Method { get; set; } = LhaConstants.MethodLh5;

  /// <summary>Gets or sets the compressed size in bytes.</summary>
  public long CompressedSize { get; set; }

  /// <summary>Gets or sets the original (uncompressed) size in bytes.</summary>
  public long OriginalSize { get; set; }

  /// <summary>Gets or sets the last modification time.</summary>
  public DateTime LastModified { get; set; }

  /// <summary>Gets or sets the CRC-16 of the original data.</summary>
  public ushort Crc16 { get; set; }

  /// <summary>Gets or sets the header level (0, 1, or 2).</summary>
  public byte HeaderLevel { get; set; }

  /// <summary>Gets or sets the OS identifier.</summary>
  public byte OsId { get; set; }

  /// <summary>Gets the offset of the compressed data within the archive stream.</summary>
  internal long DataOffset { get; set; }
}
