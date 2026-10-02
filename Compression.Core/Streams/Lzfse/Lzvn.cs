namespace FileFormat.Lzfse;

/// <summary>
/// Implements the LZVN compression and decompression algorithm used within LZFSE blocks and by
/// HFS+/APFS transparent compression.
/// </summary>
/// <remarks>
/// <para>
/// LZVN is a byte-oriented LZ77 variant developed by Apple. Every instruction starts with one
/// opcode byte; the opcode table below is Apple's (lzfse reference implementation,
/// <c>src/lzvn_decode_base.c</c>, BSD-3-Clause, Copyright (c) 2015-2016 Apple Inc.; see
/// THIRD-PARTY-NOTICE.lzfse.txt), carried over as a format constant:
/// </para>
/// <list type="bullet">
///   <item><description><c>sml_d</c> <c>LLMMMDDD DDDDDDDD</c>: L literals (0-3), match M = MMM+3
///   at distance D (11 bits); DDD 0-5 only.</description></item>
///   <item><description><c>med_d</c> <c>101LLMMM DDDDDDMM DDDDDDDD</c>: M = (MMM&lt;&lt;2 | MM)+3,
///   D = the 14 high bits of the little-endian word.</description></item>
///   <item><description><c>lrg_d</c> <c>LLMMM111</c> + D (u16 little-endian).</description></item>
///   <item><description><c>pre_d</c> <c>LLMMM110</c>: the previous distance.</description></item>
///   <item><description><c>sml_l</c> 0xE1-0xEF: 1-15 literals; <c>lrg_l</c> 0xE0 + n: n+16.</description></item>
///   <item><description><c>sml_m</c> 0xF1-0xFF: match of 1-15 at the previous distance;
///   <c>lrg_m</c> 0xF0 + n: n+16.</description></item>
///   <item><description><c>eos</c> 0x06 followed by seven padding bytes; <c>nop</c> 0x0E, 0x16;
///   everything else (0x1E-0x3E step 8, 0x70-0x7F, 0xD0-0xDF) is undefined.</description></item>
/// </list>
/// <para>
/// The compressor emits literal runs as <c>sml_l</c>/<c>lrg_l</c>, a match as <c>sml_d</c> (distance
/// below 1536) or <c>lrg_d</c> carrying up to ten bytes, continued at the same distance with
/// <c>sml_m</c>/<c>lrg_m</c>, and ends with the eight-byte <c>eos</c>. The decompressor implements
/// the whole table.
/// </para>
/// </remarks>
internal static class Lzvn {

  private const int HashBits = 14;
  private const int HashSize = 1 << HashBits;
  private const int MaxDistance = 65535;
  private const int MinMatchLength = 4;
  private const int MaxChainSteps = 64;

  #region Compression

  /// <summary>
  /// Compresses <paramref name="src"/> using the LZVN algorithm, returning the compressed bytes.
  /// </summary>
  /// <param name="src">The uncompressed data to compress.</param>
  /// <returns>A byte array containing the LZVN-compressed data terminated with an EOS marker.</returns>
  internal static byte[] Compress(ReadOnlySpan<byte> src) {
    if (src.Length == 0)
      return [.. EndOfStream];

    using var output = new MemoryStream();
    var hashTable = new int[HashSize];
    var chain = new int[src.Length];
    Array.Fill(hashTable, -1);
    Array.Fill(chain, -1);

    var pos = 0;
    var literalStart = 0;

    while (pos < src.Length) {
      var bestLen = 0;
      var bestDist = 0;

      if (pos + MinMatchLength <= src.Length)
        FindMatch(src, pos, hashTable, chain, out bestLen, out bestDist);

      if (bestLen >= MinMatchLength) {
        // Flush all pending literals before the match.
        if (pos > literalStart)
          EmitLiterals(output, src.Slice(literalStart, pos - literalStart));

        // Emit the match, possibly as multiple opcodes for long matches.
        EmitMatch(output, bestLen, bestDist);

        // Insert all match positions into hash table.
        for (var i = 0; i < bestLen && pos + i + 3 < src.Length; i++)
          InsertHash(src, pos + i, hashTable, chain);

        pos += bestLen;
        literalStart = pos;
      } else {
        if (pos + 3 < src.Length)
          InsertHash(src, pos, hashTable, chain);
        pos++;
      }
    }

    // Flush remaining literals.
    if (literalStart < pos)
      EmitLiterals(output, src[literalStart..pos]);

    // End-of-stream: the opcode and seven padding bytes, which Apple's decoder requires.
    output.Write(EndOfStream);

    return output.ToArray();
  }

