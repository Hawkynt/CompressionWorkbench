#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Compression.Analysis.ReverseEngineering;

/// <summary>
/// Analyzes multiple tool outputs to find invariant header/footer bytes, size fields,
/// and structural patterns by correlating controlled inputs with their outputs.
/// </summary>
public static class OutputCorrelator {

  /// <summary>A probe run: input probe paired with the tool's output.</summary>
  public sealed class ProbeRun {
    public required ProbeGenerator.Probe Input { get; init; }
    public required byte[] Output { get; init; }
  }

  /// <summary>Detected fixed region in the output.</summary>
  public sealed class FixedRegion {
    public required int Offset { get; init; }
    public required int Length { get; init; }
    public required byte[] Bytes { get; init; }
    public required string Location { get; init; } // "header" or "footer"
  }

  /// <summary>Detected size field encoding in the output.</summary>
  public sealed class SizeField {
    public required int Offset { get; init; }
    public required int Width { get; init; } // 2 or 4 bytes
    public required string Endianness { get; init; } // "LE" or "BE"
    public required string Meaning { get; init; } // "input_size", "output_size", "input_size+N"
    public required double Confidence { get; init; }
  }

  /// <summary>Detected compression characteristics.</summary>
  public sealed class PayloadInfo {
    public required int HeaderSize { get; init; }
    public required int FooterSize { get; init; }
    public required double PayloadEntropy { get; init; }
    public required bool IsCompressed { get; init; }
    public required bool StoresFilename { get; init; }
    public required bool IsDeterministic { get; init; }
    public required double CompressionRatio { get; init; } // average across probes
  }

  /// <summary>
  /// Finds the common prefix (fixed header) across all outputs.
  /// </summary>
  public static FixedRegion? FindCommonHeader(IReadOnlyList<ProbeRun> runs) {
    if (runs.Count < 2) return null;
    var outputs = runs.Select(r => r.Output).Where(o => o.Length > 0).ToList();
    if (outputs.Count < 2) return null;

    var minLen = outputs.Min(o => o.Length);
    var commonLen = 0;
    for (var i = 0; i < minLen; i++) {
      var b = outputs[0][i];
      if (outputs.All(o => o[i] == b))
        commonLen = i + 1;
      else
        break;
    }

    if (commonLen == 0) return null;
    var bytes = outputs[0][..commonLen];
    return new() { Offset = 0, Length = commonLen, Bytes = bytes, Location = "header" };
  }

  /// <summary>
  /// Finds the common suffix (fixed footer) across all outputs.
  /// </summary>
  public static FixedRegion? FindCommonFooter(IReadOnlyList<ProbeRun> runs) {
    if (runs.Count < 2) return null;
    var outputs = runs.Select(r => r.Output).Where(o => o.Length > 0).ToList();
    if (outputs.Count < 2) return null;

    var minLen = outputs.Min(o => o.Length);
    var commonLen = 0;
    for (var i = 1; i <= minLen; i++) {
      var b = outputs[0][^i];
      if (outputs.All(o => o[^i] == b))
        commonLen = i;
      else
        break;
    }

    if (commonLen == 0) return null;
    var bytes = outputs[0][^commonLen..];
    return new() { Offset = -commonLen, Length = commonLen, Bytes = bytes, Location = "footer" };
  }

