namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Encodes data using the ZIP Reduce (methods 2-5) algorithm.
/// </summary>
/// <remarks>
/// Two-pass: DLE/LZ77 encoding, then follower set probabilistic coding.
/// </remarks>
public static class ReduceEncoder {
  private const byte Dle = 0x90;

  /// <summary>
  /// Compresses data using the ZIP Reduce algorithm.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="factor">Compression factor (1-4). Higher = fewer distance bits, more length bits.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data, int factor = 4) {
    if (factor < 1 || factor > 4)
      throw new ArgumentOutOfRangeException(nameof(factor));

    // Pass 1: DLE/LZ77 encoding
    byte[] intermediate = DleEncode(data, factor);

    // Pass 2: Build follower sets and encode probabilistically
    var followers = BuildFollowerSets(intermediate);

    // Serialize
    var output = new List<byte>(intermediate.Length + 256 * 33);
    int bitPos = 0;
    byte currentByte = 0;

    // Write follower sets (high byte first: 255 down to 0)
    for (int i = 255; i >= 0; --i) {
      WriteBits(output, ref bitPos, ref currentByte, followers[i].Length, 6);
      for (int j = 0; j < followers[i].Length; ++j)
        WriteBits(output, ref bitPos, ref currentByte, followers[i][j], 8);
    }

    // Write encoded data
    byte lastByte = 0;
    for (int i = 0; i < intermediate.Length; ++i) {
      byte b = intermediate[i];
      int followerIdx = Array.IndexOf(followers[lastByte], b);

      if (followers[lastByte].Length == 0) {
        WriteBits(output, ref bitPos, ref currentByte, b, 8);
      }
      else if (followerIdx < 0) {
        // Not in follower set: write 1 bit + 8-bit literal
        WriteBits(output, ref bitPos, ref currentByte, 1, 1);
        WriteBits(output, ref bitPos, ref currentByte, b, 8);
      }
      else {
        // In follower set: write 0 bit + index
        WriteBits(output, ref bitPos, ref currentByte, 0, 1);
        int bitsNeeded = BitsForCount(followers[lastByte].Length);
        WriteBits(output, ref bitPos, ref currentByte, followerIdx, bitsNeeded);
      }

      lastByte = b;
    }

    // Flush remaining bits
    if (bitPos > 0)
      output.Add(currentByte);

    return [.. output];
  }

  private static byte[] DleEncode(ReadOnlySpan<byte> data, int factor) {
    var output = new List<byte>(data.Length);
    int distanceBits = 8 - factor;
    int lenBits = factor;
    int maxLenField = (1 << lenBits) - 1;
    int windowSize = 1 << (8 + distanceBits);

    int i = 0;
    while (i < data.Length) {
      int bestLen = 0;
      int bestDist = 0;

      int searchStart = Math.Max(0, i - windowSize);
      int maxMatchLen = maxLenField + 255 + 3;
      for (int j = searchStart; j < i; ++j) {
        int len = 0;
        int maxLen = Math.Min(data.Length - i, maxMatchLen);
        while (len < maxLen && data[j + (len % (i - j))] == data[i + len])
          ++len;
        if (len > bestLen && len >= 3) {
          bestLen = len;
          bestDist = i - j - 1;
        }
      }

      if (bestLen >= 3) {
        int adjLen = bestLen - 3;
        int lenField = Math.Min(adjLen, maxLenField);
        int extraLen = adjLen - lenField;

        if (lenField == maxLenField && extraLen > 255) {
          extraLen = 255;
          bestLen = maxLenField + extraLen + 3;
        }

        int lowBits = bestDist & ((1 << distanceBits) - 1);
        int highBits = bestDist >> distanceBits;

        byte v = (byte)((lenField << distanceBits) | lowBits);
        // V=0 is reserved for literal DLE escape; skip this match if it would produce V=0
        if (v == 0) {
          output.Add(data[i++]);
          if (data[i - 1] == Dle)
            output.Add(0);
          continue;
        }

        output.Add(Dle);
        output.Add(v);
        output.Add((byte)highBits);

        if (lenField == maxLenField)
          output.Add((byte)extraLen);

        i += bestLen;
      }
      else {
        byte b = data[i++];
        output.Add(b);
        if (b == Dle)
          output.Add(0); // Escape literal DLE
      }
    }

    return [.. output];
  }

  private static byte[][] BuildFollowerSets(byte[] data) {
    var pairCount = new int[256][];
    for (int i = 0; i < 256; ++i)
      pairCount[i] = new int[256];

    for (int i = 1; i < data.Length; ++i)
      ++pairCount[data[i - 1]][data[i]];

    var followers = new byte[256][];
    for (int i = 0; i < 256; ++i) {
      var sorted = new List<(int count, byte value)>();
      for (int j = 0; j < 256; ++j) {
        if (pairCount[i][j] > 0)
          sorted.Add((pairCount[i][j], (byte)j));
      }
      sorted.Sort((a, b) => b.count.CompareTo(a.count));

      int setSize = Math.Min(sorted.Count, 32);
      followers[i] = new byte[setSize];
      for (int j = 0; j < setSize; ++j)
        followers[i][j] = sorted[j].value;
    }

    return followers;
  }

  private static void WriteBits(List<byte> output, ref int bitPos, ref byte currentByte, int value, int count) {
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

  private static int BitsForCount(int count) {
    if (count <= 1) return 0;
    int bits = 0;
    int val = count - 1;
    while (val > 0) { ++bits; val >>= 1; }
    return bits;
  }
}
