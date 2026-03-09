namespace Compression.Core.Deflate;

/// <summary>
/// Specifies the compression level for the Deflate compressor.
/// </summary>
public enum DeflateCompressionLevel {
  /// <summary>No compression — emit uncompressed blocks.</summary>
  None = 0,

  /// <summary>Fast compression — static Huffman with shallow match finding.</summary>
  Fast = 1,

  /// <summary>Default compression — dynamic Huffman with moderate match finding.</summary>
  Default = 6,

  /// <summary>Best compression — dynamic Huffman with deep match finding and lazy matching.</summary>
  Best = 9,

  /// <summary>Maximum compression — Zopfli-style iterative optimal parsing and block splitting.</summary>
  Maximum = 11
}
