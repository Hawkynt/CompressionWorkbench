using Compression.Core.BitIO;
using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Dictionary.MsLzh;

/// <summary>
/// Per-block dynamic Huffman header layout for MS LZH — adapted from the
/// RFC 1951 §3.2.7 DEFLATE dynamic-block scheme. The MS LZH bit stream now
/// emits a single leading block-type bit (0 = fixed tables, 1 = dynamic
/// tables) and, for dynamic blocks, the embedded code-length-code-length
/// table followed by the literal/length and distance code-length lists.
/// <para>
/// <b>Dynamic block header bit layout</b> (after the block-type bit = 1):
/// </para>
/// <list type="bullet">
///   <item><c>5 bits HLIT</c>  — number of literal/length codes used minus
///     257 (range 257..286 → 0..29).</item>
///   <item><c>5 bits HDIST</c> — number of distance codes used minus 1
///     (range 1..30 → 0..29).</item>
///   <item><c>4 bits HCLEN</c> — number of code-length-code lengths minus 4
///     (range 4..19 → 0..15).</item>
///   <item><c>(HCLEN+4) × 3 bits</c> — code lengths for the 19-symbol
///     code-length alphabet, in the RFC 1951 permutation order
///     <see cref="CodeLengthOrder"/>.</item>
///   <item>HLIT+257 literal/length code lengths, encoded with the
///     code-length code (symbols 0..15 = literal length, 16 = repeat last
///     3-6 times, 17 = run-of-zero 3-10, 18 = run-of-zero 11-138).</item>
///   <item>HDIST+1 distance code lengths, same code-length-code encoding.</item>
/// </list>
/// <para>
/// <b>Compatibility note.</b> The dynamic block layout matches RFC 1951
/// semantically but is NOT bit-compatible with a Microsoft-produced
/// DriveSpace 3 image — the MS LZH per-block framing (block-count bytes,
/// run boundaries, dictionary-init values) is not yet reverse-engineered
/// from a reference image. Self round-trip is the gating requirement.
/// </para>
/// </summary>
internal static class MsLzhDynamicHuffman {

  /// <summary>Block-type bit emitted before every block. 0 = fixed, 1 = dynamic.</summary>
  internal const int BlockTypeFixed = 0;
  /// <summary>Block-type bit emitted before every block. 1 = dynamic Huffman tables follow.</summary>
  internal const int BlockTypeDynamic = 1;

  /// <summary>Maximum Huffman code length for the literal/length and distance alphabets.</summary>
  internal const int MaxLitLenCodeLength = 15;

  /// <summary>Number of symbols in the code-length alphabet (RFC 1951 §3.2.7).</summary>
  internal const int CodeLengthAlphabetSize = 19;

  /// <summary>Maximum code length for the code-length alphabet (3 bits per length).</summary>
  internal const int MaxCodeLengthCodeLength = 7;

  /// <summary>
  /// RFC 1951 §3.2.7 permutation order in which the code-length-code lengths
  /// are written. Symbols expected to be unused are placed at the end so
  /// HCLEN can truncate them off cheaply.
  /// </summary>
  internal static readonly int[] CodeLengthOrder = [
    16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15,
  ];

  // =========================================================================
  //                          Encoder side
  // =========================================================================

  /// <summary>
  /// Builds canonical Huffman code lengths from symbol frequencies, capped at
  /// <see cref="MaxLitLenCodeLength"/> bits per RFC 1951.
  /// </summary>
  internal static int[] BuildCodeLengths(long[] frequencies, int alphabetSize) {
    var totalFreq = 0L;
    var firstSymbol = -1;
    var secondSymbol = -1;
    for (var i = 0; i < frequencies.Length; ++i) {
      if (frequencies[i] <= 0) continue;
      totalFreq += frequencies[i];
      if (firstSymbol < 0) firstSymbol = i;
      else if (secondSymbol < 0) secondSymbol = i;
    }
    if (totalFreq == 0)
      return new int[alphabetSize];

    var tree = HuffmanTree.BuildFromFrequencies(frequencies);
    var lengths = HuffmanTree.GetCodeLengths(tree, alphabetSize);
    HuffmanTree.LimitCodeLengths(lengths, MaxLitLenCodeLength);

    // RFC 1951 requires at least two used codes to form a valid prefix code;
    // a single-symbol alphabet must be padded with a dummy second symbol.
    if (firstSymbol >= 0 && secondSymbol < 0) {
      lengths[firstSymbol] = 1;
      var dummy = firstSymbol == 0 ? 1 : 0;
      if (dummy < alphabetSize) lengths[dummy] = 1;
    }
    return lengths;
  }

