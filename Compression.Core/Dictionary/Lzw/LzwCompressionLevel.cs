namespace Compression.Core.Dictionary.Lzw;

/// <summary>
/// Specifies the compression level for the LZW encoder.
/// </summary>
public enum LzwCompressionLevel {
  /// <summary>Output raw byte codes only — no dictionary building.</summary>
  Uncompressed = 0,

  /// <summary>Standard greedy LZW — longest trie match at each step.</summary>
  FirstMatch = 1,

  /// <summary>Optimal DP-based LZW — minimizes total bit cost.</summary>
  Optimal = 9
}
