#pragma warning disable CS1591
namespace FileFormat.CpcDsk;

/// <summary>
/// Represents a single sector in a CPC DSK disk image.
/// </summary>
public sealed class CpcDskEntry {
  /// <summary>Display name, formatted as "T{track:D2}S{side}_{sectorId:X2}".</summary>
  public string Name { get; init; } = "";
  /// <summary>Physical track number (0-based).</summary>
  public int Track { get; init; }
  /// <summary>Physical side number (0 or 1).</summary>
  public int Side { get; init; }
  /// <summary>Sector ID byte as stored in the sector info block (not necessarily 1-based).</summary>
  public byte SectorId { get; init; }
  /// <summary>Size of the sector data in bytes.</summary>
  public int Size { get; init; }
  /// <summary>Absolute byte offset of the sector data within the DSK stream.</summary>
  internal long DataOffset { get; init; }
}
