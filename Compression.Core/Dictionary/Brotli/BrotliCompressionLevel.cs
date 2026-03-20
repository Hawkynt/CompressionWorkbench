namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Compression level for Brotli.
/// </summary>
public enum BrotliCompressionLevel {
  /// <summary>Uncompressed meta-blocks only (fastest, no compression).</summary>
  Uncompressed,

  /// <summary>Fast LZ77 compression.</summary>
  Fast,

  /// <summary>Default LZ77 compression.</summary>
  Default,

  /// <summary>Best LZ77 compression with deeper search.</summary>
  Best
}