  /// <summary>
  /// Scans the header region for fields that correlate with input or output sizes.
  /// </summary>
  public static List<SizeField> FindSizeFields(IReadOnlyList<ProbeRun> runs, int scanRange = 128) {
    if (runs.Count < 3) return [];
    var results = new List<SizeField>();

    // Only use runs with non-empty output and varying input sizes.
    var validRuns = runs.Where(r => r.Output.Length >= 4 && r.Input.Data.Length > 0).ToList();
    if (validRuns.Count < 3) return results;

    var maxScan = Math.Min(scanRange, validRuns.Min(r => r.Output.Length) - 4);

    for (var offset = 0; offset < maxScan; offset++) {
      // Try 4-byte LE.
      if (TrySizeCorrelation(validRuns, offset, 4, true, out var meaning4Le))
        results.Add(new() { Offset = offset, Width = 4, Endianness = "LE", Meaning = meaning4Le, Confidence = 1.0 });

      // Try 4-byte BE.
      if (TrySizeCorrelation(validRuns, offset, 4, false, out var meaning4Be))
        results.Add(new() { Offset = offset, Width = 4, Endianness = "BE", Meaning = meaning4Be, Confidence = 1.0 });

      // Try 2-byte LE.
      if (offset + 2 <= maxScan && TrySizeCorrelation(validRuns, offset, 2, true, out var meaning2Le))
        results.Add(new() { Offset = offset, Width = 2, Endianness = "LE", Meaning = meaning2Le, Confidence = 0.8 });

      // Try 2-byte BE.
      if (offset + 2 <= maxScan && TrySizeCorrelation(validRuns, offset, 2, false, out var meaning2Be))
        results.Add(new() { Offset = offset, Width = 2, Endianness = "BE", Meaning = meaning2Be, Confidence = 0.8 });
    }

    // Deduplicate: if a 4-byte field subsumes a 2-byte field at the same offset, keep only the 4-byte.
    var deduped = new List<SizeField>();
    foreach (var sf in results.OrderByDescending(s => s.Width)) {
      if (!deduped.Any(d => d.Offset == sf.Offset && d.Width > sf.Width))
        deduped.Add(sf);
    }

    return deduped;
  }

  /// <summary>
  /// Analyzes the payload region (between header and footer) across runs.
  /// </summary>
  public static PayloadInfo AnalyzePayload(IReadOnlyList<ProbeRun> runs, int headerSize, int footerSize) {
    // Entropy of payload from a representative run (the largest non-random input).
    var textRuns = runs.Where(r => r.Input.Name.Contains("text") && r.Output.Length > headerSize + footerSize).ToList();
    var representative = textRuns.FirstOrDefault() ?? runs.FirstOrDefault(r => r.Output.Length > headerSize + footerSize + 16);

    var entropy = 0.0;
    if (representative != null) {
      var payloadEnd = representative.Output.Length - footerSize;
      if (payloadEnd > headerSize) {
        var payload = representative.Output.AsSpan(headerSize, payloadEnd - headerSize);
        entropy = ComputeEntropy(payload);
      }
    }

    // Check if filenames are stored (compare name-test1 vs name-test2 outputs).
    var nameRuns = runs.Where(r => r.Input.Name.StartsWith("name-", StringComparison.Ordinal)).ToList();
    var storesFilename = nameRuns.Count >= 2 && !nameRuns[0].Output.AsSpan().SequenceEqual(nameRuns[1].Output);

    // Check determinism (determinism-a vs determinism-b should produce identical output).
    var detRuns = runs.Where(r => r.Input.Name.StartsWith("determinism-", StringComparison.Ordinal)).ToList();
    var isDeterministic = detRuns.Count >= 2 && detRuns[0].Output.AsSpan().SequenceEqual(detRuns[1].Output);

    // Average compression ratio across non-empty runs.
    var ratios = runs
      .Where(r => r.Input.Data.Length > 0 && r.Output.Length > 0)
      .Select(r => (double)r.Output.Length / r.Input.Data.Length)
      .ToList();
    var avgRatio = ratios.Count > 0 ? ratios.Average() : 1.0;

    return new() {
      HeaderSize = headerSize,
      FooterSize = footerSize,
      PayloadEntropy = entropy,
      IsCompressed = entropy > 6.5,
      StoresFilename = storesFilename,
      IsDeterministic = isDeterministic,
      CompressionRatio = avgRatio
    };
  }

  private static bool TrySizeCorrelation(List<ProbeRun> runs, int offset, int width, bool littleEndian, out string meaning) {
    meaning = "";

    foreach (var candidate in new[] { "input_size", "output_payload_size" }) {
      var allMatch = true;
      foreach (var run in runs) {
        if (offset + width > run.Output.Length) { allMatch = false; break; }

        var fieldValue = width == 4
          ? littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(run.Output.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32BigEndian(run.Output.AsSpan(offset))
          : littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(run.Output.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16BigEndian(run.Output.AsSpan(offset));

        var expected = candidate == "input_size"
          ? (uint)run.Input.Data.Length
          : (uint)(run.Output.Length - offset - width); // payload after this field

        if (fieldValue != expected) { allMatch = false; break; }
      }

      if (allMatch) {
        meaning = candidate;
        return true;
      }
    }

    return false;
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
