namespace FileFormat.StuffIt;

/// <summary>How the StuffIt writers compress each fork.</summary>
public enum StuffItCompression {
  /// <summary>The smallest of Arsenic, LZ+Huffman, RLE and storing.</summary>
  Smallest,
  /// <summary>Method 13 (LZ+Huffman), or storing when that is smaller.</summary>
  LzHuffman,
  /// <summary>Method 15 (Arsenic, StuffIt 5's "maximum"), or storing when that is smaller.</summary>
  Arsenic,
  /// <summary>Method 1 (RLE), or storing when that is smaller.</summary>
  Rle,
  /// <summary>Method 0: no compression.</summary>
  Store,
}