  /// <summary>
  /// Encodes a code-length list (literal/length + distance concatenated, per
  /// RFC 1951 §3.2.7) into a sequence of code-length-code symbols and their
  /// extra bits. Returns the symbols and a parallel array of (extraBits,
  /// extraValue) tuples — caller writes them through the code-length-code's
  /// canonical Huffman table.
  /// </summary>
  internal static List<(int Symbol, int ExtraBits, int ExtraValue)> EncodeCodeLengths(int[] mergedLengths) {
    var output = new List<(int, int, int)>();
    var i = 0;
    while (i < mergedLengths.Length) {
      var len = mergedLengths[i];
      // Run-of-zero compaction.
      if (len == 0) {
        var runStart = i;
        while (i < mergedLengths.Length && mergedLengths[i] == 0) ++i;
        var runLen = i - runStart;
        while (runLen >= 11) {
          var emit = Math.Min(runLen, 138);
          output.Add((18, 7, emit - 11));
          runLen -= emit;
        }
        if (runLen >= 3) {
          output.Add((17, 3, runLen - 3));
          runLen = 0;
        }
        while (runLen-- > 0)
          output.Add((0, 0, 0));
        continue;
      }
      // Emit the first occurrence, then collapse repeats via symbol 16
      // (repeat previous code length 3..6 times).
      output.Add((len, 0, 0));
      ++i;
      while (i < mergedLengths.Length && mergedLengths[i] == len) {
        var runStart = i;
        while (i < mergedLengths.Length && mergedLengths[i] == len) ++i;
        var repeats = i - runStart;
        while (repeats >= 3) {
          var emit = Math.Min(repeats, 6);
          output.Add((16, 2, emit - 3));
          repeats -= emit;
        }
        while (repeats-- > 0)
          output.Add((len, 0, 0));
      }
    }
    return output;
  }

