namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Categorizes trial decompression strategies by their detection approach.
/// Used to replace fragile string-based matching (e.g., <c>StartsWith("Detected:")</c>)
/// with strongly-typed enum comparisons.
/// </summary>
public enum TrialCategory {
  /// <summary>Magic-byte signature detection (instant, no decompression).</summary>
  Magic,

  /// <summary>Stream format decompression (e.g., gzip, bzip2, xz).</summary>
  Stream,

  /// <summary>Archive format listing/extraction (e.g., ZIP, TAR, 7z).</summary>
  Archive,

  /// <summary>Building block algorithm (raw algorithm primitive without container).</summary>
  BuildingBlock,

  /// <summary>Hand-tuned primitive decompression heuristic (e.g., raw Deflate, RLE, BWT).</summary>
  Primitive
}

/// <summary>
/// Interface for trial decompression strategies.
/// Each strategy attempts to decompress data using a specific algorithm.
/// </summary>
public interface ITrialStrategy {
  /// <summary>Name of the algorithm this strategy attempts.</summary>
  string Algorithm { get; }

  /// <summary>The category of this trial strategy, used for filtering and early termination.</summary>
  TrialCategory Category { get; }

  /// <summary>
  /// Attempts to decompress the data. Returns a DecompressionAttempt with results.
  /// </summary>
  /// <param name="data">Input data to try decompressing.</param>
  /// <param name="maxOutput">Maximum output size in bytes.</param>
  /// <param name="ct">Cancellation token for timeout.</param>
  DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct);
}
