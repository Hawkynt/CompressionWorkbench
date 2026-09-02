#pragma warning disable CS1591
namespace FileSystem.SmartFs;

/// <summary>
/// Represents a smart fs entry.
/// </summary>
public sealed class SmartFsEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
    /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
    /// <summary>
  /// Gets or sets the data.
  /// </summary>
public byte[] Data { get; init; } = [];
}
