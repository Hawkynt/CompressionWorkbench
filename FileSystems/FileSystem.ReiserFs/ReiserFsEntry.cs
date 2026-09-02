#pragma warning disable CS1591
namespace FileSystem.ReiserFs;

/// <summary>
/// Represents a reiser fs entry.
/// </summary>
public sealed class ReiserFsEntry {
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
  /// Gets or sets the last modified.
  /// </summary>
public DateTime? LastModified { get; init; }
  internal uint DirId { get; init; }
  internal uint ObjectId { get; init; }
}
