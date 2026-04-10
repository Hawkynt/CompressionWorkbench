namespace FileFormat.Big;

/// <summary>
/// Represents a single file entry in a BIG archive.
/// </summary>
public sealed class BigEntry {
  /// <summary>Gets the full path of the entry within the archive, using forward slashes.</summary>
  public string Path { get; init; } = string.Empty;

  /// <summary>Gets the uncompressed size of the entry data in bytes.</summary>
  public int Size { get; init; }

  /// <summary>Gets the absolute byte offset of the entry data within the archive stream.</summary>
  public long DataOffset { get; init; }
}
