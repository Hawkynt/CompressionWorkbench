namespace FileFormat.Hog;

/// <summary>
/// Represents a single file entry in a HOG archive.
/// </summary>
public sealed class HogEntry {
  /// <summary>Gets the file name (up to 13 characters).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the size of the file data in bytes.</summary>
  public int Size { get; init; }

  /// <summary>Gets the offset of the file data from the start of the HOG file.</summary>
  public long DataOffset { get; init; }
}
