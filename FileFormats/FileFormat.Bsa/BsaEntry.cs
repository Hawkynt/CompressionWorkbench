namespace FileFormat.Bsa;

/// <summary>Entry in a BSA/BA2 archive.</summary>
public sealed class BsaEntry {
  public string FileName { get; init; } = "";
  public string FolderPath { get; init; } = "";
  public long OriginalSize { get; init; }
  public long CompressedSize { get; init; }
  public bool IsCompressed { get; init; }
  public long Offset { get; init; }

  /// <summary>Full path: folder\filename</summary>
  public string FullPath => FolderPath == "" ? FileName : FolderPath + "\\" + FileName;
}
