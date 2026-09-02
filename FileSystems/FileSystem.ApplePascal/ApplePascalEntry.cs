#pragma warning disable CS1591
namespace FileSystem.ApplePascal;

/// <summary>
/// Represents an apple pascal entry.
/// </summary>
public sealed class ApplePascalEntry {
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
  /// Gets or sets the start block.
  /// </summary>
public int StartBlock { get; init; }
  /// <summary>
  /// Gets or sets the end block.
  /// </summary>
public int EndBlock { get; init; }
  /// <summary>
  /// Gets or sets the file kind.
  /// </summary>
public int FileKind { get; init; }
  /// <summary>
  /// Gets or sets the bytes in last block.
  /// </summary>
public int BytesInLastBlock { get; init; }
}
