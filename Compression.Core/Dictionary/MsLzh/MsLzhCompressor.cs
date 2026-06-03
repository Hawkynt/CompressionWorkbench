using System.Buffers.Binary;
using Compression.Core.BitIO;
using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Dictionary.MsLzh;

/// <summary>
/// MS LZH compressor — DriveSpace 3 (Win95 Plus! Pack, 1995) family codec.
/// LZ77 with 4 KiB window + canonical Huffman over a 286-symbol literal/length
/// alphabet (DEFLATE-shaped) and a 30-symbol distance alphabet.
/// <para>
/// <b>Effort tiers</b> (mirrors <c>DsLz77Compressor</c>):
/// </para>
/// <list type="bullet">
///   <item><b>0 — Greedy</b> (default): bounded hash-chain depth (64), greedy
///     match selection, fixed Huffman tables per RFC 1951 §3.2.6 shape — see
///     <see cref="MsLzhFixedTables"/>. Fast. Matches the historical effort-0
///     behaviour bit-for-bit (modulo the new leading block-type bit).</item>
///   <item><b>1 — Lazy</b> (<c>+</c>): at each candidate match position, look
///     ahead one byte; emit a literal when <c>(pos + 1)</c> would yield a
///     strictly longer match. Hash chain deepened to 1024. Same fixed Huffman
///     tables — encoder output stays self-consistent with the decoder. Roughly
///     5-10× slower, typically a few percent smaller on compressible inputs.</item>
///   <item><b>2+ — Iterated + dynamic Huffman</b> (<c>++</c>): runs the
///     effort-1 lazy parse, then compares the cost of encoding the resulting
///     token stream with the fixed tables vs. with per-block dynamic
///     Huffman tables (RFC 1951 §3.2.7 layout — see
///     <see cref="MsLzhDynamicHuffman"/>). The smaller of the two wins. Also
///     sweeps the min-match floor (3, 4, 5) like the older effort-2 parse.</item>
/// </list>
/// <para>
/// <b>Bit-stream format.</b> Every block is prefixed by a single block-type
/// bit: <c>0</c> = fixed Huffman tables (RFC 1951 §3.2.6 shape), <c>1</c> =
/// dynamic per-block Huffman tables. Both paths share the same length /
/// distance extra-bit conventions and the end-of-block marker (symbol 256).
/// </para>
/// <para>
/// <b>Termination invariant for iterated parsing:</b> the candidate set has a
/// fixed cap (4 passes max — baseline lazy plus three min-match sweeps), each
/// pass produces a complete output, and we retain the smallest. This guarantees
/// effort 2+ is monotone: <c>len(eff 2) ≤ len(eff 1) ≤ len(eff 0)</c> on
/// compressible inputs, with strict equality possible for inputs where the
/// effort-0 parse is already optimal.
/// </para>
/// <para>
/// <b>Not yet bit-compatible with Microsoft's reference decoder.</b> The
/// dynamic Huffman header layout matches RFC 1951 semantically but the
/// MS LZH per-cluster framing (block-count bytes, dictionary-init values)
/// has not been reverse-engineered from a real DRVSPACE.000 image. Self
/// round-trip is the gating requirement; cross-tool compatibility remains a
/// stretch goal.
/// </para>
/// </summary>
public sealed class MsLzhCompressor {

  /// <summary>Compresses <paramref name="data"/> with the default effort 0 (greedy + fixed tables).</summary>
  public byte[] Compress(ReadOnlySpan<byte> data) => this.Compress(data, effort: 0);

