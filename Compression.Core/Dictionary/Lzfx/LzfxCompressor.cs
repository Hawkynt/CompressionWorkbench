namespace Compression.Core.Dictionary.Lzfx;

/// <summary>
/// Implements LZFX compression from its public compressed-format specification:
/// Andrew Collette, "LZFX" (LZF-derived codec with a simplified block format),
/// https://code.google.com/archive/p/lzfx/wikis/CompressedFormat.wiki.
/// </summary>
public static class LzfxCompressor {
  private const int HashLog = 16;
  private const int HashSize = 1 << LzfxCompressor.HashLog;
  private const int HashMask = LzfxCompressor.HashSize - 1;
  private const int MinMatch = 3;
  private const int MaxLiteralRun = 32;
  private const int MaxOffsetEncoded = 8191;
  private const int MaxMatch = 9 + 255;

  /// <summary>Compresses <paramref name="data"/> using the LZFX block format.</summary>
  /// <param name="data">The uncompressed input bytes.</param>
  /// <returns>The LZFX-encoded byte stream.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var n = data.Length;
    var output = new List<byte>(Math.Max(16, n));
    if (n < 3) {
      FlushLiterals(output, data, 0, n);
      return [.. output];
    }

    var hashTable = new int[LzfxCompressor.HashSize];
    hashTable.AsSpan().Fill(-1);

    var ip = 0;
    var lit = 0;

    while (ip < n - 2) {
      var hash = Hash(data, ip);
      var candidate = hashTable[hash];
      hashTable[hash] = ip;

      if (candidate >= 0 && candidate < ip) {
        var off = ip - candidate - 1;
        if (off <= LzfxCompressor.MaxOffsetEncoded &&
            data[candidate] == data[ip] && data[candidate + 1] == data[ip + 1] && data[candidate + 2] == data[ip + 2]) {
          var maxLen = Math.Min(LzfxCompressor.MaxMatch, n - ip);
          var len = 3;
          while (len < maxLen && data[candidate + len] == data[ip + len])
            ++len;

          FlushLiterals(output, data, lit, ip - lit);
          EmitMatch(output, len, off);
          ip += len;
          lit = ip;
          continue;
        }
      }

      ++ip;
      if (ip - lit >= LzfxCompressor.MaxLiteralRun) {
        FlushLiterals(output, data, lit, ip - lit);
        lit = ip;
      }
    }

    FlushLiterals(output, data, lit, n - lit);
    return [.. output];
  }

  private static int Hash(ReadOnlySpan<byte> data, int position) {
    var hval = (data[position] << 8) | data[position + 1];
    return ((hval ^ (hval >> 8)) + data[position + 2]) & LzfxCompressor.HashMask;
  }

  private static void FlushLiterals(List<byte> output, ReadOnlySpan<byte> data, int start, int length) {
    while (length > 0) {
      var chunk = Math.Min(length, LzfxCompressor.MaxLiteralRun);
      output.Add((byte)(chunk - 1));
      for (var i = 0; i < chunk; ++i)
        output.Add(data[start + i]);
      start += chunk;
      length -= chunk;
    }
  }

  private static void EmitMatch(List<byte> output, int length, int offset) {
    var encodedLength = length - 2;
    if (encodedLength < 7) {
      output.Add((byte)((encodedLength << 5) | (offset >> 8)));
      output.Add((byte)offset);
    } else {
      output.Add((byte)(0xE0 | (offset >> 8)));
      output.Add((byte)(encodedLength - 7));
      output.Add((byte)offset);
    }
  }
}
