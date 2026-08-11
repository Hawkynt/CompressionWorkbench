#pragma warning disable CS1591
namespace FileFormat.NortonGhost;

/// <summary>
/// Decompressor for Norton Ghost's <b>Fast / Z1</b> compression mode — a
/// custom LZ77 variant with a 4096-entry hash table and 16-bit control
/// words distinguishing literals from match references.
///
/// <para>
/// The algorithm is a faithful C# port of the reverse-engineered codec
/// documented in Nyarime's pure-Go parser at
/// <a href="https://github.com/nyarime/gho">github.com/nyarime/gho</a>,
/// which derived it from Norton Ghost 11.5.1's <c>sub_4DDD70</c> via IDA.
/// Because the Fast LZ codec did not change between the DOS-era Ghost 4–7
/// and Symantec Ghost 11, the same decoder reads <c>.gho</c> and
/// <c>.ghs</c> files produced by every Ghost release that used the
/// <c>FE EF</c> stream container.
/// </para>
///
/// <para>
/// Notable quirks honoured by this port:
/// <list type="bullet">
///   <item><description>Each block's first byte is a type marker handled
///     by the caller — <see cref="Decompress"/> assumes a compressed
///     payload and skips the leading 4-byte container preamble
///     (block type + size) as the original code does.</description></item>
///   <item><description>The hash table is initialised to a sentinel; when
///     a match references an unfilled slot, the original code reads from
///     the literal string <c>"123456789012345678"</c> — a Ghost-specific
///     bug-compatibility behaviour that the decompressor must replicate
///     because Ghost's encoder relies on the same sentinel.</description></item>
///   <item><description>The hash function is the Ghost-specific
///     <c>((-24993 * x) >> 4) &amp; 0xFFF</c> with the three-byte rolling
///     window — the magic constant <c>-24993</c> must match the encoder
///     so encoder/decoder agree on slot ownership.</description></item>
///   <item><description>Literal-run tracking re-inserts the missed hash
///     entries after each match — this keeps the decompressor's hash
///     table state synchronised with the encoder.</description></item>
/// </list>
/// </para>
/// </summary>
public static class FastLzDecompressor {

  private const int HashSize = 4096;
  private const int SentinelIndex = -1;

  /// <summary>Sentinel buffer used when a match resolves to an unfilled slot.</summary>
  /// <remarks>This is the Ghost-specific literal "123456789012345678" — must not change.</remarks>
  public static readonly byte[] Sentinel = "123456789012345678"u8.ToArray();

  /// <summary>
  /// Decompresses <paramref name="block"/> into <paramref name="destination"/>
  /// and returns the number of bytes written, or <c>-1</c> on a structural
  /// error (truncated input, out-of-bounds write).
  /// </summary>
  /// <param name="block">A complete Fast LZ block as stored in the Ghost
  /// stream (excluding the 2-byte length prefix the container puts in
  /// front of each block).</param>
  /// <param name="destination">Output buffer; must be large enough for the
  /// 32 KiB decompressed block plus some headroom for token overshoot.</param>
  public static int Decompress(ReadOnlySpan<byte> block, Span<byte> destination) {
    if (block.Length <= 0) return -1;

    // Block-relative first byte is the type code; the caller dispatches on
    // it, but in case a Fast-LZ block is passed with the raw 0x01 marker
    // we honour it here too.
    if (block[0] == 0x01) {
      if (block.Length < 4) return -1;
      var n = block.Length - 4;
      if (n > destination.Length) return -1;
      block[4..].CopyTo(destination);
      return n;
    }

    var hashTable = new int[HashSize];
    for (var i = 0; i < hashTable.Length; i++) hashTable[i] = SentinelIndex;

    var src = 4; // Skip block-type marker + size prefix.
    var srcEnd = block.Length;
    var outPos = 0;
    uint control = 1; // Triggers reload on first iteration.
    ushort literalRun = 0;
    ushort prevLiteralRun = 0;

    while (src < srcEnd) {
      if (control == 1) {
        if (src + 1 >= srcEnd) break;
        control = (uint)block[src] | ((uint)block[src + 1] << 8) | 0x10000u;
        src += 2;
      }

      var nearEnd = srcEnd - 32 < src;
      var tokenCount = nearEnd ? 1 : 16;

      for (var t = 0; t < tokenCount; t++) {
        if (src >= srcEnd) break;

        if ((control & 1) != 0) {
          // Match reference: 2-byte token (hashIdx + extra len).
          if (src + 1 >= srcEnd) goto done;
          var b0 = block[src];
          var b1 = block[src + 1];
          var hashIdx = b1 | ((b0 & 0xF0) << 4);
          var extraLen = b0 & 0x0F;
          var matchPos = hashTable[hashIdx];
          var matchStart = outPos;
          var totalCopy = 3 + extraLen;

          for (var j = 0; j < totalCopy; j++) {
            if (outPos >= destination.Length) return -1;
            if (matchPos == SentinelIndex)
              destination[outPos] = j < Sentinel.Length ? Sentinel[j] : (byte)0;
            else {
              var srcIdx = matchPos + j;
              destination[outPos] = srcIdx < destination.Length ? destination[srcIdx] : (byte)0;
            }
            outPos++;
          }
          src += 2;

          if (literalRun > 0) {
            var pos = matchStart - literalRun;
            if (pos >= 0 && pos + 2 < outPos) {
              var h = Hash(destination[pos], destination[pos + 1], destination[pos + 2]);
              hashTable[h] = pos;
              if (prevLiteralRun == 2 && pos + 3 < outPos) {
                var h2 = Hash(destination[pos + 1], destination[pos + 2], destination[pos + 3]);
                hashTable[h2] = pos + 1;
              }
            }
            literalRun = 0;
            prevLiteralRun = 0;
          }
          hashTable[hashIdx] = matchStart;
        } else {
          if (outPos >= destination.Length) return -1;
          literalRun++;
          destination[outPos] = block[src];
          outPos++;
          src++;
          prevLiteralRun = literalRun;

          if (literalRun == 3) {
            var pos = outPos - 3;
            var h = Hash(destination[pos], destination[pos + 1], destination[pos + 2]);
            hashTable[h] = pos;
            literalRun = 2;
            prevLiteralRun = 2;
          }
        }

        control >>= 1;
        if (control == 1) break;
      }
    }
  done:
    return outPos;
  }

  /// <summary>
  /// Ghost Fast LZ rolling 3-byte hash: <c>((-24993 * (b2 ^ (16 * (b1 ^ (16 * b0))))) &gt;&gt; 4) &amp; 0xFFF</c>.
  /// </summary>
  /// <remarks>The magic constant <c>-24993</c> must match the original encoder.</remarks>
  public static int Hash(byte b0, byte b1, byte b2) {
    var v = b2 ^ (16 * (b1 ^ (16 * b0)));
    return (int)(((uint)(unchecked(-24993 * v))) >> 4) & 0xFFF;
  }
}
