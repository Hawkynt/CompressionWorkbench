namespace FileFormat.Arc;

/// <summary>
/// Compression methods supported by the ARC archive writer.
/// </summary>
public enum ArcCompressionMethod : byte {
  /// <summary>No compression — data is stored as-is (method 2).</summary>
  Stored = ArcConstants.MethodStored,

  /// <summary>ARC run-length encoding using 0x90 as the repeat marker (method 3).</summary>
  Packed = ArcConstants.MethodPacked,

  /// <summary>Static Huffman coding (method 4).</summary>
  Squeezed = ArcConstants.MethodSqueezed,

  /// <summary>LZW 9-12 bits with RLE pre-pass (method 5).</summary>
  Crunched5 = ArcConstants.MethodCrunched5,

  /// <summary>LZW 9-12 bits, no clear code, no RLE (method 6).</summary>
  Crunched6 = ArcConstants.MethodCrunched6,

  /// <summary>LZW 9-12 bits with clear code, no RLE (method 7).</summary>
  Crunched7 = ArcConstants.MethodCrunched7,

  /// <summary>LZW with 9-13 bit codes and dynamic clear codes (method 8).</summary>
  Crunched = ArcConstants.MethodCrunched8,

  /// <summary>LZW with 9-13 bit codes, no RLE pre-pass (method 9).</summary>
  Squashed = ArcConstants.MethodSquashed,
}
