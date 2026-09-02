namespace FileFormat.Bsa;

/// <summary>Entry in a BSA/BA2 archive.</summary>
public sealed class BsaEntry {
    /// <summary>
  /// Gets or sets the file name.
  /// </summary>
public string FileName { get; init; } = "";
    /// <summary>
  /// Gets or sets the folder path.
  /// </summary>
public string FolderPath { get; init; } = "";
    /// <summary>
  /// Gets or sets the original size.
  /// </summary>
public long OriginalSize { get; init; }
    /// <summary>
  /// Gets or sets the compressed size.
  /// </summary>
public long CompressedSize { get; init; }
    /// <summary>
  /// Gets a value indicating whether is compressed.
  /// </summary>
public bool IsCompressed { get; init; }
    /// <summary>
  /// Gets or sets the offset.
  /// </summary>
public long Offset { get; init; }

  /// <summary>Full path: folder\filename</summary>
  public string FullPath => FolderPath == "" ? FileName : FolderPath + "\\" + FileName;
}
