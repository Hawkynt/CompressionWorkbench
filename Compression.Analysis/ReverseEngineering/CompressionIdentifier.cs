#pragma warning disable CS1591

using Compression.Registry;

namespace Compression.Analysis.ReverseEngineering;

/// <summary>
/// Identifies which compression primitive(s) an unknown tool uses by trying to decompress
/// the payload region with all known building blocks, and by analyzing entropy/structure.
/// </summary>
public static class CompressionIdentifier {

  /// <summary>Result of attempting to identify the compression algorithm.</summary>
  public sealed class IdentificationResult {
    /// <summary>Building blocks that successfully decompressed the payload.</summary>
    public required List<BuildingBlockMatch> Matches { get; init; }

    /// <summary>Known format signatures found within the payload.</summary>
    public required List<SignatureMatch> Signatures { get; init; }

    /// <summary>Entropy-based classification of the payload.</summary>
    public required string EntropyClass { get; init; }

    /// <summary>Best guess at the compression algorithm.</summary>
    public string? BestGuess => Matches.FirstOrDefault()?.DisplayName ?? Signatures.FirstOrDefault()?.FormatName;
  }

  /// <summary>A building block that successfully decompressed the payload.</summary>
  public sealed class BuildingBlockMatch {
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required AlgorithmFamily Family { get; init; }
    public required int DecompressedSize { get; init; }
    public required double Confidence { get; init; }
  }

  /// <summary>A known compression signature found in the payload.</summary>
  public sealed class SignatureMatch {
    public required int Offset { get; init; }
    public required string FormatName { get; init; }
    public required byte[] Signature { get; init; }
  }

  /// <summary>
  /// Known compression stream signatures to look for within payloads.
  /// </summary>
  private static readonly (string Name, byte[] Signature)[] KnownSignatures = [
    ("DEFLATE (zlib)", [0x78, 0x01]),        // zlib low compression
    ("DEFLATE (zlib)", [0x78, 0x5E]),        // zlib default
    ("DEFLATE (zlib)", [0x78, 0x9C]),        // zlib default
    ("DEFLATE (zlib)", [0x78, 0xDA]),        // zlib best
    ("LZMA", [0x5D, 0x00, 0x00]),           // LZMA properties byte + dict size start
    ("LZ4 frame", [0x04, 0x22, 0x4D, 0x18]),// LZ4 magic
    ("Zstd frame", [0x28, 0xB5, 0x2F, 0xFD]),// Zstd magic
    ("Brotli", [0xCE, 0xB2, 0xCF, 0x81]),   // Brotli sliding window
    ("Snappy", [0xFF, 0x06, 0x00, 0x00]),    // Snappy stream identifier
    ("DEFLATE raw", [0xED, 0xBD]),           // common DEFLATE start
    ("BZip2", [0x31, 0x41, 0x59, 0x26]),    // BZip2 block magic
  ];

  /// <summary>
  /// Tries to identify the compression algorithm used in a payload.
  /// </summary>
  /// <param name="payload">The raw payload bytes (output minus header/footer).</param>
  /// <param name="expectedDecompressedSize">Expected decompressed size (from size fields), or -1 if unknown.</param>
  public static IdentificationResult Identify(ReadOnlySpan<byte> payload, int expectedDecompressedSize = -1) {
    var matches = new List<BuildingBlockMatch>();
    var signatures = new List<SignatureMatch>();

    // 1. Scan for known compression signatures.
    for (var offset = 0; offset < Math.Min(payload.Length, 64); offset++) {
      foreach (var (name, sig) in KnownSignatures) {
        if (offset + sig.Length <= payload.Length && payload.Slice(offset, sig.Length).SequenceEqual(sig))
          signatures.Add(new() { Offset = offset, FormatName = name, Signature = sig });
      }
    }

    // 2. Try all registered building blocks.
    var blocks = BuildingBlockRegistry.All;
    var payloadArray = payload.ToArray();

    foreach (var block in blocks) {
      try {
        var decompressed = block.Decompress(payloadArray);
        if (decompressed.Length == 0) continue;

        // Calculate confidence based on output plausibility.
        var confidence = 0.5;
        if (expectedDecompressedSize > 0 && decompressed.Length == expectedDecompressedSize)
          confidence = 0.95; // Exact size match = very high confidence.
        else if (decompressed.Length > payload.Length)
          confidence = 0.7; // Expanded = likely correct decompression.
        else if (decompressed.Length < payload.Length)
          confidence = 0.3; // Shrank = probably wrong.

        // Check if decompressed data looks reasonable (not all zeros or garbage).
        var entropy = ComputeEntropy(decompressed);
        if (entropy is > 0.5 and < 7.5)
          confidence += 0.1;

        matches.Add(new() {
          Id = block.Id,
          DisplayName = block.DisplayName,
          Family = block.Family,
          DecompressedSize = decompressed.Length,
          Confidence = Math.Min(1.0, confidence)
        });
      } catch {
        // Decompression failed — this block doesn't match.
      }
    }

    // Sort by confidence descending.
    matches.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

    // 3. Classify by entropy.
    var payloadEntropy = ComputeEntropy(payload);
    var entropyClass = payloadEntropy switch {
      < 1.0 => "Uncompressed (very low entropy — padding or constant data)",
      < 4.0 => "Uncompressed or lightly encoded (low-medium entropy)",
      < 6.5 => "Possibly compressed or structured binary data",
      < 7.5 => "Likely compressed (high entropy)",
      _ => "Compressed or encrypted (near-random entropy)"
    };

    return new() { Matches = matches, Signatures = signatures, EntropyClass = entropyClass };
  }

  private static double ComputeEntropy(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return 0;
    Span<int> freq = stackalloc int[256];
    foreach (var b in data) freq[b]++;
    var entropy = 0.0;
    var len = (double)data.Length;
    for (var i = 0; i < 256; i++) {
      if (freq[i] == 0) continue;
      var p = freq[i] / len;
      entropy -= p * Math.Log2(p);
    }
    return entropy;
  }
}
