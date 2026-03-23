using System.Security.Cryptography;
using Compression.Analysis.Fingerprinting;
using Compression.Analysis.TrialDecompression;

namespace Compression.Analysis.ChainReconstruction;

/// <summary>
/// Iteratively peels layers off compressed data, reconstructing the full compression chain.
/// Uses fingerprinting to guide trial decompression at each level.
/// </summary>
public sealed class ChainReconstructor {

  private readonly AlgorithmFingerprinter _fingerprinter = new();
  private readonly TrialDecompressor _decompressor;
  private readonly int _maxDepth;

  /// <summary>
  /// Creates a chain reconstructor.
  /// </summary>
  /// <param name="maxDepth">Maximum chain depth to attempt (default 10).</param>
  /// <param name="perTrialTimeoutMs">Per-trial timeout in milliseconds (default 200).</param>
  public ChainReconstructor(int maxDepth = 10, int perTrialTimeoutMs = 200) {
    _maxDepth = maxDepth;
    _decompressor = new TrialDecompressor(perTrialTimeoutMs: perTrialTimeoutMs);
  }

  /// <summary>
  /// Attempts to reconstruct the compression chain by iteratively decompressing.
  /// </summary>
  public CompressionChain Reconstruct(ReadOnlySpan<byte> data) {
    var layers = new List<ChainLayer>();
    var current = data.ToArray();
    var seenHashes = new HashSet<string> { ComputeHash(current) };

    for (var depth = 0; depth < _maxDepth; depth++) {
      var bestResult = TryFingerprintGuided(current, seenHashes);

      // Fallback: blind trial if fingerprinting didn't help
      bestResult ??= TryBlindTrial(current, seenHashes);

      if (bestResult == null) break;

      layers.Add(new ChainLayer(
        bestResult.Value.Algorithm,
        current.Length,
        bestResult.Value.Output.Length,
        bestResult.Value.Confidence
      ));

      current = bestResult.Value.Output;
      seenHashes.Add(ComputeHash(current));
    }

    return new CompressionChain(layers, current);
  }

  private (string Algorithm, byte[] Output, double Confidence)? TryFingerprintGuided(
    byte[] current, HashSet<string> seenHashes) {

    var candidates = _fingerprinter.Analyze(current);

    foreach (var candidate in candidates) {
      if (candidate.Confidence < 0.3) continue;

      // Map fingerprint algorithm name to trial strategy name
      var trialName = MapToTrialName(candidate.Algorithm);
      if (trialName == null) continue;

      var attempt = _decompressor.TryOne(current, trialName);
      if (!attempt.Success || attempt.Output == null) continue;

      var quality = SuccessEvaluator.Evaluate(current, attempt.Output, trialName);
      var outputHash = ComputeHash(attempt.Output);
      if (quality.IsImprovement && !seenHashes.Contains(outputHash))
        return (trialName, attempt.Output, candidate.Confidence);
    }

    return null;
  }

  private (string Algorithm, byte[] Output, double Confidence)? TryBlindTrial(
    byte[] current, HashSet<string> seenHashes) {

    foreach (var strategy in TrialRegistry.All) {
      try {
        using var cts = new CancellationTokenSource(200);
        var attempt = strategy.TryDecompress(current, Math.Min(current.Length * 4, 1024 * 1024), cts.Token);
        if (!attempt.Success || attempt.Output == null) continue;

        var quality = SuccessEvaluator.Evaluate(current, attempt.Output, strategy.Algorithm);
        var outputHash = ComputeHash(attempt.Output);
        if (quality.IsImprovement && !seenHashes.Contains(outputHash))
          return (strategy.Algorithm, attempt.Output, 0.3);
      }
      catch {
        // Skip failing strategy
      }
    }

    return null;
  }

  private static string? MapToTrialName(string fingerprintAlgorithm) => fingerprintAlgorithm switch {
    "Deflate" => "Deflate",
    "RLE" => "RLE",
    "MTF" => "MTF",
    "BWT" => "BWT",
    "LZW" => "LZW",
    "LZ/LZSS" => "LZSS",
    "Huffman" => "Huffman",
    "Arithmetic" => null, // No raw arithmetic trial
    "Dictionary-Compressed" => null, // Too generic
    "Strong-Compression" => null,
    "Encrypted/Random" => null,
    _ => null,
  };

  private static string ComputeHash(byte[] data) {
    var hash = SHA256.HashData(data);
    return Convert.ToHexString(hash);
  }
}
