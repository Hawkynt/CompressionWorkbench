#pragma warning disable CS1591
namespace FileFormat.PackDisk;

/// <summary>
/// Represents a pack disk entry.
/// </summary>
public sealed class PackDiskEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
    /// <summary>
  /// Gets or sets the compressed size.
  /// </summary>
public long CompressedSize { get; init; }
  internal int Offset { get; init; }
}
