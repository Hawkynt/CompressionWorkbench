namespace FileFormat.Gob;

/// <summary>
/// Represents a single entry in a Lucasarts GOB archive.
/// </summary>
public sealed class GobEntry {
  /// <summary>Gets the entry name (relative path with backslash separators, up to 127 ASCII bytes).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute byte offset of the entry data within the archive.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the entry data length in bytes (GOB stores files uncompressed).</summary>
  public long Size { get; init; }
}
