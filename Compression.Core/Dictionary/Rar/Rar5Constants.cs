namespace Compression.Core.Dictionary.Rar;

/// <summary>Constants of the RAR 5.0 compression format.</summary>
public static class Rar5Constants {
  /// <summary>Longest Huffman code in a RAR 5 table.</summary>
  public const int MaxCodeLength = 15;

  /// <summary>Smallest dictionary a RAR 5 header can declare (128 KB).</summary>
  public const int MinDictionarySize = 128 * 1024;

  /// <summary>Largest dictionary of compression algorithm version 0 (4 GB).</summary>
  public const long MaxDictionarySize = 4L * 1024 * 1024 * 1024;
}
