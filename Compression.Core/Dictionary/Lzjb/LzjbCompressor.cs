namespace Compression.Core.Dictionary.Lzjb;

/// <summary>
/// Implements LZJB compression from its public algorithm description:
/// Jeff Bonwick, LZJB (ZFS metadata/small-block compressor), documented at
/// https://en.wikipedia.org/wiki/LZJB and https://docs.oracle.com/cd/E19253-01/819-5461/gbchx/index.html —
/// a 1KB-window LZ77 variant with an 8-flag copymap byte and a 3-byte minimum match.
/// </summary>
public static class LzjbCompressor {
  private const int MatchBits = 6;
  private const int MatchMin = 3;
  private const int MatchMax = (1 << LzjbCompressor.MatchBits) + (LzjbCompressor.MatchMin - 1);
  private const int OffsetMask = (1 << (16 - LzjbCompressor.MatchBits)) - 1;
  private const int LempelSize = 1024;
  private const int ItemsPerGroup = 8;

  /// <summary>Compresses <paramref name="data"/> using the LZJB copymap format.</summary>
  /// <param name="data">The uncompressed input bytes.</param>
  /// <returns>The LZJB-encoded byte stream.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var n = data.Length;
    if (n == 0)
      return [];

    var output = new List<byte>(Math.Max(16, n));
    var hashTable = new int[LzjbCompressor.LempelSize];
    hashTable.AsSpan().Fill(-1);

    var pos = 0;
    var copymapPos = output.Count;
    output.Add(0);
    var copymap = 0;
    var bit = 0;

    while (pos < n) {
      if (bit == LzjbCompressor.ItemsPerGroup) {
        output[copymapPos] = (byte)copymap;
        copymap = 0;
        bit = 0;
        copymapPos = output.Count;
        output.Add(0);
      }

      var matchLength = 0;
      var offset = 0;

      if (pos + LzjbCompressor.MatchMin <= n) {
        var hash = Hash(data, pos);
        var candidate = hashTable[hash];
        hashTable[hash] = pos;

        if (candidate >= 0) {
          offset = pos - candidate;
          if (offset > 0 && offset <= LzjbCompressor.OffsetMask)
            matchLength = MatchLength(data, candidate, pos, Math.Min(LzjbCompressor.MatchMax, n - pos));
        }
      }

      if (matchLength >= LzjbCompressor.MatchMin) {
        copymap |= 1 << bit;
        var code = (offset << LzjbCompressor.MatchBits) | (matchLength - LzjbCompressor.MatchMin);
        output.Add((byte)code);
        output.Add((byte)(code >> 8));
        pos += matchLength;
      } else {
        output.Add(data[pos]);
        ++pos;
      }

      ++bit;
    }

    output[copymapPos] = (byte)copymap;
    return [.. output];
  }

  private static int Hash(ReadOnlySpan<byte> data, int position) {
    var value = (data[position] << 16) | (data[position + 1] << 8) | data[position + 2];
    return (value ^ (value >> 9)) & (LzjbCompressor.LempelSize - 1);
  }

  private static int MatchLength(ReadOnlySpan<byte> data, int a, int b, int maxLength) {
    var len = 0;
    while (len < maxLength && data[a + len] == data[b + len])
      ++len;
    return len;
  }
}
