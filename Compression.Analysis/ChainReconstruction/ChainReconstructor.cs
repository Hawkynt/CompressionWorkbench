using System.Security.Cryptography;
using Compression.Analysis.Fingerprinting;
using Compression.Analysis.TrialDecompression;
using Compression.Registry;

namespace Compression.Analysis.ChainReconstruction;

/// <summary>
/// Iteratively peels layers off compressed data, reconstructing the full compression chain.
/// Uses fingerprinting to guide trial decompression at each level.
/// </summary>
public sealed class ChainReconstructor {

  private readonly AlgorithmFingerprinter _fingerprinter = new();
  private readonly TrialDecompressor _decompressor;
  private readonly int _maxDepth;
  private readonly int _perTrialTimeoutMs;

  /// <summary>
  /// Creates a chain reconstructor.
  /// </summary>
  /// <param name="maxDepth">Maximum chain depth to attempt (default 10).</param>
  /// <param name="perTrialTimeoutMs">Base per-trial timeout in milliseconds (default 200).
  /// The blind-trial sweep scales this up with the layer size so a large layer is not
  /// abandoned mid-decompress.</param>
  public ChainReconstructor(int maxDepth = 10, int perTrialTimeoutMs = 200) {
    _maxDepth = maxDepth;
    _perTrialTimeoutMs = perTrialTimeoutMs;
    _decompressor = new TrialDecompressor(perTrialTimeoutMs: perTrialTimeoutMs);
  }

  // The blind-trial budget scales with the layer size (a megabyte-scale layer needs
  // more than the 200 ms a few-KB header does) but is capped so one pathological
  // strategy cannot stall the whole sweep.
  private int BlindTrialTimeoutMs(int inputLength) {
    var scaled = (long)_perTrialTimeoutMs + inputLength / 1024; // +1 ms per KiB
    return (int)Math.Min(scaled, 5000);
  }

  /// <summary>
  /// Attempts to reconstruct the compression chain by iteratively decompressing.
  /// </summary>
  public CompressionChain Reconstruct(ReadOnlySpan<byte> data) {
    var layers = new List<ChainLayer>();
    var current = data.ToArray();
    var seenHashes = new HashSet<string> { ComputeHash(current) };

    for (var depth = 0; depth < _maxDepth; depth++) {
      // First try: archive extraction (containers like ZIP, 7z, RAR).
      var archiveResult = TryArchiveExtract(current, seenHashes);
      if (archiveResult != null) {
        current = archiveResult.Value.Output;
        layers.Add(new ChainLayer(
          archiveResult.Value.Algorithm,
          current.Length,
          archiveResult.Value.Output.Length,
          archiveResult.Value.Confidence,
          current
        ));
        seenHashes.Add(ComputeHash(current));
        continue;
      }

      var bestResult = TryFingerprintGuided(current, seenHashes);
      bestResult ??= TryBlindTrial(current, seenHashes);
      if (bestResult == null) break;

      current = bestResult.Value.Output;
      layers.Add(new ChainLayer(
        bestResult.Value.Algorithm,
        current.Length,
        bestResult.Value.Output.Length,
        bestResult.Value.Confidence,
        current
      ));
      seenHashes.Add(ComputeHash(current));
    }

    return new CompressionChain(layers, current);
  }

  private (string Algorithm, byte[] Output, double Confidence)? TryFingerprintGuided(
    byte[] current, HashSet<string> seenHashes) {

    var candidates = _fingerprinter.Analyze(current);

    foreach (var candidate in candidates) {
      if (candidate.Confidence < 0.3) continue;

      // Map the fingerprint to an ordered set of likely raw-algorithm trials and
      // try each in priority order; the blind sweep remains the safety net for
      // anything not covered here.
      foreach (var trialName in MapToTrialNames(candidate.Algorithm)) {
        var attempt = _decompressor.TryOne(current, trialName);
        if (!attempt.Success || attempt.Output == null) continue;

        var quality = SuccessEvaluator.Evaluate(current, attempt.Output, trialName);
        var outputHash = ComputeHash(attempt.Output);
        if (quality.IsImprovement && !seenHashes.Contains(outputHash))
          return (trialName, attempt.Output, candidate.Confidence);
      }
    }

    return null;
  }

