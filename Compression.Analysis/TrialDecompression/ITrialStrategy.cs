namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Interface for trial decompression strategies.
/// Each strategy attempts to decompress data using a specific algorithm.
/// </summary>
public interface ITrialStrategy {
  /// <summary>Name of the algorithm this strategy attempts.</summary>
  string Algorithm { get; }

  /// <summary>
  /// Attempts to decompress the data. Returns a DecompressionAttempt with results.
  /// </summary>
  /// <param name="data">Input data to try decompressing.</param>
  /// <param name="maxOutput">Maximum output size in bytes.</param>
  /// <param name="ct">Cancellation token for timeout.</param>
  DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct);
}
