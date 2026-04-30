namespace FileFormat.Pbp;

/// <summary>
/// Represents a single section in a PSP PBP archive.
/// </summary>
public sealed class PbpEntry {
  /// <summary>Gets the fixed section name (one of the eight PBP section names).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the offset of this section's payload from the start of the PBP file.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the size in bytes of this section's payload.</summary>
  public long Size { get; init; }
}
