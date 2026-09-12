#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Ghost;

/// <summary>
/// Ghost Fast LZ (Z1) codec — a custom LZ77 variant with a 4096-entry
/// hash table reverse-engineered from Norton Ghost 11.5.1 (port of the
/// MIT-licensed nyarime/gho Go reference implementation).
/// </summary>
/// <remarks>
/// <para>
/// Block layout: <c>[1 byte tag][3 bytes pad][payload]</c>. Tag <c>1</c>
/// means the payload is uncompressed; any other tag means the payload is
/// a Fast LZ stream of 16-bit control words.
/// </para>
/// <para>
/// Each control word covers up to 16 tokens; bit <c>i</c> selects
/// "match" (1) or "literal" (0) for token <c>i</c>. A literal is a single
/// raw byte; a match is two bytes
/// <c>(b0, b1) -&gt; hash_idx = b1 | ((b0 &amp; 0xF0) &lt;&lt; 4)</c>,
/// <c>extra_len = b0 &amp; 0x0F</c>, copying <c>3 + extra_len</c> bytes
/// from the position stored in the hash table.
/// </para>
/// <para>
/// Hash function:
/// <c>h = ((-24993 * (b2 ^ (16 * (b1 ^ (16 * b0))))) &gt;&gt; 4) &amp; 0xFFF</c>.
/// </para>
/// <para>
/// The hash table is seeded with a special "sentinel" string
/// (<c>"123456789012345678"</c>) — a hash entry not yet populated by a
/// match-or-literal-run update copies bytes out of this sentinel buffer
/// instead, matching what the original C decoder does when an unpopulated
/// hash slot is referenced.
/// </para>
/// </remarks>
public static class GhostFastLz {

  private static readonly byte[] Sentinel = Encoding.ASCII.GetBytes("123456789012345678");
  private const int SentinelIdx = -1;

  /// <summary>
  /// Compute the Ghost Fast LZ 12-bit hash for three consecutive bytes.
  /// Mirrors the original integer truncation semantics — the 32-bit
  /// multiply wraps around modulo 2^32.
  /// </summary>
  public static int Hash(byte b0, byte b1, byte b2) {
    var v = (int)b2 ^ (16 * ((int)b1 ^ (16 * (int)b0)));
    var prod = unchecked((uint)(-24993 * v));
    return (int)((prod >> 4) & 0xFFF);
  }

  /// <summary>
  /// Decompress one block. <paramref name="data"/> is the raw block bytes
  /// (header + payload), <paramref name="compLen"/> the total compressed
  /// length, <paramref name="dst"/> the destination buffer (must be at
  /// least <see cref="GhostConstants.BlockSize"/> bytes).
  /// </summary>
  /// <returns>Number of bytes written to <paramref name="dst"/>.</returns>
  public static int Decompress(ReadOnlySpan<byte> data, int compLen, Span<byte> dst) {
    if (compLen <= 0 || data.Length < compLen)
      throw new InvalidDataException("Ghost FastLZ: truncated block.");

    // Uncompressed tag.
    if (data[0] == 1) {
      var n = compLen - 4;
      if (n <= 0 || n > dst.Length)
        throw new InvalidDataException("Ghost FastLZ: corrupt uncompressed block length.");
      data.Slice(4, n).CopyTo(dst);
      return n;
    }

    var hashTable = new int[GhostConstants.FastLzHashSize];
    for (var i = 0; i < hashTable.Length; i++) hashTable[i] = SentinelIdx;

    var src = 4; // Skip the 4-byte block header.
    var srcEnd = compLen;
    var outPos = 0;

    uint control = 1; // Triggers reload on first iteration.
    ushort literalRun = 0;
    ushort prevLiteralRun = 0;

    while (src < srcEnd) {
      if (control == 1) {
        if (src + 1 >= srcEnd) break;
        control = data[src] | ((uint)data[src + 1] << 8) | 0x10000;
        src += 2;
      }

      var tokenCount = srcEnd - 32 < src ? 1 : 16;

      var needReload = false;
      for (var t = 0; t < tokenCount && src < srcEnd && !needReload; t++) {
        if ((control & 1) != 0) {
          // Match.
          if (src + 1 >= srcEnd) return outPos;

          var b0 = data[src];
          var b1 = data[src + 1];
          var hashIdx = b1 | ((b0 & 0xF0) << 4);
          var extraLen = b0 & 0x0F;
          var matchPos = hashTable[hashIdx];
          var matchStart = outPos;
          var totalCopy = 3 + extraLen;

          for (var j = 0; j < totalCopy; j++) {
            if (outPos >= dst.Length)
              throw new InvalidDataException("Ghost FastLZ: destination overflow.");
            if (matchPos == SentinelIdx) {
              dst[outPos] = j < Sentinel.Length ? Sentinel[j] : (byte)0;
            } else {
              var srcIdx = matchPos + j;
              dst[outPos] = srcIdx < dst.Length ? dst[srcIdx] : (byte)0;
            }
            outPos++;
          }

          src += 2;

          if (literalRun > 0) {
            var pos = matchStart - literalRun;
            if (pos >= 0 && pos + 2 < outPos) {
              var h = Hash(dst[pos], dst[pos + 1], dst[pos + 2]);
              hashTable[h] = pos;
              if (prevLiteralRun == 2 && pos + 3 < outPos) {
                var h2 = Hash(dst[pos + 1], dst[pos + 2], dst[pos + 3]);
                hashTable[h2] = pos + 1;
              }
            }
            literalRun = 0;
            prevLiteralRun = 0;
          }

          hashTable[hashIdx] = matchStart;
        } else {
          // Literal.
          if (outPos >= dst.Length)
            throw new InvalidDataException("Ghost FastLZ: destination overflow.");
          literalRun++;
          dst[outPos] = data[src];
          outPos++;
          src++;
          prevLiteralRun = literalRun;

          if (literalRun == 3) {
            var pos = outPos - 3;
            var h = Hash(dst[pos], dst[pos + 1], dst[pos + 2]);
            hashTable[h] = pos;
            literalRun = 2;
            prevLiteralRun = 2;
          }
        }

        control >>= 1;
        if (control == 1) needReload = true; // Need fresh control word.
      }
    }

    return outPos;
  }

