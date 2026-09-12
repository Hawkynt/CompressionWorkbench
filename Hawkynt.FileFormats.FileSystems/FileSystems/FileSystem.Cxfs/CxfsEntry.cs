#pragma warning disable CS1591
namespace FileSystem.Cxfs;

/// <summary>
/// Represents a cxfs entry.
/// </summary>
public sealed class CxfsEntry {
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
  /// Gets or sets the offset.
  /// </summary>
  public long Offset { get; init; }
  /// <summary>
  /// Gets or sets the data.
  /// </summary>
  public byte[] Data { get; init; } = [];

  /// <summary>
  /// True when this entry was produced by the XFS reader walking the
  /// underlying XFS layer (R/O delegation). False for the synthetic
  /// <c>metadata.ini</c>/<c>cxfs-volume.bin</c> Stage-0 fallback entries
  /// emitted when the XFS reader cannot read the image.
  /// </summary>
  public bool FromXfsLayer { get; init; }
}
