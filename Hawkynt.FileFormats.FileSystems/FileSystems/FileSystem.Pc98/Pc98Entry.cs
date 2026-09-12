#pragma warning disable CS1591
namespace FileSystem.Pc98;

/// <summary>
/// Represents a pc 98 entry.
/// </summary>
public sealed class Pc98Entry {
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
  /// Gets or sets the first cluster.
  /// </summary>
  public int FirstCluster { get; init; }
  /// <summary>
  /// Gets or sets the attributes.
  /// </summary>
  public byte Attributes { get; init; }
}
