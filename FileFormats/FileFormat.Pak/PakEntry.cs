namespace FileFormat.Pak;

/// <summary>One directory entry in a Quake PACK archive.</summary>
public sealed class PakEntry {
  /// <summary>Gets the archive-relative file name.</summary>
  public string FileName { get; init; } = "";

  /// <summary>Gets the absolute byte offset of the stored payload.</summary>
  public int FileOffset { get; init; }

  /// <summary>Gets the stored payload length in bytes.</summary>
  public int Size { get; init; }
}
