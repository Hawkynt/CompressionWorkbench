namespace FileFormat.Akb;

/// <summary>
/// Represents a single audio entry within a Square Enix AKB audio bank.
/// </summary>
public sealed class AkbEntry {
  /// <summary>Gets the synthetic display name (e.g. <c>entry_000.bin</c>).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute byte offset of the entry's audio data within the AKB stream.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the byte length of the entry's audio data.</summary>
  public long Size { get; init; }

  /// <summary>Gets the duration of the entry in samples (codec-dependent interpretation).</summary>
  public uint SampleCount { get; init; }

  /// <summary>Gets the per-entry flags word; bit 0 indicates a looping sample.</summary>
  public uint Flags { get; init; }
}
