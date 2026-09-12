namespace FileFormat.Wad;

/// <summary>
/// Represents a single lump entry in a WAD archive.
/// </summary>
public sealed class WadEntry {
  /// <summary>Gets the lump name (up to 8 uppercase ASCII characters).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the size of the lump data in bytes.</summary>
  public int Size { get; init; }

  /// <summary>Gets the offset of the lump data from the start of the WAD file.</summary>
  public int DataOffset { get; init; }

  /// <summary>Gets whether this entry is a marker lump (zero-size lumps such as "MAP01", "S_START", etc.).</summary>
  public bool IsMarker => this.Size == 0;
}
