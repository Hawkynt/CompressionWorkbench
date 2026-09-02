#pragma warning disable CS1591
namespace FileSystem.Trsdos;

/// <summary>
/// Represents a trsdos entry.
/// </summary>
public sealed class TrsdosEntry {
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
  /// Gets or sets the attributes.
  /// </summary>
public byte Attributes { get; init; }
}
