namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Runs all registered heuristics against binary data and returns results sorted by confidence.
/// </summary>
public sealed class AlgorithmFingerprinter {

  private static readonly IHeuristic[] DefaultHeuristics = [
    new EntropyClassifier(),
    new DeflateHeuristic(),
    new RleHeuristic(),
    new MtfHeuristic(),
    new BwtHeuristic(),
    new LzHeuristic(),
    new ArithmeticHeuristic(),
    new LzwHeuristic(),
    new HuffmanHeuristic(),
  ];

  private readonly IHeuristic[] _heuristics;

  /// <summary>Creates a fingerprinter with the default set of heuristics.</summary>
  public AlgorithmFingerprinter() : this(DefaultHeuristics) { }

  /// <summary>Creates a fingerprinter with a custom set of heuristics.</summary>
  public AlgorithmFingerprinter(IHeuristic[] heuristics) {
    _heuristics = heuristics;
  }

  /// <summary>
  /// Analyzes data against all heuristics and returns results sorted by confidence descending.
  /// </summary>
  public List<FingerprintResult> Analyze(ReadOnlySpan<byte> data) {
    var results = new List<FingerprintResult>();
    foreach (var h in _heuristics) {
      var result = h.Analyze(data);
      if (result != null)
        results.Add(result);
    }
    results.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
    return results;
  }
}