  private static readonly byte[] EndOfStream = [0x06, 0, 0, 0, 0, 0, 0, 0];

  /// <summary>Emits literal-only opcodes: <c>lrg_l</c> for 16-271 literals, <c>sml_l</c> for 1-15.</summary>
  private static void EmitLiterals(MemoryStream output, ReadOnlySpan<byte> literals) {
    var offset = 0;
    while (offset < literals.Length) {
      var count = Math.Min(literals.Length - offset, 271);
      if (count >= 16) {
        output.WriteByte(0xE0);
        output.WriteByte((byte)(count - 16));
      } else {
        output.WriteByte((byte)(0xE0 | count));
      }
      output.Write(literals.Slice(offset, count));
      offset += count;
    }
  }

  /// <summary>
  /// Emits one match: <c>sml_d</c> (distance below 1536) or <c>lrg_d</c> for its first 3-10 bytes,
  /// then <c>lrg_m</c>/<c>sml_m</c> at the same distance for the rest.
  /// </summary>
  private static void EmitMatch(MemoryStream output, int totalMatchLen, int dist) {
    var first = Math.Min(totalMatchLen, 10);
    if (dist < 1536) {
      output.WriteByte((byte)(((first - 3) << 3) | (dist >> 8)));
      output.WriteByte((byte)dist);
    } else {
      output.WriteByte((byte)(((first - 3) << 3) | 0x07));
      output.WriteByte((byte)dist);
      output.WriteByte((byte)(dist >> 8));
    }
    for (var remaining = totalMatchLen - first; remaining > 0;) {
      var m = Math.Min(remaining, 271);
      if (m >= 16) {
        output.WriteByte(0xF0);
        output.WriteByte((byte)(m - 16));
      } else {
        output.WriteByte((byte)(0xF0 | m));
      }
      remaining -= m;
    }
  }

  #endregion

  #region Decompression