  /// <summary>
  /// Compresses <paramref name="data"/> at the requested
  /// <paramref name="effort"/> level. Negative values are clamped to 0
  /// (greedy). Values above 2 are treated as 2 (iterated + dynamic Huffman).
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data, int effort) {
    var clampedEffort = Math.Clamp(effort, 0, 2);

    return clampedEffort switch {
      0 => EncodeGreedyFixed(data),
      1 => EncodeLazyFixed(data, chainDepth: 1024, minMatch: MsLzhConstants.MinMatch),
      _ => EncodeIteratedDynamic(data),
    };
  }

  // =========================================================================
  //         Token buffer — symbol stream produced by the parser, fed
  //         into either the fixed-table or dynamic-table emitter
  // =========================================================================

  /// <summary>
  /// One emitted token. A literal has <c>DistSym = -1</c>; a length+distance
  /// pair has the literal/length symbol in <see cref="LitLenSym"/> plus the
  /// distance symbol and its extra bits.
  /// </summary>
  private readonly record struct Token(
    int LitLenSym, int LenExtraBits, int LenExtraValue,
    int DistSym, int DistExtraBits, int DistExtraValue);

  // =========================================================================
  //               Effort 0 — historical greedy + fixed tables
  // =========================================================================

  private static byte[] EncodeGreedyFixed(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var writer = new BitWriter<MsbBitOrder>(ms);
    // Block-type bit: 0 = fixed Huffman tables.
    writer.WriteBit(MsLzhDynamicHuffman.BlockTypeFixed);
    EncodeBodyGreedyFixed(data, writer);
    // End-of-block.
    var (eofCode, eofLen) = MsLzhFixedTables.LitLen.GetCode(MsLzhConstants.EndOfBlockSymbol);
    writer.WriteBits(eofCode, eofLen);
    writer.FlushBits();
    return ms.ToArray();
  }

  private static void EncodeBodyGreedyFixed(ReadOnlySpan<byte> data, BitWriter<MsbBitOrder> writer) {
    const int HashSize = 1 << 13;
    var hashHead = new int[HashSize];
    var hashNext = new int[data.Length];
    Array.Fill(hashHead, -1);
    Array.Fill(hashNext, -1);

    var pos = 0;
    while (pos < data.Length) {
      // Insert into hash chain.
      if (pos + 2 < data.Length) {
        var h = Hash3(data, pos);
        hashNext[pos] = hashHead[h];
        hashHead[h] = pos;
      }

      var (bestLen, bestOff) = FindBestMatch(data, pos, hashHead, hashNext, MsLzhConstants.MinMatch, maxChainLen: 64);

      if (bestLen >= MsLzhConstants.MinMatch) {
        WriteMatchFixed(writer, bestLen, bestOff);
        // Update hash chain for interior positions so straddling matches work.
        var insertEnd = Math.Min(pos + bestLen, data.Length - 2);
        for (var j = pos + 1; j < insertEnd; j++) {
          var h = Hash3(data, j);
          hashNext[j] = hashHead[h];
          hashHead[h] = j;
        }
        pos += bestLen;
      } else {
        WriteLiteralFixed(writer, data[pos]);
        pos++;
      }
    }
  }

  // =========================================================================
  //                Effort 1 — lazy matching (look-ahead 1) + fixed tables
  // =========================================================================

  private static byte[] EncodeLazyFixed(ReadOnlySpan<byte> data, int chainDepth, int minMatch) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var tokens = ParseToTokens(data, chainDepth, minMatch);

    var writer = new BitWriter<MsbBitOrder>(ms);
    writer.WriteBit(MsLzhDynamicHuffman.BlockTypeFixed);
    foreach (var t in tokens) {
      if (t.DistSym < 0)
        WriteLiteralFixed(writer, (byte)t.LitLenSym);
      else
        WriteMatchFixedFromToken(writer, t);
    }
    var (eofCode, eofLen) = MsLzhFixedTables.LitLen.GetCode(MsLzhConstants.EndOfBlockSymbol);
    writer.WriteBits(eofCode, eofLen);
    writer.FlushBits();
    return ms.ToArray();
  }

  // =========================================================================
  //                Effort 2+ — iterated + dynamic Huffman
  // =========================================================================

  /// <summary>
  /// Iterated parse: runs the effort-1 lazy parse with progressively higher
  /// min-match thresholds (3, 4, 5) and, for each token stream, picks the
  /// smaller of static-fixed-table coding vs. dynamic per-block Huffman
  /// coding. Keeps the smallest output overall.
  /// <para>
  /// <b>Dynamic vs static comparison.</b> Dynamic Huffman adds a per-block
  /// header (HLIT/HDIST/HCLEN + code-length-code table + lit/len + distance
  /// code-length lists). For small or near-uniform-distribution blocks the
  /// header overhead can exceed the payload savings, in which case we emit
  /// fixed tables. The cost-comparison is exact (bits-precise) — we count
  /// header + per-symbol-frequency × code-length for both layouts and keep
  /// the smaller.
  /// </para>
  /// <para>
  /// Always returns no larger than the effort-1 baseline (the first iteration
  /// IS the effort-1 result). Iteration cap is fixed at three passes plus the
  /// baseline so wall time stays bounded.
  /// </para>
  /// </summary>
  private static byte[] EncodeIteratedDynamic(ReadOnlySpan<byte> data) {
    if (data.Length == 0) {
      using var emptyMs = new MemoryStream();
      Span<byte> header = stackalloc byte[4];
      BinaryPrimitives.WriteInt32LittleEndian(header, 0);
      emptyMs.Write(header);
      return emptyMs.ToArray();
    }

    var asArray = data.ToArray();

    int[] minMatchSweep = [MsLzhConstants.MinMatch, 4, 5, 6];
    var bestBytes = (byte[]?)null;

    for (var pass = 0; pass < minMatchSweep.Length; ++pass) {
      var minMatch = minMatchSweep[pass];
      var chainDepth = pass == 0 ? 1024 : 2048;
      var tokens = ParseToTokens(asArray, chainDepth, minMatch);

      var candidate = EncodePickSmallerBlock(asArray.Length, tokens);
      if (bestBytes == null || candidate.Length < bestBytes.Length)
        bestBytes = candidate;
    }

    return bestBytes!;
  }

  /// <summary>
  /// Given a token stream, evaluates the bit cost of encoding it with the
  /// fixed tables vs. with per-block dynamic Huffman tables built from this
  /// stream's symbol frequencies, and returns the smaller-encoded byte
  /// array.
  /// </summary>
  private static byte[] EncodePickSmallerBlock(int originalSize, List<Token> tokens) {
    var litLenFreq = new long[MsLzhConstants.LitLenAlphabetSize];
    var distFreq = new long[MsLzhConstants.DistanceAlphabetSize];
    foreach (var t in tokens) {
      litLenFreq[t.LitLenSym]++;
      if (t.DistSym >= 0)
        distFreq[t.DistSym]++;
    }
    litLenFreq[MsLzhConstants.EndOfBlockSymbol]++;

    var dynLitLenLengths = MsLzhDynamicHuffman.BuildCodeLengths(litLenFreq, MsLzhConstants.LitLenAlphabetSize);
    var anyDist = distFreq.Any(t => t > 0);
    var dynDistLengths = anyDist
      ? MsLzhDynamicHuffman.BuildCodeLengths(distFreq, MsLzhConstants.DistanceAlphabetSize)
      : SinglePlaceholderDistanceLengths();

    var staticBits = MsLzhDynamicHuffman.EstimateStaticBlockBits(litLenFreq, distFreq);
    var dynamicBits = MsLzhDynamicHuffman.EstimateDynamicBlockBits(
      litLenFreq, dynLitLenLengths, distFreq, dynDistLengths);

    var useDynamic = dynamicBits < staticBits;

    return useDynamic
      ? EmitDynamicBlock(originalSize, tokens, dynLitLenLengths, dynDistLengths)
      : EmitFixedBlock(originalSize, tokens);
  }

  /// <summary>
  /// Returns a placeholder distance code-length array with one used symbol
  /// (length 1) so the canonical Huffman table is valid even though no
  /// distance codes are actually emitted in the payload.
  /// </summary>
  private static int[] SinglePlaceholderDistanceLengths() {
    var distLengths = new int[MsLzhConstants.DistanceAlphabetSize];
    distLengths[0] = 1;
    return distLengths;
  }

  private static byte[] EmitFixedBlock(int originalSize, List<Token> tokens) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, originalSize);
    ms.Write(header);

    var writer = new BitWriter<MsbBitOrder>(ms);
    writer.WriteBit(MsLzhDynamicHuffman.BlockTypeFixed);
    foreach (var t in tokens) {
      if (t.DistSym < 0)
        WriteLiteralFixed(writer, (byte)t.LitLenSym);
      else
        WriteMatchFixedFromToken(writer, t);
    }
    var (eofCode, eofLen) = MsLzhFixedTables.LitLen.GetCode(MsLzhConstants.EndOfBlockSymbol);
    writer.WriteBits(eofCode, eofLen);
    writer.FlushBits();
    return ms.ToArray();
  }

  private static byte[] EmitDynamicBlock(
      int originalSize, List<Token> tokens,
      int[] litLenLengths, int[] distLengths) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, originalSize);
    ms.Write(header);

    var writer = new BitWriter<MsbBitOrder>(ms);
    writer.WriteBit(MsLzhDynamicHuffman.BlockTypeDynamic);
    var (litLenHuf, distHuf) = MsLzhDynamicHuffman.WriteHeader(writer, litLenLengths, distLengths);

    foreach (var t in tokens) {
      if (t.DistSym < 0) {
        var (code, codeLen) = litLenHuf.GetCode(t.LitLenSym);
        writer.WriteBits(code, codeLen);
        continue;
      }
      var (lenCode, lenCodeLen) = litLenHuf.GetCode(t.LitLenSym);
      writer.WriteBits(lenCode, lenCodeLen);
      if (t.LenExtraBits > 0)
        writer.WriteBits((uint)t.LenExtraValue, t.LenExtraBits);
      var (distCode, distCodeLen) = distHuf.GetCode(t.DistSym);
      writer.WriteBits(distCode, distCodeLen);
      if (t.DistExtraBits > 0)
        writer.WriteBits((uint)t.DistExtraValue, t.DistExtraBits);
    }

    var (eofCode2, eofLen2) = litLenHuf.GetCode(MsLzhConstants.EndOfBlockSymbol);
    writer.WriteBits(eofCode2, eofLen2);
    writer.FlushBits();
    return ms.ToArray();
  }

  // =========================================================================
  //                       Lazy parse → token stream
  // =========================================================================

  /// <summary>
  /// Runs the lazy-parse over <paramref name="data"/> and emits a token
  /// stream (literals + length/distance pairs) ready to be fed into either
  /// the fixed-table or dynamic-Huffman emitter. The parser itself is the
  /// same as the historical effort-1 implementation; the only change is
  /// that it appends tokens to a list instead of writing them to a bit
  /// stream.
  /// </summary>
  private static List<Token> ParseToTokens(ReadOnlySpan<byte> data, int chainDepth, int minMatch) {
    var tokens = new List<Token>(capacity: data.Length / 2);

    const int HashSize = 1 << 14;
    var hashHead = new int[HashSize];
    var hashNext = new int[data.Length];
    Array.Fill(hashHead, -1);
    Array.Fill(hashNext, -1);

    var pos = 0;
    while (pos < data.Length) {
      InsertHash(data, pos, hashHead, hashNext);
      var (curLen, curOff) = FindBestMatch(data, pos, hashHead, hashNext, minMatch, chainDepth);

      if (curLen < minMatch) {
        tokens.Add(MakeLiteralToken(data[pos]));
        ++pos;
        continue;
      }

      var nextPos = pos + 1;
      var preferLiteral = false;
      if (nextPos < data.Length) {
        InsertHash(data, nextPos, hashHead, hashNext);
        var (nextLen, _) = FindBestMatch(data, nextPos, hashHead, hashNext, minMatch, chainDepth);
        if (nextLen > curLen)
          preferLiteral = true;
      }

      if (preferLiteral) {
        tokens.Add(MakeLiteralToken(data[pos]));
        ++pos;
        continue;
      }

      tokens.Add(MakeMatchToken(curLen, curOff));
      for (var j = 2; j < curLen; ++j) {
        var p = pos + j;
        if (p + 2 < data.Length)
          InsertHash(data, p, hashHead, hashNext);
      }
      pos += curLen;
    }

    return tokens;
  }

  private static Token MakeLiteralToken(byte value) => new(value, 0, 0, -1, 0, 0);

  private static Token MakeMatchToken(int length, int distance) {
    var (lenSym, lenExtraBits, lenExtraVal) = MsLzhConstants.EncodeLength(length);
    var (distSym, distExtraBits, distExtraVal) = MsLzhConstants.EncodeDistance(distance);
    return new Token(lenSym, lenExtraBits, lenExtraVal, distSym, distExtraBits, distExtraVal);
  }

  // =========================================================================
  //                              Helpers
  // =========================================================================

  private static int Hash3(ReadOnlySpan<byte> data, int pos)
    => ((data[pos] << 10) ^ (data[pos + 1] << 5) ^ data[pos + 2]) & 0x1FFF;

  private static void InsertHash(ReadOnlySpan<byte> data, int pos, int[] hashHead, int[] hashNext) {
    if (pos + 2 >= data.Length) return;
    var h = Hash3(data, pos);
    hashNext[pos] = hashHead[h];
    hashHead[h] = pos;
  }

  private static (int Length, int Offset) FindBestMatch(
      ReadOnlySpan<byte> data, int pos, int[] hashHead, int[] hashNext,
      int minMatch, int maxChainLen) {
    if (pos + minMatch > data.Length)
      return (0, 0);

    var bestLen = 0;
    var bestOff = 0;
    var minPos = Math.Max(0, pos - MsLzhConstants.WindowSize);
    var idx = hashNext[pos];
    var chainLen = 0;

    var maxLen = Math.Min(data.Length - pos, MsLzhConstants.MaxMatch);

    while (idx >= minPos && idx < pos && chainLen < maxChainLen) {
      if (data[idx] == data[pos]
          && data[idx + 1] == data[pos + 1]
          && data[idx + 2] == data[pos + 2]) {
        var len = 3;
        while (len < maxLen && data[idx + len] == data[pos + len])
          len++;
        if (len > bestLen && len >= minMatch) {
          bestLen = len;
          bestOff = pos - idx;
          if (bestLen >= maxLen) break;
        }
      }
      idx = hashNext[idx];
      chainLen++;
    }
    return (bestLen, bestOff);
  }

  private static void WriteLiteralFixed(BitWriter<MsbBitOrder> writer, byte value) {
    var (code, len) = MsLzhFixedTables.LitLen.GetCode(value);
    writer.WriteBits(code, len);
  }

  private static void WriteMatchFixed(BitWriter<MsbBitOrder> writer, int length, int distance) {
    var (lenSym, lenExtraBits, lenExtraVal) = MsLzhConstants.EncodeLength(length);
    var (lenCode, lenCodeLen) = MsLzhFixedTables.LitLen.GetCode(lenSym);
    writer.WriteBits(lenCode, lenCodeLen);
    if (lenExtraBits > 0)
      writer.WriteBits((uint)lenExtraVal, lenExtraBits);

    var (distSym, distExtraBits, distExtraVal) = MsLzhConstants.EncodeDistance(distance);
    var (distCode, distCodeLen) = MsLzhFixedTables.Distance.GetCode(distSym);
    writer.WriteBits(distCode, distCodeLen);
    if (distExtraBits > 0)
      writer.WriteBits((uint)distExtraVal, distExtraBits);
  }

  private static void WriteMatchFixedFromToken(BitWriter<MsbBitOrder> writer, Token t) {
    var (lenCode, lenCodeLen) = MsLzhFixedTables.LitLen.GetCode(t.LitLenSym);
    writer.WriteBits(lenCode, lenCodeLen);
    if (t.LenExtraBits > 0)
      writer.WriteBits((uint)t.LenExtraValue, t.LenExtraBits);
    var (distCode, distCodeLen) = MsLzhFixedTables.Distance.GetCode(t.DistSym);
    writer.WriteBits(distCode, distCodeLen);
    if (t.DistExtraBits > 0)
      writer.WriteBits((uint)t.DistExtraValue, t.DistExtraBits);
  }
}
