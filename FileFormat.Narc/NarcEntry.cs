namespace FileFormat.Narc;

/// <summary>
/// Represents a single file inside a NARC archive.
/// </summary>
public sealed class NarcEntry {
  /// <summary>Gets the entry name (from BTNF, or a synthesized <c>file_NNNN.bin</c> if BTNF is non-flat).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute offset into the source stream where this entry's data begins.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the length in bytes of this entry's data.</summary>
  public long Size { get; init; }
}
