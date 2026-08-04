namespace Compression.Core.Dictionary.FastLz;

/// <summary>
/// Implements FastLZ Level 1 compression from the public block-format specification:
/// Ariya Hidayat, "FastLZ — free, open-source, portable real-time compression library",
/// https://ariya.github.io/FastLZ/ (block format section) and https://github.com/ariya/FastLZ.
/// </summary>
public static class FastLzCompressor {
  private const int HashBits = 13;
  private const int HashSize = 1 << FastLzCompressor.HashBits;
  private const int HashMask = FastLzCompressor.HashSize - 1;
  private const int MinMatch = 3;
  private const int MaxShortMatch = 8;
  private const int MaxLongMatchExtra = 255;
  private const int MaxMatch = 9 + FastLzCompressor.MaxLongMatchExtra;
  private const int MaxDistance = 8192;
  private const int MaxLiteralRun = 32;

  /// <summary>Compresses <paramref name="data"/> using the FastLZ level-1 block format.</summary>
  /// <param name="data">The uncompressed input bytes.</param>
  /// <returns>The FastLZ-encoded byte stream.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var n = data.Length;
    var output = new List<byte>(Math.Max(16, n));
    if (n == 0)
      return [];

    var hashTable = new int[FastLzCompressor.HashSize];
    hashTable.AsSpan().Fill(-1);

    var ip = 0;
    var anchor = 0;

    while (ip + FastLzCompressor.MinMatch <= n) {
      var hash = Hash(data, ip);
      var candidate = hashTable[hash];
      hashTable[hash] = ip;

      var matchLength = 0;
      if (candidate >= 0 &&
          data[candidate] == data[ip] &&
          data[candidate + 1] == data[ip + 1] &&
          data[candidate + 2] == data[ip + 2] &&
          ip - candidate <= FastLzCompressor.MaxDistance)
        matchLength = MatchLength(data, candidate, ip, Math.Min(FastLzCompressor.MaxMatch, n - ip));

      if (matchLength >= FastLzCompressor.MinMatch) {
        EmitLiterals(output, data, anchor, ip - anchor);
        EmitMatch(output, matchLength, ip - candidate);

        var matchEnd = ip + matchLength;
        ++ip;
        while (ip < matchEnd) {
          if (ip + FastLzCompressor.MinMatch <= n)
            hashTable[Hash(data, ip)] = ip;
          ++ip;
        }

        anchor = ip;
      } else
        ++ip;
    }

    EmitLiterals(output, data, anchor, n - anchor);
    return [.. output];
  }

  private static int Hash(ReadOnlySpan<byte> data, int position) {
    var value = (data[position] << 16) | (data[position + 1] << 8) | data[position + 2];
    var h = unchecked((uint)value * 2654435769u);
    return (int)(h >> (32 - FastLzCompressor.HashBits)) & FastLzCompressor.HashMask;
  }

  private static int MatchLength(ReadOnlySpan<byte> data, int a, int b, int maxLength) {
    var len = 0;
    while (len < maxLength && data[a + len] == data[b + len])
      ++len;
    return len;
  }

  private static void EmitLiterals(List<byte> output, ReadOnlySpan<byte> data, int start, int length) {
    while (length > 0) {
      var chunk = Math.Min(length, FastLzCompressor.MaxLiteralRun);
      output.Add((byte)(chunk - 1));
      for (var i = 0; i < chunk; ++i)
        output.Add(data[start + i]);
      start += chunk;
      length -= chunk;
    }
  }

  private static void EmitMatch(List<byte> output, int length, int distance) {
    var encodedDistance = distance - 1;
    if (length <= FastLzCompressor.MaxShortMatch) {
      var type = length - 2;
      output.Add((byte)((type << 5) | (encodedDistance >> 8)));
      output.Add((byte)encodedDistance);
    } else {
      output.Add((byte)((7 << 5) | (encodedDistance >> 8)));
      output.Add((byte)encodedDistance);
      output.Add((byte)(length - 9));
    }
  }
}
