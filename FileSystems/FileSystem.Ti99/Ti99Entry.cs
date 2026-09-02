#pragma warning disable CS1591
namespace FileSystem.Ti99;

/// <summary>
/// Represents a ti 99 entry.
/// </summary>
public sealed class Ti99Entry {
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
  /// Gets or sets the first sector.
  /// </summary>
  public int FirstSector { get; init; }
  /// <summary>
  /// Gets or sets the sector count.
  /// </summary>
  public int SectorCount { get; init; }
  /// <summary>
  /// Gets or sets the file flags.
  /// </summary>
  public byte FileFlags { get; init; }
  /// <summary>
  /// Gets or sets the records per sector.
  /// </summary>
  public byte RecordsPerSector { get; init; }
}
