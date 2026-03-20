namespace FileFormat.SevenZip;

/// <summary>
/// Represents a single entry (file or directory) in a 7z archive.
/// </summary>
public sealed class SevenZipEntry {
  /// <summary>Gets or sets the file name (including path within the archive).</summary>
  public string Name { get; set; } = "";

  /// <summary>Gets or sets the uncompressed size in bytes.</summary>
  public long Size { get; set; }

  /// <summary>Gets or sets whether this entry is a directory.</summary>
  public bool IsDirectory { get; set; }

  /// <summary>Gets or sets the last write time in UTC.</summary>
  public DateTime? LastWriteTime { get; set; }

  /// <summary>Gets or sets the creation time in UTC.</summary>
  public DateTime? CreationTime { get; set; }

  /// <summary>Gets or sets the compressed size in bytes (-1 if unknown).</summary>
  public long CompressedSize { get; set; } = -1;

  /// <summary>Gets or sets the compression method name.</summary>
  public string Method { get; set; } = "";

  /// <summary>Gets or sets the CRC-32 of the uncompressed data.</summary>
  public uint? Crc { get; set; }

  /// <summary>Gets or sets the Windows file attributes.</summary>
  public uint? Attributes { get; set; }
}
