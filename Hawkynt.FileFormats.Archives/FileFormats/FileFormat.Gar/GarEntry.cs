namespace FileFormat.Gar;

/// <summary>
/// Represents a single file entry in a Nintendo 3DS GAR archive.
/// </summary>
public sealed class GarEntry {

  /// <summary>Gets the full filename including extension (e.g. "icon.bclim").</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute byte offset of the file payload from the start of the archive.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the size of the file payload in bytes.</summary>
  public long Size { get; init; }

  /// <summary>Gets the index into the file-type table giving this file's extension.</summary>
  public int TypeIndex { get; init; }
}
