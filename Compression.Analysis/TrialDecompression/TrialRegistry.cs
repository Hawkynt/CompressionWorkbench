namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Central registry of all trial decompression strategies.
/// </summary>
public static class TrialRegistry {

  private static readonly List<ITrialStrategy> _strategies;

  static TrialRegistry() {
    _strategies = [
      // Format-level strategies first (they validate headers → fewer false positives)
      .. TrialFormat.CreateAll(),

      // Primitive-level strategies (more prone to false positives)
      new TrialDeflate(),
      new TrialRle(),
      new TrialMtf(),
      new TrialLzw(),
      new TrialLzss(),
      new TrialBwt(),
      new TrialHuffman(),
    ];
  }

  /// <summary>All registered trial strategies.</summary>
  public static IReadOnlyList<ITrialStrategy> All => _strategies;

  /// <summary>Returns the strategy for a given algorithm name, or null.</summary>
  public static ITrialStrategy? GetByName(string algorithm)
    => _strategies.FirstOrDefault(s => s.Algorithm.Equals(algorithm, StringComparison.OrdinalIgnoreCase));
}
