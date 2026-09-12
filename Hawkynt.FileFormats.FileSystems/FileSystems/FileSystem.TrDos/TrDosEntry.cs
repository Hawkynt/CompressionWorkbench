#pragma warning disable CS1591
namespace FileSystem.TrDos;

/// <summary>
/// Represents a tr dos entry.
/// </summary>
public sealed class TrDosEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
  public long Size { get; init; }
  /// <summary>
  /// Gets or sets the data size.
  /// </summary>
  public int DataSize { get; init; }
  /// <summary>
  /// Gets or sets the start sector.
  /// </summary>
  public int StartSector { get; init; }
  /// <summary>
  /// Gets or sets the start track.
  /// </summary>
  public int StartTrack { get; init; }
  /// <summary>
  /// Gets or sets the length sectors.
  /// </summary>
  public int LengthSectors { get; init; }
  /// <summary>
  /// Gets or sets the file type.
  /// </summary>
  public char FileType { get; init; }
}
