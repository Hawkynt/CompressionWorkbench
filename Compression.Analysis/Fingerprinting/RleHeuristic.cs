namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Detects RLE (Run-Length Encoding) patterns: count+value byte pairs where
/// the count byte followed by repeated value bytes is a dominant pattern.
/// </summary>
public sealed class RleHeuristic : IHeuristic {

  /// <inheritdoc />
  public FingerprintResult? Analyze(ReadOnlySpan<byte> data) {
    if (data.Length < 4) return null;

    // Check for count+value pair pattern:
    // In typical RLE, byte pairs appear as (count, value) where count > 0
    // and the same value appears count times in original
    var pairMatches = 0;
    var totalPairs = data.Length / 2;
    for (var i = 0; i + 1 < data.Length; i += 2) {
      var count = data[i];
      // Valid RLE count: 1-255 (0 might be escape or end marker)
      if (count is >= 1 and <= 127)
        pairMatches++;
    }

    var ratio = (double)pairMatches / totalPairs;
    if (ratio < 0.4) return null;

    // Also check: low diversity of "value" bytes (position 1,3,5,...)
    var valueBytes = new HashSet<byte>();
    for (var i = 1; i < data.Length; i += 2)
      valueBytes.Add(data[i]);

    var valueDiversity = (double)valueBytes.Count / 256;
    var confidence = Math.Min(0.85, 0.3 + ratio * 0.4 + (1 - valueDiversity) * 0.15);

    if (confidence < 0.4) return null;
    return new("RLE", confidence, $"Count+value pairs: {ratio:P0}, value diversity: {valueDiversity:P0}");
  }
}
