namespace FileFormat.Lfd;

/// <summary>
/// Represents a single resource entry inside a LucasArts LFD bundle.
/// </summary>
public sealed class LfdEntry {
  /// <summary>Gets the 4-character resource type (e.g. "BMAP", "DELT", "VOIC", "RMAP").</summary>
  public string Type { get; init; } = "";

  /// <summary>Gets the entry's 8-character name.</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the display name (Type + "." + Name) used by tools to surface entries.</summary>
  public string DisplayName { get; init; } = "";

  /// <summary>Gets the byte offset of the payload from the start of the LFD stream.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the payload size in bytes.</summary>
  public long Size { get; init; }
}
