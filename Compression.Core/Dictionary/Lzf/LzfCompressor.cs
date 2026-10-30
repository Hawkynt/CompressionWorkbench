namespace Compression.Core.Dictionary.Lzf;

/// <summary>
/// LZF compression exactly as liblzf's <c>lzf_compress</c> performs it.
/// </summary>
/// <remarks>
/// <para>
/// Converted from Marc Lehmann's liblzf 3.6 <c>lzf_c.c</c> (BSD-2-Clause; see
/// THIRD-PARTY-NOTICE.liblzf.txt) with its default configuration: <c>HLOG</c> 16 and
/// <c>VERY_FAST</c>. The hash table starts zeroed, as with <c>INIT_HTAB</c>; liblzf leaves it
/// uninitialised by default, which only changes which matches are found, never the format.
/// </para>
/// <para>
/// The stream is a sequence of runs: <c>000LLLLL</c> and L+1 literal bytes, or a back-reference
/// <c>LLLooooo oooooooo</c> copying L+2 bytes from distance o+1, with L = 7 continued by an
/// extra length byte (L+2 up to 264 bytes, distance up to 8192).
/// </para>
/// </remarks>
public static class LzfCompressor {
  private const int HashLog = 16;
  private const int HashSize = 1 << HashLog;
  private const int MaxLiteral = 1 << 5;
  private const int MaxOffset = 1 << 13;
  private const int MaxReference = (1 << 8) + (1 << 3);

  /// <summary>Compresses <paramref name="data"/> into a bare LZF stream.</summary>
  /// <param name="data">The uncompressed input bytes.</param>
  /// <returns>The LZF stream; empty for empty input.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var inLength = data.Length;
    if (inLength == 0)
      return [];

    var output = new byte[inLength + inLength / MaxLiteral + 16];
    var table = new int[HashSize];
    var ip = 0;
    var op = 1;      // the first literal run's header byte
    var literals = 0;
    var hash = inLength >= 2 ? First(data, 0) : 0u;

    while (ip < inLength - 2) {
      hash = Next(hash, data, ip);
      var slot = Index(hash);
      var reference = table[slot];
      table[slot] = ip;
      var offset = (uint)(ip - reference - 1);

      if (offset < MaxOffset && reference > 0
          && data[reference + 2] == data[ip + 2] && data[reference] == data[ip] && data[reference + 1] == data[ip + 1]) {
        var maxLength = Math.Min(inLength - ip - 2, MaxReference);
        output[op - literals - 1] = (byte)(literals - 1);   // close the literal run
        op -= literals == 0 ? 1 : 0;                        // or drop it if empty

        var length = 3;
        while (length < maxLength && data[reference + length] == data[ip + length])
          ++length;

        length -= 2;                                        // octets - 1
        ++ip;
        if (length < 7)
          output[op++] = (byte)((offset >> 8) + (length << 5));
        else {
          output[op++] = (byte)((offset >> 8) + (7 << 5));
          output[op++] = (byte)(length - 7);
        }

        output[op++] = (byte)offset;
        literals = 0;
        ++op;                                               // next literal run's header
        ip += length + 1;
        if (ip >= inLength - 2)
          break;

        // VERY_FAST: re-hash the two positions before the new one.
        ip -= 2;
        hash = First(data, ip);
        hash = Next(hash, data, ip);
        table[Index(hash)] = ip;
        ++ip;
        hash = Next(hash, data, ip);
        table[Index(hash)] = ip;
        ++ip;
      } else {
        ++literals;
        output[op++] = data[ip++];
        if (literals == MaxLiteral) {
          output[op - literals - 1] = (byte)(literals - 1);
          literals = 0;
          ++op;
        }
      }
    }

    while (ip < inLength) {
      ++literals;
      output[op++] = data[ip++];
      if (literals == MaxLiteral) {
        output[op - literals - 1] = (byte)(literals - 1);
        literals = 0;
        ++op;
      }
    }

    output[op - literals - 1] = (byte)(literals - 1);
    op -= literals == 0 ? 1 : 0;
    return output[..op];
  }

  private static uint First(ReadOnlySpan<byte> data, int p) => (uint)((data[p] << 8) | data[p + 1]);

  private static uint Next(uint value, ReadOnlySpan<byte> data, int p) => (value << 8) | data[p + 2];

  private static int Index(uint hash) => (int)(((hash >> (3 * 8 - HashLog)) - hash * 5) & (HashSize - 1));
}
