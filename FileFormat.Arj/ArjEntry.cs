namespace FileFormat.Arj;

/// <summary>
/// Represents a single entry (file or directory) within an ARJ archive.
/// </summary>
public sealed class ArjEntry {
  /// <summary>Gets or sets the file name (may include a relative path).</summary>
  public string FileName { get; set; } = string.Empty;

  /// <summary>Gets or sets the file comment.</summary>
  public string Comment { get; set; } = string.Empty;

  /// <summary>Gets or sets the compression method.</summary>
  public byte Method { get; set; }

  /// <summary>Gets or sets the file type.</summary>
  public byte FileType { get; set; }

  /// <summary>Gets or sets the host OS on which the file was archived.</summary>
  public byte HostOs { get; set; }

  /// <summary>Gets or sets the ARJ flags byte.</summary>
  public byte Flags { get; set; }

  /// <summary>Gets or sets the compressed size in bytes.</summary>
  public uint CompressedSize { get; set; }

  /// <summary>Gets or sets the original (uncompressed) size in bytes.</summary>
  public uint OriginalSize { get; set; }

  /// <summary>Gets or sets the CRC-32 of the original (uncompressed) data.</summary>
  public uint Crc32 { get; set; }

  /// <summary>Gets or sets the last modification timestamp in MS-DOS format.</summary>
  public uint MsdosTimestamp { get; set; }

  /// <summary>Gets or sets the file access mode (MS-DOS attribute bits).</summary>
  public ushort FileMode { get; set; }

  /// <summary>
  /// Gets whether this entry represents a directory.
  /// </summary>
  public bool IsDirectory => this.FileType == ArjConstants.FileTypeDirectory;

  /// <summary>
  /// Gets the last modification time decoded from the MS-DOS timestamp.
  /// Returns <see cref="DateTime.MinValue"/> if the timestamp is invalid.
  /// </summary>
  public DateTime LastModified {
    get {
      int time = (int)(this.MsdosTimestamp & 0xFFFF);
      int date = (int)(this.MsdosTimestamp >> 16);
      try {
        return new DateTime(
          ((date >> 9) & 0x7F) + 1980,
          (date >> 5) & 0x0F,
          date & 0x1F,
          (time >> 11) & 0x1F,
          (time >> 5) & 0x3F,
          (time & 0x1F) * 2,
          DateTimeKind.Local);
      } catch {
        return DateTime.MinValue;
      }
    }
  }

  /// <summary>Gets the byte offset of the compressed data within the source archive stream.</summary>
  internal long DataOffset { get; set; }
}