  /// <summary>
  /// Writes the dynamic-block header (HLIT/HDIST/HCLEN + code-length-code
  /// lengths + encoded lit/len and distance code lengths) to the bit writer.
  /// Returns the constructed <see cref="CanonicalHuffman"/> tables for the
  /// caller to encode the block payload.
  /// </summary>
  internal static (CanonicalHuffman LitLen, CanonicalHuffman Distance) WriteHeader(
      BitWriter<MsbBitOrder> writer,
      int[] litLenLengths,
      int[] distLengths) {
    // Trim trailing zeros: HLIT is the highest used literal/length index + 1,
    // clamped to ≥ 257. HDIST is the highest used distance index + 1.
    var hlit = MsLzhConstants.LitLenAlphabetSize;
    while (hlit > 257 && litLenLengths[hlit - 1] == 0) --hlit;
    var hdist = MsLzhConstants.DistanceAlphabetSize;
    while (hdist > 1 && distLengths[hdist - 1] == 0) --hdist;

    // Merge into a single list for code-length-code encoding.
    var merged = new int[hlit + hdist];
    Array.Copy(litLenLengths, 0, merged, 0, hlit);
    Array.Copy(distLengths, 0, merged, hlit, hdist);
    var encoded = EncodeCodeLengths(merged);

    // Frequency-count the code-length-code alphabet.
    var clFreq = new long[CodeLengthAlphabetSize];
    foreach (var (sym, _, _) in encoded)
      clFreq[sym]++;

    var clLengths = new int[CodeLengthAlphabetSize];
    if (clFreq.Any(t => t > 0)) {
      var tree = HuffmanTree.BuildFromFrequencies(clFreq);
      clLengths = HuffmanTree.GetCodeLengths(tree, CodeLengthAlphabetSize);
      HuffmanTree.LimitCodeLengths(clLengths, MaxCodeLengthCodeLength);
    }

    // HCLEN: trim trailing zeros in the code-length-code permutation order,
    // but keep at least 4 entries (RFC 1951 mandates HCLEN ≥ 0 ↔ 4 entries).
    var hclen = CodeLengthAlphabetSize;
    while (hclen > 4 && clLengths[CodeLengthOrder[hclen - 1]] == 0) --hclen;

    // Header bits.
    writer.WriteBits((uint)(hlit - 257), 5);
    writer.WriteBits((uint)(hdist - 1), 5);
    writer.WriteBits((uint)(hclen - 4), 4);

    // Code-length-code lengths in permutation order, 3 bits each.
    for (var k = 0; k < hclen; ++k)
      writer.WriteBits((uint)clLengths[CodeLengthOrder[k]], 3);

    // Build the code-length-code table and emit the encoded lit/len+dist lengths.
    var clHuf = new CanonicalHuffman(clLengths);
    foreach (var (sym, extraBits, extraValue) in encoded) {
      var (code, codeLen) = clHuf.GetCode(sym);
      writer.WriteBits(code, codeLen);
      if (extraBits > 0)
        writer.WriteBits((uint)extraValue, extraBits);
    }

    // Build the lit/len and distance Huffman tables for the block payload.
    // Pad to the alphabet sizes — unused symbols already have length 0.
    var litLenPadded = new int[MsLzhConstants.LitLenAlphabetSize];
    Array.Copy(litLenLengths, litLenPadded, hlit);
    var distPadded = new int[MsLzhConstants.DistanceAlphabetSize];
    Array.Copy(distLengths, distPadded, hdist);
    return (new CanonicalHuffman(litLenPadded), new CanonicalHuffman(distPadded));
  }

  /// <summary>
  /// Estimates the bit cost of a dynamic block header + payload. Used by the
  /// encoder cost-comparison logic to pick the smaller of static vs dynamic
  /// per block.
  /// </summary>
  internal static long EstimateDynamicBlockBits(
      long[] litLenFreq, int[] litLenLengths,
      long[] distFreq, int[] distLengths) {
    // Header cost: 14 bits (HLIT+HDIST+HCLEN) + 3 bits per code-length-code
    // length emitted + encoded lit/len+dist code-length list + the payload.
    var hlit = MsLzhConstants.LitLenAlphabetSize;
    while (hlit > 257 && litLenLengths[hlit - 1] == 0) --hlit;
    var hdist = MsLzhConstants.DistanceAlphabetSize;
    while (hdist > 1 && distLengths[hdist - 1] == 0) --hdist;

    var merged = new int[hlit + hdist];
    Array.Copy(litLenLengths, 0, merged, 0, hlit);
    Array.Copy(distLengths, 0, merged, hlit, hdist);
    var encoded = EncodeCodeLengths(merged);

    var clFreq = new long[CodeLengthAlphabetSize];
    foreach (var (sym, _, _) in encoded)
      clFreq[sym]++;
    var clLengths = new int[CodeLengthAlphabetSize];
    if (clFreq.Any(t => t > 0)) {
      var tree = HuffmanTree.BuildFromFrequencies(clFreq);
      clLengths = HuffmanTree.GetCodeLengths(tree, CodeLengthAlphabetSize);
      HuffmanTree.LimitCodeLengths(clLengths, MaxCodeLengthCodeLength);
    }
    var hclen = CodeLengthAlphabetSize;
    while (hclen > 4 && clLengths[CodeLengthOrder[hclen - 1]] == 0) --hclen;

    var headerBits = 14L + 3L * hclen;
    foreach (var (sym, extraBits, _) in encoded)
      headerBits += clLengths[sym] + extraBits;

    // Payload bits: per-symbol-frequency × code-length.
    var payloadBits = 0L;
    for (var s = 0; s < litLenFreq.Length; ++s)
      payloadBits += litLenFreq[s] * litLenLengths[s];
    for (var s = 0; s < distFreq.Length; ++s)
      payloadBits += distFreq[s] * distLengths[s];

    return headerBits + payloadBits;
  }

