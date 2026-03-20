namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Encodes data using the ZIP Implode (method 6) algorithm.
/// </summary>
/// <remarks>
/// Uses LZ77 + Shannon-Fano trees with 8KB dictionary and literal tree.
/// </remarks>
public static class ImplodeEncoder {
  /// <summary>
  /// Compresses data using the ZIP Implode algorithm.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="useLiteralTree">Whether to use a literal tree (3 trees) vs 2 trees.</param>
  /// <param name="use8kDictionary">Whether to use an 8KB dictionary vs 4KB.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data, bool useLiteralTree = true, bool use8kDictionary = true) {
    int distanceBits = use8kDictionary ? 7 : 6;
    int minMatchLen = useLiteralTree ? 3 : 2;
    int windowSize = use8kDictionary ? 8192 : 4096;

    // Pass 1: LZ77 parse
    var tokens = Lz77Parse(data, windowSize, minMatchLen);

    // Collect statistics for tree building
    var literalFreq = new int[256];
    var lengthFreq = new int[64];
    var distanceFreq = new int[64];

    foreach (var (isLit, lit, len, dist) in tokens) {
      if (isLit) {
        literalFreq[lit]++;
      }
      else {
        int lenCode = Math.Min(len - minMatchLen, 63);
        int distHigh = dist >> distanceBits;
        lengthFreq[lenCode]++;
        if (distHigh < 64) distanceFreq[distHigh]++;
      }
    }

    // Build code lengths (all symbols get non-zero lengths for Shannon-Fano format)
    int[]? literalLengths = useLiteralTree ? BuildCodeLengths(literalFreq, 256) : null;
    int[] lengthLengths = BuildCodeLengths(lengthFreq, 64);
    int[] distanceLengths = BuildCodeLengths(distanceFreq, 64);

    // Build encoding tables
    (int code, int bits)[]? literalCodes = useLiteralTree ? BuildCodes(literalLengths!, 256) : null;
    (int code, int bits)[] lengthCodes = BuildCodes(lengthLengths, 64);
    (int code, int bits)[] distanceCodes = BuildCodes(distanceLengths, 64);

    // Serialize
    var output = new List<byte>();
    int bitPos = 0;
    byte currentByte = 0;

    // Write trees
    if (useLiteralTree)
      WriteSfTree(output, ref bitPos, ref currentByte, literalLengths!, 256);
    WriteSfTree(output, ref bitPos, ref currentByte, lengthLengths, 64);
    WriteSfTree(output, ref bitPos, ref currentByte, distanceLengths, 64);

    // Write tokens
    foreach (var (isLit, lit, len, dist) in tokens) {
      if (isLit) {
        WriteBit(output, ref bitPos, ref currentByte, 1); // literal flag
        if (useLiteralTree && literalCodes != null) {
          var (code, bits) = literalCodes[lit];
          WriteBitsReversed(output, ref bitPos, ref currentByte, code, bits);
        }
        else {
          WriteBitsReversed(output, ref bitPos, ref currentByte, lit, 8);
        }
      }
      else {
        WriteBit(output, ref bitPos, ref currentByte, 0); // match flag
        int distLow = dist & ((1 << distanceBits) - 1);
        int distHigh = dist >> distanceBits;
        int lenCode = Math.Min(len - minMatchLen, 63);

        WriteBitsReversed(output, ref bitPos, ref currentByte, distLow, distanceBits);

        var (dc, db) = distanceCodes[distHigh < 64 ? distHigh : 0];
        WriteBitsReversed(output, ref bitPos, ref currentByte, dc, db);

        var (lc, lb) = lengthCodes[lenCode];
        WriteBitsReversed(output, ref bitPos, ref currentByte, lc, lb);

        if (lenCode == 63) {
          int extra = len - minMatchLen - 63;
          WriteBitsReversed(output, ref bitPos, ref currentByte, Math.Min(extra, 255), 8);
        }
      }
    }

    if (bitPos > 0)
      output.Add(currentByte);

