#pragma warning disable CS1591
namespace FileFormat.Chm;

/// <summary>
/// Represents a single entry (file) within a CHM archive.
/// </summary>
public sealed class ChmEntry {
  /// <summary>The full path of the entry within the CHM.</summary>
  public string Path { get; init; } = string.Empty;

  /// <summary>The uncompressed size of the entry in bytes.</summary>
  public long Size { get; init; }

  /// <summary>The content section index (0 = uncompressed, 1 = LZX compressed).</summary>
  public int Section { get; init; }

  /// <summary>The byte offset of this entry within its content section.</summary>
  public long Offset { get; init; }
}
