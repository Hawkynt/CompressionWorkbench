namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Decodes ZIP Implode (method 6) compressed data.
/// </summary>
/// <remarks>
/// Implode uses LZ77 + Shannon-Fano trees (actually reversed canonical Huffman).
/// 2 or 3 trees: distance tree, length tree, and optional literal tree.
/// The flag bits in the general-purpose bit field control:
/// - Bit 1: 8KB dictionary (1) or 4KB dictionary (0)
/// - Bit 2: 3 trees with literal tree (1) or 2 trees (0)
/// </remarks>
public static class ImplodeDecoder {
  /// <summary>
  /// Decompresses ZIP Implode data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <param name="hasLiteralTree">Whether a literal tree is present (bit 2 of GP flags).</param>
  /// <param name="is8kDictionary">Whether an 8KB dictionary is used (bit 1 of GP flags).</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize, bool hasLiteralTree, bool is8kDictionary)
    => Decode(compressed.ToArray(), originalSize, hasLiteralTree, is8kDictionary);

  /// <summary>
  /// Decompresses ZIP Implode data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <param name="hasLiteralTree">Whether a literal tree is present (bit 2 of GP flags).</param>
  /// <param name="is8kDictionary">Whether an 8KB dictionary is used (bit 1 of GP flags).</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(byte[] compressed, int originalSize, bool hasLiteralTree, bool is8kDictionary) {
    var bitPos = 0;
    var distanceBits = is8kDictionary ? 7 : 6;
    var minMatchLen = hasLiteralTree ? 3 : 2;

    // Read Shannon-Fano trees
    int[]? literalTree = null;
    if (hasLiteralTree)
      literalTree = ReadSfTree(compressed, ref bitPos, 256);

    var lengthTree = ReadSfTree(compressed, ref bitPos, 64);
    var distanceTree = ReadSfTree(compressed, ref bitPos, 64);

    var output = new byte[originalSize];
    var outPos = 0;

    while (outPos < originalSize) {
      var flag = ReadBit(compressed, ref bitPos);

      if (flag == 1) {
        // Literal byte
        byte b;
        if (hasLiteralTree && literalTree != null) {
          b = (byte)DecodeSymbol(compressed, ref bitPos, literalTree);
        }
        else {
          b = (byte)ReadBitsReversed(compressed, ref bitPos, 8);
        }
        output[outPos++] = b;
      }
      else {
        // Length-distance pair
        var distLow = ReadBitsReversed(compressed, ref bitPos, distanceBits);
        var distHigh = DecodeSymbol(compressed, ref bitPos, distanceTree);
        var distance = (distHigh << distanceBits) | distLow;

        var lenCode = DecodeSymbol(compressed, ref bitPos, lengthTree);
        var length = lenCode + minMatchLen;

        if (lenCode == 63) {
          // Extended length
          var extra = ReadBitsReversed(compressed, ref bitPos, 8);
          length += extra;
        }

        var srcPos = outPos - distance - 1;
        for (var i = 0; i < length && outPos < originalSize; ++i) {
          if (srcPos + i >= 0 && srcPos + i < outPos)
            output[outPos] = output[srcPos + i];
          else
            output[outPos] = 0;
          ++outPos;
        }
      }
    }

    return output;
  }

  private static int[] ReadSfTree(byte[] data, ref int bitPos, int maxSymbol) {
    // Read code lengths via RLE
    var numEntries = ReadBitsReversed(data, ref bitPos, 8) + 1;
    var codeLengths = new int[maxSymbol];

    var idx = 0;
    for (var i = 0; i < numEntries && idx < maxSymbol; ++i) {
      var val = ReadBitsReversed(data, ref bitPos, 8);
      var len = (val & 0x0F) + 1;
      var count = (val >> 4) + 1;
      for (var j = 0; j < count && idx < maxSymbol; ++j)
        codeLengths[idx++] = len;
    }

    // Build decode table: code → symbol (max 16 bits)
    return BuildDecodeTable(codeLengths, maxSymbol);
  }

  private static int[] BuildDecodeTable(int[] codeLengths, int maxSymbol) {
    var maxLen = 0;
    for (var i = 0; i < maxSymbol; ++i)
      if (codeLengths[i] > maxLen) maxLen = codeLengths[i];

    if (maxLen == 0) maxLen = 1;

    // Build canonical Huffman codes (reversed bit order for Shannon-Fano)
    var blCount = new int[maxLen + 1];
    for (var i = 0; i < maxSymbol; ++i)
      if (codeLengths[i] > 0) ++blCount[codeLengths[i]];

    var nextCode = new int[maxLen + 1];
    var code = 0;
    for (var bits = 1; bits <= maxLen; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    // Build lookup table: indexed by code (up to 2^maxLen entries)
    var tableSize = 1 << maxLen;
    var table = new int[tableSize];
    Array.Fill(table, -1);

    for (var sym = 0; sym < maxSymbol; ++sym) {
      var len = codeLengths[sym];
      if (len == 0) continue;

      var c = nextCode[len]++;
      // Fill all table entries: code in low bits, don't-care bits in high bits
      // (matches LSB-first bit reading order)
      var fill = 1 << (maxLen - len);
      for (var j = 0; j < fill; ++j)
        table[c | (j << len)] = sym | (len << 16);
    }

    return table;
  }

  private static int DecodeSymbol(byte[] data, ref int bitPos, int[] table) {
    var tableSize = table.Length;
    var maxBits = 0;
    while ((1 << maxBits) < tableSize) ++maxBits;

    var code = ReadBitsReversed(data, ref bitPos, maxBits);
    if (code >= tableSize || table[code] == -1)
      throw new InvalidDataException($"Invalid Implode Huffman code at bit position {bitPos}.");

    var entry = table[code];
    var symbol = entry & 0xFFFF;
    var usedBits = entry >> 16;

    // Return unused bits
    bitPos -= (maxBits - usedBits);

    return symbol;
  }

  private static int ReadBit(byte[] data, ref int bitPos) {
    var byteIdx = bitPos / 8;
    var bitIdx = bitPos % 8;
    if (byteIdx >= data.Length) return 0;
    ++bitPos;
    return (data[byteIdx] >> bitIdx) & 1;
  }

  private static int ReadBitsReversed(byte[] data, ref int bitPos, int count) {
    // Read bits LSB-first (natural order)
    var result = 0;
    for (var i = 0; i < count; ++i) {
      var byteIdx = bitPos / 8;
      var bitIdx = bitPos % 8;
      if (byteIdx >= data.Length) break;
      result |= ((data[byteIdx] >> bitIdx) & 1) << i;
      ++bitPos;
    }
    return result;
  }
}
