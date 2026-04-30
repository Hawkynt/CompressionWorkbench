namespace FileFormat.Awb;

/// <summary>
/// Represents a single audio entry inside a CRI Audio Wave Bank (AFS2).
/// The wave bank stores raw codec payload (HCA, ADX, etc.) — payload bytes are surfaced verbatim.
/// </summary>
public sealed class AwbEntry {

  /// <summary>Synthetic name in the form <c>cue_NNNNN.bin</c> where NNNNN is the zero-padded cue ID.</summary>
  public string Name { get; init; } = "";

  /// <summary>Game-specific cue identifier (lookup key into the paired ACB cue sheet).</summary>
  public uint CueId { get; init; }

  /// <summary>Absolute byte offset of this entry's data inside the AWB file (already alignment-resolved).</summary>
  public long Offset { get; init; }

  /// <summary>Length of this entry's payload in bytes.</summary>
  public long Size { get; init; }
}
