#pragma warning disable CS1591
namespace FileSystem.DriveSpace3;

/// <summary>
/// One file entry inside a DriveSpace 3 CVF. Names with characters or lengths
/// outside the 8.3 FAT subset are surfaced via the inner volume's VFAT LFN
/// chain.
/// </summary>
public sealed class DriveSpace3Entry {
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
  internal int SectorCount { get; init; }
}
