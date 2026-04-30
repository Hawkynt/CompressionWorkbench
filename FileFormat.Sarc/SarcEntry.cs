namespace FileFormat.Sarc;

/// <summary>
/// Represents a single file entry in a Nintendo SARC archive.
/// </summary>
public sealed class SarcEntry {

  /// <summary>Gets the entry name resolved from the SFNT string table (UTF-8 path).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the precomputed name hash stored in the SFAT entry.</summary>
  public uint NameHash { get; init; }

  /// <summary>Gets the absolute offset of this entry's data within the SARC stream.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the size in bytes of this entry's payload.</summary>
  public long Size { get; init; }
}
