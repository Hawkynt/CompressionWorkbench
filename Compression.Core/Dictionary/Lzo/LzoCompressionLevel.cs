namespace Compression.Core.Dictionary.Lzo;

/// <summary>
/// Compression level for LZO1X.
/// </summary>
public enum LzoCompressionLevel {
  /// <summary>Fast compression (LZO1X-1 style, single-slot hash).</summary>
  Fast,

  /// <summary>Best compression (LZO1X-999 style, hash chains + optimal parsing).</summary>
  Best
}
