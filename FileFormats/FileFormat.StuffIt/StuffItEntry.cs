namespace FileFormat.StuffIt;

/// <summary>
/// Represents a single file entry in a StuffIt (SIT) archive.
/// </summary>
public sealed class StuffItEntry {
  /// <summary>Gets or sets the file name as stored in the archive (up to 63 characters).</summary>
  public string FileName { get; set; } = string.Empty;

  /// <summary>Gets or sets the uncompressed size of the data fork in bytes.</summary>
  public long DataForkSize { get; set; }

  /// <summary>Gets or sets the uncompressed size of the resource fork in bytes.</summary>
  public long ResourceForkSize { get; set; }

  /// <summary>Gets or sets the compressed size of the data fork in bytes.</summary>
  public long CompressedDataSize { get; set; }

  /// <summary>Gets or sets the compressed size of the resource fork in bytes.</summary>
  public long CompressedResourceSize { get; set; }

  /// <summary>Gets or sets the compression method code for the data fork.</summary>
  public int DataMethod { get; set; }

  /// <summary>Gets or sets the compression method code for the resource fork.</summary>
  public int ResourceMethod { get; set; }

  /// <summary>Gets or sets the Mac four-character file type code (e.g. "TEXT").</summary>
  public string FileType { get; set; } = "    ";

  /// <summary>Gets or sets the Mac four-character file creator code.</summary>
  public string FileCreator { get; set; } = "    ";

  /// <summary>Gets or sets the last modification date/time of the file.</summary>
  public DateTime LastModified { get; set; }

  /// <summary>StuffIt classic archives do not support directory entries.</summary>
  public bool IsDirectory => false;

  // Internal: byte offset of the resource fork compressed data within the archive stream.
  internal long ResourceDataOffset { get; set; }

  // Internal: byte offset of the data fork compressed data within the archive stream.
  internal long DataForkOffset { get; set; }

  // Stored CRC-16 values for integrity checking.
  internal ushort DataForkCrc16     { get; set; }
  internal ushort ResourceForkCrc16 { get; set; }
}
