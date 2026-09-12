namespace FileFormat.Rar;

/// <summary>
/// Represents an entry in a RAR archive.
/// </summary>
public sealed class RarEntry {
  /// <summary>Gets or sets the entry name (including path within the archive).</summary>
  public string Name { get; set; } = "";

  /// <summary>Gets or sets the uncompressed size in bytes.</summary>
  public long Size { get; set; }

  /// <summary>Gets or sets the compressed size in bytes.</summary>
  public long CompressedSize { get; set; }

  /// <summary>Gets or sets a value indicating whether this entry is a directory.</summary>
  public bool IsDirectory { get; set; }

  /// <summary>Gets or sets the last modification time, or <see langword="null"/> if not stored.</summary>
  public DateTimeOffset? ModifiedTime { get; set; }

  /// <summary>Gets or sets the CRC-32 of the uncompressed data, or <see langword="null"/> if not stored.</summary>
  public uint? Crc { get; set; }

  /// <summary>Gets or sets the compression method used (0 = Store, 1-5 = compressed).</summary>
  public int CompressionMethod { get; set; }

  /// <summary>Gets or sets a value indicating whether this entry uses solid compression (dictionary carries over from previous file).</summary>
  public bool IsSolid { get; set; }

  /// <summary>Gets or sets a value indicating whether this entry is encrypted.</summary>
  public bool IsEncrypted { get; set; }
}
