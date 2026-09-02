#pragma warning disable CS1591
namespace FileSystem.Fatx;

/// <summary>Single directory record from an Xbox FATX volume.</summary>
public sealed class FatxEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
  public long Size { get; init; }
  /// <summary>
  /// Gets or sets the first cluster.
  /// </summary>
  public uint FirstCluster { get; init; }
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
  public bool IsDirectory { get; init; }
}
