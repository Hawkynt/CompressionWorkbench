#pragma warning disable CS1591
namespace Compression.Analysis;

/// <summary>
/// Computes Shannon entropy per fixed-size window across a buffer to produce a
/// heatmap useful for locating compressed / encrypted regions (entropy ≈ 8.0),
/// natural-language / code regions (≈ 4.0–5.0), and zero-padding (≈ 0.0).
/// <para>
/// Output is a normalised array of doubles in [0, 8] — one sample per window —
/// which downstream CLI / UI tooling can render as a horizontal strip or a
/// hex-dump-style colour ramp.
/// </para>
/// </summary>
public static class EntropyHeatmap {

  public sealed record HeatmapSample(long OffsetBytes, double EntropyBits);

  public sealed record HeatmapOptions(int WindowBytes = 4096, int StepBytes = 4096);

  /// <summary>
  /// Returns one entropy sample per non-overlapping window of <paramref name="options"/>.WindowBytes.
  /// Entropy is measured in bits per byte; max = 8.
  /// </summary>
  public static IReadOnlyList<HeatmapSample> Compute(ReadOnlySpan<byte> data, HeatmapOptions? options = null) {
    options ??= new HeatmapOptions();
    if (options.WindowBytes <= 0 || options.StepBytes <= 0)
      throw new ArgumentException("window/step must be positive");

    var samples = new List<HeatmapSample>();
    for (var offset = 0L; offset + options.WindowBytes <= data.Length; offset += options.StepBytes) {
      var window = data.Slice((int)offset, options.WindowBytes);
      samples.Add(new HeatmapSample(offset, ShannonEntropy(window)));
    }
    return samples;
  }

  /// <summary>
  /// Shannon entropy of a single byte buffer in bits per byte.
  /// </summary>
  public static double ShannonEntropy(ReadOnlySpan<byte> buffer) {
    if (buffer.Length == 0) return 0.0;
    Span<int> histogram = stackalloc int[256];
    foreach (var b in buffer) histogram[b]++;
    double entropy = 0.0;
    var total = (double)buffer.Length;
    foreach (var count in histogram) {
      if (count == 0) continue;
      var p = count / total;
      entropy -= p * Math.Log2(p);
    }
    return entropy;
  }

  /// <summary>
  /// ASCII rendering of the heatmap as a horizontal strip. Uses a 5-step ramp
  /// (` .+#█`) based on bits-per-byte thresholds. Handy for CLI output.
  /// </summary>
  public static string RenderAscii(IReadOnlyList<HeatmapSample> samples, int maxWidth = 80) {
    if (samples.Count == 0) return "";
    const string Ramp = " .:+*#%@";
    var span = maxWidth > 0 ? Math.Min(samples.Count, maxWidth) : samples.Count;
    var chars = new char[span];
    for (var i = 0; i < span; ++i) {
      var srcIdx = (int)((long)i * samples.Count / span);
      var bits = samples[srcIdx].EntropyBits;
      var rampIdx = (int)Math.Min(Ramp.Length - 1, Math.Max(0, bits / 8.0 * (Ramp.Length - 1)));
      chars[i] = Ramp[rampIdx];
    }
    return new string(chars);
  }

  /// <summary>
  /// Finds regions where the entropy crosses <paramref name="threshold"/>, useful
  /// for auto-locating compressed or encrypted blobs inside a binary.
  /// </summary>
  public static IReadOnlyList<(long Start, long End)> FindHighEntropyRegions(
      IReadOnlyList<HeatmapSample> samples, double threshold = 7.5) {
    var regions = new List<(long, long)>();
    long? regionStart = null;
    for (var i = 0; i < samples.Count; ++i) {
      var above = samples[i].EntropyBits >= threshold;
      if (above && regionStart == null) regionStart = samples[i].OffsetBytes;
      else if (!above && regionStart.HasValue) {
        regions.Add((regionStart.Value, samples[i].OffsetBytes));
        regionStart = null;
      }
    }
    if (regionStart.HasValue)
      regions.Add((regionStart.Value, samples[^1].OffsetBytes));
    return regions;
  }
}