  /// <summary>
  /// Decompresses LZVN-compressed data from <paramref name="src"/> into <paramref name="dst"/>.
  /// </summary>
  /// <param name="src">The LZVN-compressed data.</param>
  /// <param name="dst">The buffer to receive decompressed data. Must be sized to the expected uncompressed length.</param>
  /// <returns>The number of decompressed bytes written to <paramref name="dst"/>.</returns>
  /// <exception cref="InvalidDataException">
  /// Thrown for an undefined opcode, a match reaching before the output's start, or an
  /// instruction cut off by the end of the source.
  /// </exception>
  internal static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst) {
    var si = 0;
    var di = 0;
    var d = 0;

    while (si < src.Length && di < dst.Length) {
      var op = src[si];
      int l, m, length;
      switch (op) {
        case 0x06:
          return di;                                   // eos (its padding is not data)
        case 0x0E or 0x16:
          ++si;                                        // nop
          continue;
        case 0xE0:                                     // lrg_l
          Need(src, si, 2);
          CopyLiterals(src, ref si, 2, src[si + 1] + 16, dst, ref di);
          continue;
        case > 0xE0 and < 0xF0:                        // sml_l
          CopyLiterals(src, ref si, 1, op & 0x0F, dst, ref di);
          continue;
        case 0xF0:                                     // lrg_m
          Need(src, si, 2);
          m = src[si + 1] + 16;
          si += 2;
          CopyMatch(dst, ref di, m, d);
          continue;
        case > 0xF0:                                   // sml_m
          ++si;
          CopyMatch(dst, ref di, op & 0x0F, d);
          continue;
        case >= 0xA0 and < 0xC0: {                     // med_d
          Need(src, si, 3);
          var word = src[si + 1] | (src[si + 2] << 8);
          l = (op >> 3) & 3;
          m = (((op & 7) << 2) | (word & 3)) + 3;
          d = word >> 2;
          length = 3;
          break;
        }
        case (>= 0x70 and < 0x80) or (>= 0xD0 and < 0xE0):
          throw new InvalidDataException($"LZVN opcode 0x{op:X2} is undefined.");
        default:
          l = op >> 6;
          m = ((op >> 3) & 7) + 3;
          switch (op & 7) {
            case 7:                                    // lrg_d
              Need(src, si, 3);
              d = src[si + 1] | (src[si + 2] << 8);
              length = 3;
              break;
            case 6:                                    // pre_d (0x1E-0x3E are undefined)
              if (op < 0x40) throw new InvalidDataException($"LZVN opcode 0x{op:X2} is undefined.");
              length = 1;
              break;
            default:                                   // sml_d
              Need(src, si, 2);
              d = ((op & 7) << 8) | src[si + 1];
              length = 2;
              break;
          }
          break;
      }
      CopyLiterals(src, ref si, length, l, dst, ref di);
      CopyMatch(dst, ref di, m, d);
    }

    return di;
  }

  private static void Need(ReadOnlySpan<byte> src, int at, int length) {
    if (at + length > src.Length)
      throw new InvalidDataException("LZVN instruction is truncated.");
  }

  private static void CopyLiterals(ReadOnlySpan<byte> src, ref int si, int opcodeLength, int count, Span<byte> dst, ref int di) {
    if (si + opcodeLength + count > src.Length)
      throw new InvalidDataException("LZVN literal run is truncated.");
    si += opcodeLength;
    var take = Math.Min(count, dst.Length - di);
    src.Slice(si, take).CopyTo(dst[di..]);
    si += count;
    di += take;
  }

  private static void CopyMatch(Span<byte> dst, ref int di, int length, int distance) {
    if (length == 0) return;
    if (distance <= 0 || distance > di)
      throw new InvalidDataException($"LZVN match distance {distance} exceeds output position {di}.");
    // Byte by byte: an overlapping match (distance below length) repeats what it just wrote.
    for (var i = 0; i < length && di < dst.Length; ++i, ++di)
      dst[di] = dst[di - distance];
  }

  #endregion

  #region Hash chain match finder

  private static void FindMatch(ReadOnlySpan<byte> src, int pos, int[] hashTable, int[] chain, out int bestLen, out int bestDist) {
    bestLen = 0;
    bestDist = 0;

    if (pos + 3 >= src.Length)
      return;

    var h = Hash4(src, pos);
    var candidate = hashTable[h];
    // Allow finding long matches - they'll be split into multiple opcodes.
    var maxLen = Math.Min(src.Length - pos, 256);
    var minPos = Math.Max(0, pos - MaxDistance);

    var attempts = MaxChainSteps;
    while (candidate >= minPos && attempts-- > 0) {
      if (src[candidate] == src[pos] && src[candidate + 1] == src[pos + 1]) {
        var len = 0;
        while (len < maxLen && src[candidate + len] == src[pos + len])
          len++;

        if (len >= MinMatchLength && len > bestLen) {
          bestLen = len;
          bestDist = pos - candidate;
          if (len == maxLen)
            break;
        }
      }

      var prev = chain[candidate];
      if (prev >= candidate)
        break;
      candidate = prev;
    }
  }

  private static int Hash4(ReadOnlySpan<byte> data, int pos) =>
    ((data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24)) * unchecked((int)0x9E3779B1)) >>> (32 - HashBits);

  private static void InsertHash(ReadOnlySpan<byte> src, int pos, int[] hashTable, int[] chain) {
    var h = Hash4(src, pos);
    chain[pos] = hashTable[h];
    hashTable[h] = pos;
  }

  #endregion
}
