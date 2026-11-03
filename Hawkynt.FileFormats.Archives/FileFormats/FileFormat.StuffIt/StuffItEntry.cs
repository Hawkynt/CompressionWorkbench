namespace FileFormat.StuffIt;

/// <summary>
/// Represents a single file or folder entry in a StuffIt archive (classic "SIT!" or StuffIt 5).
/// </summary>
public sealed class StuffItEntry {
  /// <summary>Gets or sets the entry's path inside the archive, folders separated by '/'.</summary>
  public string FileName { get; set; } = string.Empty;

  /// <summary>Gets or sets the uncompressed size of the data fork in bytes.</summary>
  public long DataForkSize { get; set; }

  /// <summary>Gets or sets the uncompressed size of the resource fork in bytes.</summary>
  public long ResourceForkSize { get; set; }

  /// <summary>Gets or sets the compressed size of the data fork in bytes.</summary>
  public long CompressedDataSize { get; set; }

  /// <summary>Gets or sets the compressed size of the resource fork in bytes.</summary>
  public long CompressedResourceSize { get; set; }

  /// <summary>Gets or sets the compression method code for the data fork (encryption flag removed).</summary>
  public int DataMethod { get; set; }

  /// <summary>Gets or sets the compression method code for the resource fork (encryption flag removed).</summary>
  public int ResourceMethod { get; set; }

  /// <summary>Gets or sets the Mac four-character file type code (e.g. "TEXT").</summary>
  public string FileType { get; set; } = "    ";

  /// <summary>Gets or sets the Mac four-character file creator code.</summary>
  public string FileCreator { get; set; } = "    ";

  /// <summary>Gets or sets the last modification date/time of the file.</summary>
  public DateTime LastModified { get; set; }

  /// <summary>Gets or sets whether this entry is a folder.</summary>
  public bool IsDirectory { get; set; }

  /// <summary>Gets or sets whether the forks are encrypted (classic DES or StuffIt 5 RC4).</summary>
  public bool IsEncrypted { get; set; }

  /// <summary>Gets whether the entry carries a resource fork.</summary>
  public bool HasResourceFork => this.ResourceForkSize > 0 || this.CompressedResourceSize > 0;

  // Internal: byte offset of the resource fork compressed data within the archive stream.
  internal long ResourceDataOffset { get; set; }

  // Internal: byte offset of the data fork compressed data within the archive stream.
  internal long DataForkOffset { get; set; }

  // Stored CRC-16/ARC values for integrity checking (not used with method 15, which carries a CRC-32).
  internal ushort DataForkCrc16 { get; set; }
  internal ushort ResourceForkCrc16 { get; set; }
}
