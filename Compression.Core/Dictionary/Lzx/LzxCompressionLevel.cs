namespace Compression.Core.Dictionary.Lzx;

/// <summary>
/// Compression level for LZX.
/// </summary>
public enum LzxCompressionLevel {
  /// <summary>Fast compression: chain depth 16.</summary>
  Fast,

  /// <summary>Normal compression: chain depth 64.</summary>
  Normal,

  /// <summary>Best compression: chain depth 256.</summary>
  Best
}
