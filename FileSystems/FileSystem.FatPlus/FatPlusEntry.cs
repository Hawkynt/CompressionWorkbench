#pragma warning disable CS1591
namespace FileSystem.FatPlus;

/// <summary>
/// A directory entry from a FAT+ volume. Identical shape to
/// <see cref="FileSystem.Fat.FatEntry"/> but with an extended (up to 8-byte)
/// file size to represent files larger than 4 GiB.
/// </summary>
public sealed class FatPlusEntry {
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
  internal int StartCluster { get; init; }
  /// <summary>
  /// Gets or sets the last modified.
  /// </summary>
public DateTime? LastModified { get; init; }
}
