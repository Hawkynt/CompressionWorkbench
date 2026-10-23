namespace Compression.Core.Dictionary.Lzjb;

/// <summary>
/// LZJB compression, Jeff Bonwick's compressor from Solaris and ZFS, in the bitstream
/// <see cref="LzjbDecompressor"/> documents.
/// </summary>
/// <remarks>
/// The match finder follows ZFS's: a 1024-entry table of last positions indexed by a hash
/// of the next three bytes, distances up to 1023, matches of 3-66 bytes, and the final 66
/// input bytes always emitted as literals. Written from <c>docs/ZFS-ON-DISK.md</c>;
/// OpenZFS (CDDL) was read for behaviour only.
/// </remarks>
public static class LzjbCompressor {
  private const int MatchBits = 6;
  private const int MatchMin = 3;
  private const int MatchMax = (1 << LzjbCompressor.MatchBits) + (LzjbCompressor.MatchMin - 1);
  private const int OffsetMask = (1 << (16 - LzjbCompressor.MatchBits)) - 1;
  private const int LempelSize = 1024;

  /// <summary>Compresses <paramref name="data"/> using the LZJB copymap format.</summary>
  /// <param name="data">The uncompressed input bytes.</param>
  /// <returns>The LZJB-encoded byte stream.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var n = data.Length;
    if (n == 0)
      return [];

    // One copy-map byte per eight items, at most one byte per literal item.
    var output = new byte[n + (n + 7) / 8];
    Span<int> lempel = stackalloc int[LzjbCompressor.LempelSize];
    lempel.Clear();

    var op = 0;
    var copymapAt = 0;
    var bit = 8;
    for (var ip = 0; ip < n;) {
      if (bit == 8) {
        copymapAt = op;
        output[op++] = 0;
        bit = 0;
      }

      if (ip > n - LzjbCompressor.MatchMax) {
        output[op++] = data[ip++];
        ++bit;
        continue;
      }

      var hash = (data[ip] << 16) + (data[ip + 1] << 8) + data[ip + 2];
      hash += hash >> 9;
      hash += hash >> 5;
      ref var slot = ref lempel[hash & (LzjbCompressor.LempelSize - 1)];
      var distance = (ip - slot) & LzjbCompressor.OffsetMask;
      slot = ip;
      var from = ip - distance;
      if (distance != 0 && from >= 0
          && data[ip] == data[from] && data[ip + 1] == data[from + 1] && data[ip + 2] == data[from + 2]) {
        var length = LzjbCompressor.MatchMin;
        while (length < LzjbCompressor.MatchMax && data[ip + length] == data[from + length])
          ++length;
        output[copymapAt] |= (byte)(1 << bit);
        output[op++] = (byte)(((length - LzjbCompressor.MatchMin) << (8 - LzjbCompressor.MatchBits)) | (distance >> 8));
        output[op++] = (byte)distance;
        ip += length;
      } else
        output[op++] = data[ip++];

      ++bit;
    }

    return output.AsSpan(0, op).ToArray();
  }
}
