namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Decodes ZIP Reduce (methods 2-5) compressed data.
/// </summary>
/// <remarks>
/// Reduce is a two-pass algorithm:
/// 1. LZ77 with DLE (0x90) encoding for length-distance pairs
/// 2. Follower set probabilistic coding for byte prediction
/// Methods 2-5 differ only in the compression factor (1-4), which controls
/// the distance bit split: V byte has (8-factor) low bits for distance, factor high bits for length.
/// </remarks>
public static class ReduceDecoder {
  private const byte Dle = 0x90;

  /// <summary>
  /// Decompresses ZIP Reduce data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <param name="factor">The compression factor (1-4), derived from method - 1.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(byte[] compressed, int originalSize, int factor) {
    if (factor < 1 || factor > 4)
      throw new ArgumentOutOfRangeException(nameof(factor), "Compression factor must be 1-4.");

    int pos = 0;

    // Read follower sets
    var followers = new byte[256][];
    for (int i = 255; i >= 0; --i) {
      int count = ReadBits(compressed, ref pos, 6);
      followers[i] = new byte[count];
      for (int j = 0; j < count; ++j)
        followers[i][j] = (byte)ReadBits(compressed, ref pos, 8);
    }

    // Decode the probabilistic stream to get intermediate DLE-encoded bytes
    var intermediate = new List<byte>(originalSize * 2);
    byte lastByte = 0;

    // We don't know the exact intermediate length, so decode until we've
    // produced enough output after DLE decoding
    int totalBits = compressed.Length * 8;
    while (pos < totalBits) {
      byte b;
      if (followers[lastByte].Length == 0) {
        if (pos + 8 > totalBits)
          break;
        b = (byte)ReadBits(compressed, ref pos, 8);
      }
      else {
        int bit = ReadBits(compressed, ref pos, 1);
        if (bit == 1) {
          if (pos + 8 > totalBits)
            break;
          b = (byte)ReadBits(compressed, ref pos, 8);
        }
        else {
          int bitsNeeded = BitsForCount(followers[lastByte].Length);
          if (bitsNeeded > 0 && pos + bitsNeeded > totalBits)
            break;
          int idx = bitsNeeded > 0 ? ReadBits(compressed, ref pos, bitsNeeded) : 0;
          if (idx >= followers[lastByte].Length)
            break;
          b = followers[lastByte][idx];
        }
      }

      intermediate.Add(b);
      lastByte = b;
    }

    // Now undo the DLE/LZ77 encoding
    var output = new byte[originalSize];
    int outPos = 0;
    int inPos = 0;

    int distanceBits = 8 - factor;
    int maxLenField = (1 << factor) - 1;

    while (outPos < originalSize && inPos < intermediate.Count) {
      byte cur = intermediate[inPos++];

      if (cur != Dle) {
        output[outPos++] = cur;
        continue;
      }

      if (inPos >= intermediate.Count)
        break;

      byte v = intermediate[inPos++];
      if (v == 0) {
        // Literal DLE marker
        output[outPos++] = Dle;
        continue;
      }

      // Length-distance pair
      if (inPos >= intermediate.Count)
        break;
      int distHigh = intermediate[inPos++];

      int distLow = v & ((1 << distanceBits) - 1);
      int distance = (distHigh << distanceBits) | distLow;
      int lenField = v >> distanceBits;
      int length = lenField;

      if (lenField == maxLenField) {
        // Extended length
        if (inPos >= intermediate.Count)
          break;
        length += intermediate[inPos++];
      }

      length += 3; // minimum match length

      // Copy from output history
      int srcPos = outPos - distance - 1;
      for (int j = 0; j < length && outPos < originalSize; ++j) {
        int src = srcPos + j;
        if (src >= 0 && src < outPos)
          output[outPos] = output[src];
        else
          output[outPos] = 0;
        ++outPos;
      }
    }

    return output;
  }

  private static int ReadBits(byte[] data, ref int bitPos, int count) {
    int result = 0;
    for (int i = 0; i < count; ++i) {
      int byteIdx = bitPos / 8;
      int bitIdx = bitPos % 8;
      if (byteIdx >= data.Length)
        return result;
      result |= ((data[byteIdx] >> bitIdx) & 1) << i;
      ++bitPos;
    }
    return result;
  }

  private static int BitsForCount(int count) {
    if (count <= 1) return 0;
    int bits = 0;
    int val = count - 1;
    while (val > 0) { ++bits; val >>= 1; }
    return bits;
  }
}
