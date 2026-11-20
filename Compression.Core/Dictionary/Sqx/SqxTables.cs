namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// Constants of the SQX LZ77 + Huffman stream (methods 1-4), as Squeez-c's <c>huff.h</c>/<c>huff.c</c>
/// and <c>lz.h</c> define them: symbol alphabets of formats 1.1 and 2.0, length and distance base
/// tables, and match-length thresholds.
/// </summary>
public static class SqxTables {
  /// <summary>Archive format 1.1 (<c>cArcerExVer</c> 11).</summary>
  public const byte Format11 = 11;

  /// <summary>Archive format 2.0 (<c>cArcerExVer</c> 20).</summary>
  public const byte Format20 = 20;

  internal const int MetaCodes = 19;
  internal const int MetaCodeBits = 7;
  internal const int CodeBits = 15;
  internal const int LiteralCodes = 256;
  internal const int Len2Codes = 8;
  internal const int Len3Codes = 15;
  internal const int LenCodes = 24 + 1;
  internal const int DistCodes11 = 56;
  internal const int DistCodes20 = 54;
  internal const int Low16Codes = 16;

  /// <summary>Format 1.1 symbol layout.</summary>
  internal const int Len2Start = LiteralCodes + 1 + 4;
  internal const int Len3Start = Len2Start + Len2Codes;
  internal const int LenStart = Len3Start + Len3Codes;
  internal const int LitLenCodes11 = LenStart + LenCodes + 1;

  /// <summary>Format 2.0 symbol layout (two extra codes, 257 and 285, shift the groups).</summary>
  internal const int LitLenCodes20 = 311;
  internal const int Len2Start20 = 262;
  internal const int Len3Start20 = 270;
  internal const int LenStart20 = 286;
  internal const int SymCopyOne = 257;

  internal const uint MinLen2 = 0xFF;
  internal const uint MinLen3 = 0x3FFF;
  internal const uint MinLen4 = 0x3FFFF;
  internal const int MaxMatch = 16640;

  internal static ReadOnlySpan<uint> Len2Offsets => [0, 4, 8, 16, 32, 64, 128, 192];
  internal static ReadOnlySpan<int> Len2ExtraBits => [2, 2, 3, 4, 5, 6, 6, 6];
  internal static ReadOnlySpan<uint> Len3Offsets => [0, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384];
  internal static ReadOnlySpan<int> Len3ExtraBits => [0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14];
  internal static ReadOnlySpan<uint> LenOffsets => [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 224, 0];
  internal static ReadOnlySpan<int> LenExtraBits => [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 5, 5, 5, 0];

  /// <summary>
  /// The distance codes of a dictionary size (SQX 1.1 format notes, table 3.3): codes 0-3 are the
  /// distances themselves; from code 4 on the bases double every two codes with one more extra
  /// bit each time, until the extra bits reach the dictionary's limit (16 up to 1 MB, 17 for 2 MB,
  /// 18 for 4 MB, one more per doubling after that); from there every code adds a constant step
  /// of 2^limit. Codes past the dictionary size are never written.
  /// </summary>
  internal static (uint[] Base, int[] ExtraBits) DistanceCodes(int dictionarySize) {
    var limit = Math.Max(16, System.Numerics.BitOperations.Log2((uint)dictionarySize) - 4);
    var bases = new uint[DistCodes11];
    var extras = new int[DistCodes11];
    for (var c = 0; c < bases.Length; ++c) {
      if (c < 4) {
        bases[c] = (uint)c;
        continue;
      }
      var extra = c / 2 - 1;
      if (extra <= limit) {
        extras[c] = extra;
        bases[c] = (uint)(2 | (c & 1)) << extra;
      } else {
        extras[c] = limit;
        var next = (ulong)bases[c - 1] + (1UL << extras[c - 1]);
        bases[c] = next > uint.MaxValue ? uint.MaxValue : (uint)next;
      }
    }
    return (bases, extras);
  }

  /// <summary>Dictionary size from a file header's flags (bits 8-10, and bit 11 for 2.0's extended sizes).</summary>
  public static int DictionarySize(ushort flags, byte format) {
    var index = (flags >> 8) & 7;
    if (format >= Format20 && (flags & 0x0800) != 0)
      return (8 << 20) << index;
    return 1 << (15 + index);
  }

  /// <summary>File header flag bits that announce <paramref name="dictionarySize"/>.</summary>
  public static ushort DictionaryFlags(int dictionarySize, byte format) {
    var n = 0;
    if (format >= Format20 && dictionarySize > 4 << 20) {
      while (n < 7 && (8 << 20) << n < dictionarySize)
        ++n;
      return (ushort)(0x0800 | n << 8);
    }

    while (n < 7 && 32768 << n < dictionarySize)
      ++n;
    return (ushort)(n << 8);
  }
}
