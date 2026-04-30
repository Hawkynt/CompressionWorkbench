namespace FileFormat.Psarc;

/// <summary>
/// Represents a single entry inside a PSARC archive.
/// </summary>
public sealed class PsarcEntry {
  /// <summary>Gets the relative, forward-slash, lowercase path of this entry (manifest-derived; empty for the manifest itself).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the original (uncompressed) size in bytes.</summary>
  public long OriginalSize { get; init; }

  /// <summary>Gets the total compressed size on disk in bytes (sum of all blocks belonging to this entry).</summary>
  public long CompressedSize { get; init; }

  /// <summary>Gets the index into the block-sizes table where this entry's first block lives.</summary>
  public int StartBlockIndex { get; init; }

  /// <summary>Gets the absolute byte offset into the archive where this entry's first compressed block begins.</summary>
  public long StartOffset { get; init; }
}
