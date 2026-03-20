namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// Compression level for LZMA/LZMA2.
/// </summary>
public enum LzmaCompressionLevel {
  /// <summary>Fast compression: chain depth 16.</summary>
  Fast,

  /// <summary>Normal compression: chain depth 64.</summary>
  Normal,

  /// <summary>Best compression: chain depth 256, uses BinaryTree match finder.</summary>
  Best
}