  /// <summary>
  /// Compress <paramref name="src"/> as a Ghost Fast LZ block (header +
  /// payload). If the compressed form would be larger than just storing
  /// the literal payload, returns an uncompressed block (tag = 1).
  /// </summary>
  public static byte[] Compress(ReadOnlySpan<byte> src) {
    if (src.Length == 0) return [];
    if (src.Length < 18) return StoreUncompressed(src);
    var compressed = CompressInner(src);
    return compressed == null || compressed.Length >= src.Length + 4 ? StoreUncompressed(src) : compressed;
  }

  /// <summary>Wrap raw bytes in a tag-1 (uncompressed) block.</summary>
  public static byte[] StoreUncompressed(ReadOnlySpan<byte> src) {
    var output = new byte[4 + src.Length];
    output[0] = 1;
    src.CopyTo(output.AsSpan(4));
    return output;
  }

  private static byte[]? CompressInner(ReadOnlySpan<byte> src) {
    var n = src.Length;
    var output = new List<byte>(4 + n + n / 8 + 64) { 0, 0, 0, 0 };

    var hashTable = new int[GhostConstants.FastLzHashSize];
    for (var i = 0; i < hashTable.Length; i++) hashTable[i] = SentinelIdx;

    var pos = 0;
    ushort literalRun = 0;
    ushort prevLiteralRun = 0;
    var tokenData = new List<byte>(34);

    while (pos < n) {
      ushort controlBits = 0;
      tokenData.Clear();
      var tokenCount = 0;

      while (tokenCount < 16 && pos < n) {
        var matchLen = 0;
        var matchHashIdx = 0;

        if (pos + 2 < n) {
          var h = Hash(src[pos], src[pos + 1], src[pos + 2]);
          var matchPos = hashTable[h];
          if (matchPos >= 0 && matchPos < pos) {
            var ml = 0;
            var maxMatch = 18;
            if (pos + maxMatch > n) maxMatch = n - pos;
            while (ml < maxMatch && src[matchPos + ml] == src[pos + ml]) ml++;
            if (ml >= 3) {
              matchLen = ml;
              matchHashIdx = h;
            }
          }
        }

        if (matchLen >= 3) {
          var extraLen = matchLen - 3;
          var b0 = (byte)((extraLen & 0x0F) | ((matchHashIdx >> 4) & 0xF0));
          var b1 = (byte)(matchHashIdx & 0xFF);
          tokenData.Add(b0);
          tokenData.Add(b1);
          controlBits |= (ushort)(1 << tokenCount);

          var matchStart = pos;
          pos += matchLen;

          if (literalRun > 0) {
            var litPos = matchStart - literalRun;
            if (litPos >= 0 && litPos + 2 < pos) {
              var lh = Hash(src[litPos], src[litPos + 1], src[litPos + 2]);
              hashTable[lh] = litPos;
              if (prevLiteralRun == 2 && litPos + 3 < pos) {
                var lh2 = Hash(src[litPos + 1], src[litPos + 2], src[litPos + 3]);
                hashTable[lh2] = litPos + 1;
              }
            }
            literalRun = 0;
            prevLiteralRun = 0;
          }
          hashTable[matchHashIdx] = matchStart;
        } else {
          tokenData.Add(src[pos]);
          literalRun++;
          pos++;
          prevLiteralRun = literalRun;
          if (literalRun == 3) {
            var litPos = pos - 3;
            var lh = Hash(src[litPos], src[litPos + 1], src[litPos + 2]);
            hashTable[lh] = litPos;
            literalRun = 2;
            prevLiteralRun = 2;
          }
        }

        tokenCount++;
      }

      output.Add((byte)controlBits);
      output.Add((byte)(controlBits >> 8));
      output.AddRange(tokenData);
    }

    return output.ToArray();
  }
}
