namespace Compression.Core.Dictionary.Lz4;

/// <summary>
/// Compression level for LZ4.
/// </summary>
public enum Lz4CompressionLevel {
  /// <summary>Fast compression using single-slot hash table.</summary>
  Fast,

  /// <summary>High compression using hash chains with moderate search depth (16).</summary>
  Hc,

  /// <summary>Maximum compression using hash chains with deep search (64).</summary>
  Max
}
