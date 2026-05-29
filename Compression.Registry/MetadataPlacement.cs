#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Which zone a metadata chunk should be placed in relative to the primary
/// data payload during file-internal optimization.
/// </summary>
public enum PlacementZone {
  /// <summary>Place the chunk before the primary data payload.</summary>
  BeforeData,
  /// <summary>Place the chunk after the primary data payload.</summary>
  AfterData,
  /// <summary>Remove the chunk entirely during optimization.</summary>
  Remove,
}

/// <summary>
/// Maps a chunk type (e.g. "eXIf", "APP1", "moov", "idx1") to a
/// <see cref="PlacementZone"/>. Used by file-internal optimizers to
/// decide where metadata chunks land relative to the data payload.
/// </summary>
public sealed record MetadataPlacementRule(
  string ChunkType,
  PlacementZone Zone);

/// <summary>
/// A named set of <see cref="MetadataPlacementRule"/>s that controls
/// where metadata chunks are placed during file-internal optimization.
/// Optimizers that accept this profile apply matching rules; chunks
/// not covered by any rule keep their format-specific default placement.
/// </summary>
public sealed class MetadataPlacementProfile {
  /// <summary>Rules keyed by chunk type. Order is irrelevant; lookup is by type.</summary>
  public IReadOnlyList<MetadataPlacementRule> Rules { get; init; } = [];

  /// <summary>Human-readable label for UI display.</summary>
  public string Name { get; init; } = "Custom";

  /// <summary>
  /// Looks up the placement zone for a given chunk type.
  /// Returns <c>null</c> when no rule matches (caller should fall back
  /// to the format-specific default).
  /// </summary>
  public PlacementZone? GetZone(string chunkType) {
    foreach (var rule in Rules)
      if (rule.ChunkType.Equals(chunkType, System.StringComparison.OrdinalIgnoreCase))
        return rule.Zone;
    return null;
  }

  /// <summary>
  /// All metadata chunks placed before the data payload. The optimizer
  /// should load metadata first for fastest access.
  /// </summary>
  public static MetadataPlacementProfile MetadataFirst { get; } = new() {
    Name = "Metadata first",
    Rules = [
      // PNG
      new("eXIf", PlacementZone.BeforeData),
      new("tEXt", PlacementZone.BeforeData),
      new("iTXt", PlacementZone.BeforeData),
      new("zTXt", PlacementZone.BeforeData),
      new("iCCP", PlacementZone.BeforeData),
      // MP4
      new("moov", PlacementZone.BeforeData),
      // JPEG
      new("APP1", PlacementZone.BeforeData),
      new("APP2", PlacementZone.BeforeData),
      new("APP13", PlacementZone.BeforeData),
      // AVI
      new("idx1", PlacementZone.BeforeData),
      // MKV
      new("Cues", PlacementZone.BeforeData),
      // WAV
      new("LIST", PlacementZone.AfterData),
      new("bext", PlacementZone.AfterData),
      // MP3
      new("ID3v2", PlacementZone.BeforeData),
    ],
  };

  /// <summary>
  /// Data payload first, metadata after. Optimizes for streaming playback
  /// at the cost of slower metadata access.
  /// </summary>
  public static MetadataPlacementProfile DataFirst { get; } = new() {
    Name = "Data first",
    Rules = [
      // PNG
      new("eXIf", PlacementZone.AfterData),
      new("tEXt", PlacementZone.AfterData),
      new("iTXt", PlacementZone.AfterData),
      new("zTXt", PlacementZone.AfterData),
      new("iCCP", PlacementZone.AfterData),
      // MP4
      new("moov", PlacementZone.AfterData),
      // JPEG
      new("APP1", PlacementZone.AfterData),
      new("APP2", PlacementZone.AfterData),
      new("APP13", PlacementZone.AfterData),
      // AVI
      new("idx1", PlacementZone.AfterData),
      // MKV
      new("Cues", PlacementZone.AfterData),
      // WAV
      new("LIST", PlacementZone.AfterData),
      new("bext", PlacementZone.AfterData),
      // MP3
      new("ID3v2", PlacementZone.BeforeData),
    ],
  };

  /// <summary>
  /// No rules — each optimizer uses its own format-specific default.
  /// </summary>
  public static MetadataPlacementProfile Default { get; } = new() {
    Name = "Format default",
    Rules = [],
  };
}
