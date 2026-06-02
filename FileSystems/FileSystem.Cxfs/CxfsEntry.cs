#pragma warning disable CS1591
namespace FileSystem.Cxfs;

public sealed class CxfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public long Offset { get; init; }
  public byte[] Data { get; init; } = [];

  /// <summary>
  /// True when this entry was produced by the XFS reader walking the
  /// underlying XFS layer (R/O delegation). False for the synthetic
  /// <c>metadata.ini</c>/<c>cxfs-volume.bin</c> Stage-0 fallback entries
  /// emitted when the XFS reader cannot read the image.
  /// </summary>
  public bool FromXfsLayer { get; init; }
}