    return [.. output];
  }

  private static List<(bool isLiteral, byte literal, int length, int distance)> Lz77Parse(
      ReadOnlySpan<byte> data, int windowSize, int minMatchLen) {
    var tokens = new List<(bool, byte, int, int)>();
    int i = 0;

    while (i < data.Length) {
      int bestLen = 0;
      int bestDist = 0;

      int searchStart = Math.Max(0, i - windowSize);
      for (int j = searchStart; j < i; ++j) {
        int len = 0;
        int maxLen = Math.Min(data.Length - i, 63 + 255 + minMatchLen);
        while (len < maxLen && data[j + (len % (i - j))] == data[i + len])
          ++len;
        if (len > bestLen && len >= minMatchLen) {
          bestLen = len;
          bestDist = i - j - 1;
        }
      }

      if (bestLen >= minMatchLen) {
        tokens.Add((false, 0, bestLen, bestDist));
        i += bestLen;
      }
      else {
        tokens.Add((true, data[i], 0, 0));
        ++i;
      }
    }

    return tokens;
  }

  private static int[] BuildCodeLengths(int[] freq, int numSymbols) {
    // In PKZIP Implode Shannon-Fano format, ALL symbols must have non-zero
    // code lengths (the format stores len-1 in 4 bits, so min length is 1).
    // Build a Huffman tree with all symbols having at least frequency 1.

    var pq = new PriorityQueue<int, long>();
    var nodes = new List<(long freq, int sym, int left, int right)>();

    for (int i = 0; i < numSymbols; ++i) {
      long f = Math.Max(freq[i], 1);
      nodes.Add((f, i, -1, -1));
      pq.Enqueue(i, f);
    }

    if (numSymbols == 1) {
      return [1];
    }

    while (pq.Count > 1) {
      pq.TryDequeue(out int a, out long fa);
      pq.TryDequeue(out int b, out long fb);
      long combined = fa + fb;
      int newIdx = nodes.Count;
      nodes.Add((combined, -1, a, b));
      pq.Enqueue(newIdx, combined);
    }

    pq.TryDequeue(out int root, out _);

    var lengths = new int[numSymbols];

    void Walk(int idx, int depth) {
      var node = nodes[idx];
      if (node.sym >= 0) {
        lengths[node.sym] = Math.Max(depth, 1);
        return;
      }
      Walk(node.left, depth + 1);
      Walk(node.right, depth + 1);
    }

    Walk(root, 0);

    // Clamp to max 16 (4-bit field stores len-1, max 15 → len 16)
    // If any exceed 16, flatten to uniform lengths
    int maxLen = 0;
    for (int i = 0; i < numSymbols; ++i)
      if (lengths[i] > maxLen) maxLen = lengths[i];

    if (maxLen > 16) {
      // Fallback: uniform assignment
      int bits = 1;
      while ((1 << bits) < numSymbols) ++bits;
      for (int i = 0; i < numSymbols; ++i)
        lengths[i] = bits;
    }

    return lengths;
  }

  private static (int code, int bits)[] BuildCodes(int[] codeLengths, int numSymbols) {
    int maxLen = 0;
    for (int i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > maxLen) maxLen = codeLengths[i];

    if (maxLen == 0) maxLen = 1;

    var blCount = new int[maxLen + 1];
    for (int i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > 0) ++blCount[codeLengths[i]];

    var nextCode = new int[maxLen + 1];
    int code = 0;
    for (int b = 1; b <= maxLen; ++b) {
      code = (code + blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    var codes = new (int code, int bits)[numSymbols];
    for (int sym = 0; sym < numSymbols; ++sym) {
      int len = codeLengths[sym];
      if (len == 0) {
        codes[sym] = (0, 0);
        continue;
      }
      codes[sym] = (nextCode[len]++, len);
    }

    return codes;
  }

  private static void WriteSfTree(List<byte> output, ref int bitPos, ref byte currentByte,
      int[] codeLengths, int numSymbols) {
    // RLE encode code lengths (stored as len-1)
    var runs = new List<(int adjLen, int count)>();
    int i = 0;
    while (i < numSymbols) {
      int len = codeLengths[i];
      int adjLen = len > 0 ? len - 1 : 0;
      int count = 1;
      while (i + count < numSymbols && codeLengths[i + count] == len && count < 16)
        ++count;
      runs.Add((adjLen, count));
      i += count;
    }

    // Write number of RLE entries - 1
    WriteBitsReversed(output, ref bitPos, ref currentByte, runs.Count - 1, 8);

    foreach (var (adjLen, count) in runs) {
      int val = adjLen | ((count - 1) << 4);
      WriteBitsReversed(output, ref bitPos, ref currentByte, val, 8);
    }
  }

  private static void WriteBit(List<byte> output, ref int bitPos, ref byte currentByte, int bit) {
    if (bit == 1)
      currentByte |= (byte)(1 << bitPos);
    ++bitPos;
    if (bitPos == 8) {
      output.Add(currentByte);
      currentByte = 0;
      bitPos = 0;
    }
  }

  private static void WriteBitsReversed(List<byte> output, ref int bitPos, ref byte currentByte,
      int value, int count) {
    for (int i = 0; i < count; ++i) {
      if (((value >> i) & 1) == 1)
        currentByte |= (byte)(1 << bitPos);
      ++bitPos;
      if (bitPos == 8) {
        output.Add(currentByte);
        currentByte = 0;
        bitPos = 0;
      }
    }
  }
}
