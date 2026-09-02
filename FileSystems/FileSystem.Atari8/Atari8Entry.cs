#pragma warning disable CS1591
namespace FileSystem.Atari8;

/// <summary>
/// Directory entry in an Atari 8-bit AtariDOS 2.x <c>.atr</c> disk image.
/// </summary>
public sealed class Atari8Entry {
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
public bool IsDirectory => false;
  /// <summary>AtariDOS flags byte: bit 7=deleted, bit 6=in-use, bit 5=locked,
  /// bit 1=DOS-2 file, bit 0=open for write.</summary>
  public byte Flags { get; init; }
  /// <summary>Sector count stored in the directory slot.</summary>
  public int SectorCount { get; init; }
  /// <summary>First sector of the file's chain.</summary>
  public int StartSector { get; init; }
}
