namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Compression level for XPRESS (plain and Huffman variants).
/// </summary>
public enum XpressCompressionLevel {
  /// <summary>Fast compression: chain depth 16.</summary>
  Fast,

  /// <summary>Normal compression: chain depth 128.</summary>
  Normal,

  /// <summary>Best compression: chain depth 512.</summary>
  Best
}