  private (string Algorithm, byte[] Output, double Confidence)? TryBlindTrial(
    byte[] current, HashSet<string> seenHashes) {

    foreach (var strategy in TrialRegistry.All) {
      // Skip magic detections and archive listings — they don't produce decompressed data.
      if (strategy.Category is TrialCategory.Magic or TrialCategory.Archive) continue;

      try {
        using var cts = new CancellationTokenSource(BlindTrialTimeoutMs(current.Length));
        var attempt = strategy.TryDecompress(current, (int)Math.Min((long)current.Length * 4, 1024 * 1024), cts.Token);
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

  // Ordered raw-algorithm trial candidates for each fingerprint class. A precise
  // fingerprint maps to a single trial; the generic classes ("Dictionary-Compressed",
  // "Strong-Compression") map to a priority-ordered shortlist of the raw trials most
  // likely to peel them, so a confident-but-unspecific fingerprint still gets a fast
  // path instead of falling straight through to the full blind sweep. Truly random /
  // encrypted data maps to nothing (there is no layer to peel).
  private static readonly string[] NoCandidates = [];

  private static string[] MapToTrialNames(string fingerprintAlgorithm) => fingerprintAlgorithm switch {
    "Deflate" => ["Deflate"],
    "RLE" => ["RLE"],
    "MTF" => ["MTF"],
    "BWT" => ["BWT"],
    "LZW" => ["LZW"],
    "LZ/LZSS" => ["LZSS"],
    "Huffman" => ["Huffman"],
    // Generic dictionary signature: try the byte-oriented dictionary coders first.
    "Dictionary-Compressed" => ["Deflate", "LZSS", "LZW"],
    // High-entropy "strong" output: most likely Deflate-family or an entropy stage;
    // try the dictionary coders then the entropy/transform trials.
    "Strong-Compression" => ["Deflate", "LZSS", "LZW", "Huffman", "BWT"],
    "Arithmetic" => NoCandidates,     // No raw arithmetic trial strategy
    "Encrypted/Random" => NoCandidates, // Nothing to peel
    _ => NoCandidates,
  };

  /// <summary>
  /// Tries to detect the data as an archive and extract the first file's content.
  /// Returns the extracted content so chain analysis can continue on it.
  /// </summary>
  private static (string Algorithm, byte[] Output, double Confidence)? TryArchiveExtract(
    byte[] current, HashSet<string> seenHashes) {

    try {
      Compression.Lib.FormatRegistration.EnsureInitialized();

      foreach (var desc in FormatRegistry.All) {
        if (desc.Category is not FormatCategory.Archive) continue;
        if (!desc.Capabilities.HasFlag(FormatCapabilities.CanList)) continue;
        if (!desc.Capabilities.HasFlag(FormatCapabilities.CanExtract)) continue;

        // Quick magic check first.
        var magicMatch = false;
        foreach (var sig in desc.MagicSignatures) {
          if (current.Length < sig.Offset + sig.Bytes.Length) continue;
          var match = true;
          for (var i = 0; i < sig.Bytes.Length && match; i++) {
            var b = current[sig.Offset + i];
            if (sig.Mask != null) b = (byte)(b & sig.Mask[i]);
            if (b != sig.Bytes[i]) match = false;
          }
          if (match) { magicMatch = true; break; }
        }
        if (!magicMatch) continue;

        // List entries via MemoryStream (listing usually works fine).
        List<ArchiveEntryInfo>? entries;
        try {
          using var input = new MemoryStream(current);
          var ops = FormatRegistry.GetArchiveOps(desc.Id);
          if (ops == null) continue;
          entries = ops.List(input, null);
        } catch { continue; }

        var firstFile = entries?.FirstOrDefault(e => !e.IsDirectory && e.OriginalSize > 0);
        if (firstFile == null) continue;

        // Extract via temp file (most reliable).
        var tempArchive = Path.Combine(Path.GetTempPath(), "cwb_chain_" + Guid.NewGuid().ToString("N")[..8] + desc.DefaultExtension);
        var tempDir = tempArchive + "_out";
        try {
          File.WriteAllBytes(tempArchive, current);
          Directory.CreateDirectory(tempDir);
          try {
            Compression.Lib.ArchiveOperations.Extract(tempArchive, tempDir, null, [firstFile.Name]);
          } catch {
            try { Compression.Lib.ArchiveOperations.Extract(tempArchive, tempDir, null, null); } catch { continue; }
          }

          var candidates = Directory.Exists(tempDir)
            ? Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
            : [];
          var match = candidates.FirstOrDefault(f => Path.GetFileName(f) == Path.GetFileName(firstFile.Name))
                      ?? candidates.FirstOrDefault();
          if (match == null) continue;

          var extracted = File.ReadAllBytes(match);
          if (extracted.Length == 0) continue;

          var outputHash = ComputeHash(extracted);
          if (seenHashes.Contains(outputHash)) continue;

          return ($"{desc.DisplayName} container \u2192 {firstFile.Name}", extracted, 0.95);
        } finally {
          try { File.Delete(tempArchive); } catch { }
          try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
      }
    } catch { /* archive detection failed */ }

    return null;
  }

  private static string ComputeHash(byte[] data) {
    var hash = SHA256.HashData(data);
    return Convert.ToHexString(hash);
  }
}