  /// <summary>
  /// Estimates the bit cost of encoding the same payload with the fixed
  /// static Huffman tables. Used by the encoder cost-comparison logic.
  /// </summary>
  internal static long EstimateStaticBlockBits(long[] litLenFreq, long[] distFreq) {
    var bits = 0L;
    for (var s = 0; s < litLenFreq.Length; ++s)
      bits += litLenFreq[s] * MsLzhFixedTables.LitLenLengths[s];
    for (var s = 0; s < distFreq.Length; ++s)
      bits += distFreq[s] * MsLzhFixedTables.DistanceLengths[s];
    return bits;
  }

  // =========================================================================
  //                          Decoder side
  // =========================================================================

  /// <summary>
  /// Reads the dynamic-block header from the bit reader and returns the
  /// resulting literal/length and distance Huffman tables.
  /// </summary>
  internal static (CanonicalHuffman LitLen, CanonicalHuffman Distance) ReadHeader(
      BitReader<MsbBitOrder> reader) {
    var hlit = (int)reader.ReadBits(5) + 257;
    var hdist = (int)reader.ReadBits(5) + 1;
    var hclen = (int)reader.ReadBits(4) + 4;

    if (hlit > MsLzhConstants.LitLenAlphabetSize)
      throw new InvalidDataException($"MS LZH: dynamic block HLIT {hlit} exceeds literal/length alphabet.");
    if (hdist > MsLzhConstants.DistanceAlphabetSize)
      throw new InvalidDataException($"MS LZH: dynamic block HDIST {hdist} exceeds distance alphabet.");
    if (hclen > CodeLengthAlphabetSize)
      throw new InvalidDataException($"MS LZH: dynamic block HCLEN {hclen} exceeds code-length alphabet.");

    var clLengths = new int[CodeLengthAlphabetSize];
    for (var k = 0; k < hclen; ++k)
      clLengths[CodeLengthOrder[k]] = (int)reader.ReadBits(3);

    var clHuf = new CanonicalHuffman(clLengths);
    if (clHuf.MaxCodeLength == 0)
      throw new InvalidDataException("MS LZH: dynamic block code-length-code table is empty.");

    var merged = new int[hlit + hdist];
    var idx = 0;
    while (idx < merged.Length) {
      var sym = clHuf.DecodeSymbol(reader);
      if (sym < 0 || sym >= CodeLengthAlphabetSize)
        throw new InvalidDataException($"MS LZH: dynamic block invalid code-length symbol {sym}.");
      switch (sym) {
        case <= 15:
          merged[idx++] = sym;
          break;
        case 16: {
          // Repeat previous code length 3..6 times (2 extra bits).
          if (idx == 0)
            throw new InvalidDataException("MS LZH: dynamic block code-length symbol 16 at start of list.");
          var repeat = (int)reader.ReadBits(2) + 3;
          var prev = merged[idx - 1];
          while (repeat-- > 0 && idx < merged.Length)
            merged[idx++] = prev;
          break;
        }
        case 17: {
          // Run of zeros, 3..10 (3 extra bits).
          var repeat = (int)reader.ReadBits(3) + 3;
          while (repeat-- > 0 && idx < merged.Length)
            merged[idx++] = 0;
          break;
        }
        case 18: {
          // Run of zeros, 11..138 (7 extra bits).
          var repeat = (int)reader.ReadBits(7) + 11;
          while (repeat-- > 0 && idx < merged.Length)
            merged[idx++] = 0;
          break;
        }
        default:
          throw new InvalidDataException($"MS LZH: dynamic block code-length symbol {sym} unrecognised.");
      }
    }

    var litLenLengths = new int[MsLzhConstants.LitLenAlphabetSize];
    Array.Copy(merged, 0, litLenLengths, 0, hlit);
    var distLengths = new int[MsLzhConstants.DistanceAlphabetSize];
    Array.Copy(merged, hlit, distLengths, 0, hdist);
    return (new CanonicalHuffman(litLenLengths), new CanonicalHuffman(distLengths));
  }
}
