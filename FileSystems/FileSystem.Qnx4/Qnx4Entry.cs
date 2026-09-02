#pragma warning disable CS1591
namespace FileSystem.Qnx4;

/// <summary>Directory entry from a QNX4 filesystem.</summary>
public sealed class Qnx4Entry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
  /// <summary>
  /// Gets or sets the first extent block.
  /// </summary>
public uint FirstExtentBlock { get; init; }
  /// <summary>
  /// Gets or sets the extent block count.
  /// </summary>
public uint ExtentBlockCount { get; init; }
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
}
