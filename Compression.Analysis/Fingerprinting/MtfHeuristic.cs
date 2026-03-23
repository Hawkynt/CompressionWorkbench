namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Detects Move-to-Front transform output: characterized by low-value byte concentration
/// where bytes 0-3 dominate the distribution with geometric frequency decay.
/// </summary>
public sealed class MtfHeuristic : IHeuristic {

  /// <inheritdoc />
  public FingerprintResult? Analyze(ReadOnlySpan<byte> data) {
    if (data.Length < 16) return null;

    // Count bytes in low range [0-3]
    var lowCount = 0;
    var freq = new int[256];
    foreach (var b in data) {
      freq[b]++;
      if (b <= 3) lowCount++;
    }

    var lowRatio = (double)lowCount / data.Length;
    if (lowRatio < 0.3) return null;

    // Check for geometric decay: freq[0] > freq[1] > freq[2] > freq[3]
    var decayScore = 0;
    for (var i = 0; i < 3; i++) {
      if (freq[i] > freq[i + 1]) decayScore++;
    }

    // Also check that high bytes are rare
    var highCount = 0;
    for (var i = 128; i < 256; i++) highCount += freq[i];
    var highRatio = (double)highCount / data.Length;

    var confidence = 0.3;
    if (lowRatio > 0.5) confidence += 0.2;
    if (lowRatio > 0.7) confidence += 0.1;
    if (decayScore >= 3) confidence += 0.15;
    if (highRatio < 0.1) confidence += 0.1;

    confidence = Math.Min(confidence, 0.85);
    if (confidence < 0.5) return null;

    return new("MTF", confidence, $"Low-byte concentration: {lowRatio:P0}, decay score: {decayScore}/3");
  }
}
