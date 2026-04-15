namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Central registry of all trial decompression strategies.
/// Uses lazy initialization to avoid TypeInitializationException cascading if any strategy fails to load.
/// </summary>
public static class TrialRegistry {

  private static volatile List<ITrialStrategy>? _strategies;
  private static readonly object _lock = new();

  private static List<ITrialStrategy> EnsureLoaded() {
    if (_strategies != null) return _strategies;

    lock (_lock) {
      if (_strategies != null) return _strategies;

      var list = new List<ITrialStrategy>();

      // Hand-tuned primitive strategies (always available, no registry dependency).
      list.Add(new TrialDeflate());
      list.Add(new TrialRle());
      list.Add(new TrialMtf());
      list.Add(new TrialLzw());
      list.Add(new TrialLzss());
      list.Add(new TrialBwt());
      list.Add(new TrialHuffman());

      // Registry-dependent strategies — wrap each in try/catch so one failure doesn't kill the rest.
      try { list.InsertRange(0, TrialFormat.CreateMagicDetections()); } catch { /* registry not available */ }
      try { list.InsertRange(list.Count - 7, TrialFormat.CreateAll()); } catch { /* stream formats failed */ }
      try { list.AddRange(TrialFormat.CreateArchiveTrials()); } catch { /* archive formats failed */ }
      try { list.AddRange(TrialBuildingBlock.CreateAll()); } catch { /* building blocks failed */ }

      _strategies = list;
      return _strategies;
    }
  }

  /// <summary>All registered trial strategies.</summary>
  public static IReadOnlyList<ITrialStrategy> All => EnsureLoaded();

  /// <summary>Returns the strategy for a given algorithm name, or null.</summary>
  public static ITrialStrategy? GetByName(string algorithm)
    => EnsureLoaded().FirstOrDefault(s => s.Algorithm.Equals(algorithm, StringComparison.OrdinalIgnoreCase));
}
