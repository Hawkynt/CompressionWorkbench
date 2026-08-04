namespace Compression.Core.Dictionary.Lzrw1;

/// <summary>
/// Implements LZRW1 compression from its public algorithm description:
/// Ross N. Williams, "An Extremely Fast Ziv-Lempel Data Compression Algorithm",
/// Data Compression Conference 1991, http://ross.net/compression/lzrw1.html —
/// a hash-matched LZ77 variant grouping 16 items behind a control word,
/// with a 4096-entry single-probe hash table and matches of length 3-18.
/// </summary>
public static class Lzrw1Compressor {
  private const int HashTableSize = 4096;
  private const int HashMask = Lzrw1Compressor.HashTableSize - 1;
  private const int MinMatch = 3;
  private const int MaxMatch = 18;
  private const int MaxOffset = 4095;
  private const int ItemsPerGroup = 16;

  /// <summary>Compresses <paramref name="data"/> using the LZRW1 control-word format.</summary>
  /// <param name="data">The uncompressed input bytes.</param>
  /// <returns>The LZRW1-encoded byte stream.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var n = data.Length;
    var output = new List<byte>(Math.Max(16, n));
    if (n == 0)
      return [];

    var hashTable = new int[Lzrw1Compressor.HashTableSize];
    hashTable.AsSpan().Fill(-1);

    var pos = 0;
    while (pos < n) {
      var controlWordPos = output.Count;
      output.Add(0);
      output.Add(0);
      var controlWord = 0;
      var itemCount = 0;

      while (itemCount < Lzrw1Compressor.ItemsPerGroup && pos < n) {
        var matchLength = 0;
        var matchOffset = 0;

        if (pos + Lzrw1Compressor.MinMatch <= n) {
          var hash = Hash(data, pos);
          var candidate = hashTable[hash];
          hashTable[hash] = pos;

          if (candidate >= 0) {
            var offset = pos - candidate;
            if (offset <= Lzrw1Compressor.MaxOffset) {
              matchLength = MatchLength(data, candidate, pos, Math.Min(Lzrw1Compressor.MaxMatch, n - pos));
              matchOffset = offset;
            }
          }
        }

        if (matchLength >= Lzrw1Compressor.MinMatch) {
          controlWord |= 1 << itemCount;
          var code = ((matchLength - Lzrw1Compressor.MinMatch) << 12) | (matchOffset - 1);
          output.Add((byte)(code >> 8));
          output.Add((byte)code);
          pos += matchLength;
        } else {
          output.Add(data[pos]);
          ++pos;
        }

        ++itemCount;
      }

      output[controlWordPos] = (byte)(controlWord >> 8);
      output[controlWordPos + 1] = (byte)controlWord;
    }

    return [.. output];
  }

  private static int Hash(ReadOnlySpan<byte> data, int position) {
    var value = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
    return (int)((uint)value * 2654435761u >> 20) & Lzrw1Compressor.HashMask;
  }

  private static int MatchLength(ReadOnlySpan<byte> data, int a, int b, int maxLength) {
    var len = 0;
    while (len < maxLength && data[a + len] == data[b + len])
      ++len;
    return len;
  }
}
