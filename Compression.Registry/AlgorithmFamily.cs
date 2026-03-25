namespace Compression.Registry;

/// <summary>
/// Classification of a compression algorithm's family for grouping and display.
/// </summary>
public enum AlgorithmFamily {
  /// <summary>Unclassified or other.</summary>
  Other = 0,

  /// <summary>LZ dictionary-based compression (LZ77, LZ78, LZW, LZSS, etc.).</summary>
  Dictionary,

  /// <summary>Entropy coding (Huffman, Arithmetic, FSE, Golomb, Range coding, etc.).</summary>
  Entropy,

  /// <summary>Data transforms (BWT, MTF, RLE, Delta, PackBits).</summary>
  Transform,

  /// <summary>Context mixing and statistical modeling (PAQ8, cmix, MCM, PPM, CTW, BCM, BSC).</summary>
  ContextMixing,

  /// <summary>Classic/legacy algorithms (Bzip2, SZDD, PowerPacker, RNC, etc.).</summary>
  Classic,

  /// <summary>Binary-to-text and container encodings (UuEncoding, YEnc, BinHex, MacBinary).</summary>
  Encoding,

  /// <summary>Archive and container formats (Zip, Tar, 7z, RAR, etc.).</summary>
  Archive,
}
