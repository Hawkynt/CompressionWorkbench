namespace FileFormat.Mpq;

/// <summary>Entry in an MPQ archive.</summary>
public sealed class MpqEntry {
    /// <summary>
  /// Gets or sets the file name.
  /// </summary>
public string FileName { get; init; } = "";
    /// <summary>
  /// Gets or sets the original size.
  /// </summary>
public long OriginalSize { get; init; }
    /// <summary>
  /// Gets or sets the compressed size.
  /// </summary>
public long CompressedSize { get; init; }
    /// <summary>
  /// Gets or sets the flags.
  /// </summary>
public uint Flags { get; init; }
    /// <summary>
  /// Gets or sets the file offset.
  /// </summary>
public long FileOffset { get; init; }

  /// <summary>File exists in archive.</summary>
  public bool Exists => (Flags & 0x80000000) != 0;
  /// <summary>File is compressed.</summary>
  public bool IsCompressed => (Flags & 0x00000200) != 0 || (Flags & 0x00000100) != 0;
  /// <summary>File is encrypted.</summary>
  public bool IsEncrypted => (Flags & 0x00010000) != 0;
  /// <summary>File is a single unit (not sector-based).</summary>
  public bool IsSingleUnit => (Flags & 0x01000000) != 0;
}
