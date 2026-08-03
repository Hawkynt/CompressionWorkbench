namespace Compression.Core.Dictionary.Lzf;

/// <summary>
/// Implements LZF compression from the public liblzf wire-format specification:
/// Marc Lehmann, "LZF: a very small data compression library",
/// http://software.schmorp.de/pkg/liblzf.html.
/// </summary>
public static class LzfCompressor {
  private const int HashLog = 13;
  private const int HashSize = 1 << LzfCompressor.HashLog;
  private const int HashMask = LzfCompressor.HashSize - 1;
  private const int MinMatch = 2;
  private const int MaxLiteralRun = 32;
  private const int MaxOffsetEncoded = 8191;
  private const int MaxMatch = 8 + 255;

  /// <summary>Compresses <paramref name="data"/> using the LZF wire format.</summary>
  /// <param name="data">The uncompressed input bytes.</param>
  /// <returns>The LZF-encoded byte stream.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var n = data.Length;
    var output = new List<byte>(Math.Max(16, n));
    if (n < 3) {
      FlushLiterals(output, data, 0, n);
      return [.. output];
    }

    var hashTable = new int[LzfCompressor.HashSize];
    hashTable.AsSpan().Fill(-1);

    var ip = 0;
    var lit = 0;

    while (ip < n - 2) {
      var hash = Hash(data, ip);
      var candidate = hashTable[hash];
      hashTable[hash] = ip;

      if (candidate >= 0 && candidate < ip) {
        var off = ip - candidate - 1;
        if (off <= LzfCompressor.MaxOffsetEncoded && data[candidate] == data[ip] && data[candidate + 1] == data[ip + 1]) {
          var maxLen = Math.Min(LzfCompressor.MaxMatch, n - ip);
          var len = 2;
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
      if (ip - lit >= LzfCompressor.MaxLiteralRun) {
        FlushLiterals(output, data, lit, ip - lit);
        lit = ip;
      }
    }

    FlushLiterals(output, data, lit, n - lit);
    return [.. output];
  }

  private static int Hash(ReadOnlySpan<byte> data, int position) {
    var hval = (data[position] << 8) | data[position + 1];
    return ((hval ^ (hval >> 8)) + data[position + 2]) & LzfCompressor.HashMask;
  }

  private static void FlushLiterals(List<byte> output, ReadOnlySpan<byte> data, int start, int length) {
    while (length > 0) {
      var chunk = Math.Min(length, LzfCompressor.MaxLiteralRun);
      output.Add((byte)(chunk - 1));
      for (var i = 0; i < chunk; ++i)
        output.Add(data[start + i]);
      start += chunk;
      length -= chunk;
    }
  }

  private static void EmitMatch(List<byte> output, int length, int offset) {
    var encodedLength = length - 1;
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
