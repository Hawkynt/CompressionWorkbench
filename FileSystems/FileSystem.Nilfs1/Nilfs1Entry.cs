#pragma warning disable CS1591
namespace FileSystem.Nilfs1;

/// <summary>
/// Represents a nilfs 1 entry.
/// </summary>
public sealed class Nilfs1Entry {
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

  /// <summary>
  /// Where the entry's bytes live in the image, for entries the reader leaves in
  /// place rather than copying. -1 when <see cref="Data" /> carries them.
  /// </summary>
  public long Offset { get; init; } = -1;
}
