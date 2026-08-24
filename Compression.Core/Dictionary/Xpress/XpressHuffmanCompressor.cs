using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;
using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Compresses data using the XPRESS Huffman variant, as used by WIM images and
/// by Windows wherever "Xpress Huffman" is named.
/// </summary>
/// <remarks>
/// <para>
/// Input is split into 65 536-byte (64 KiB) chunks. Each chunk is compressed
/// independently and prefixed with a 256-byte Huffman table header containing
/// 512 4-bit code lengths packed as nibbles (low nibble first).
/// </para>
/// <para>
/// The 512-symbol alphabet:
/// <list type="bullet">
///   <item><description>Symbols 0–255: literal bytes.</description></item>
///   <item><description>
///     Symbols 256–511: LZ matches encoded as
///     <c>256 + (offset_log2 &lt;&lt; 4) + min(length - 3, 15)</c>, followed by
///     <c>offset_log2</c> raw bits giving the offset below its power of two.
///     A length header of 15 means the length did not fit, and a raw byte
///     carrying <c>min(length - 3 - 15, 255)</c> follows; a byte of 255 in turn
///     means a raw 16-bit <c>length - 3</c> follows it.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Bits are written most significant first and packed into 16-bit
/// little-endian words; the raw bytes above are not part of that bit stream but
/// sit between the words, which is what
/// <see cref="XpressHuffmanOutputBitstream" /> exists to arrange.
/// </para>
/// <para>
/// Format per [MS-XCA] section 2.2, "LZ77+Huffman Compression Algorithm".
/// </para>
/// </remarks>
public sealed partial class XpressHuffmanCompressor {
  private readonly int _maxChainDepth;

  /// <summary>
  /// Initializes a new <see cref="XpressHuffmanCompressor"/>.
  /// </summary>
  /// <param name="maxChainDepth">Hash-chain search depth.</param>
  public XpressHuffmanCompressor(int maxChainDepth = 128) {
    ArgumentOutOfRangeException.ThrowIfLessThan(maxChainDepth, 1);
    this._maxChainDepth = maxChainDepth;
  }

  /// <summary>
  /// Compresses <paramref name="input"/> and returns the compressed bytes.
  /// </summary>
  /// <param name="input">The data to compress.</param>
  /// <returns>The XPRESS Huffman-compressed data.</returns>
  public byte[] Compress(ReadOnlySpan<byte> input) {
    if (input.IsEmpty)
      return [];

    using var output = new MemoryStream(input.Length);
    this.Compress(input, output);
    return output.ToArray();
  }

  /// <summary>
  /// Compresses <paramref name="input"/> and writes the result to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The data to compress.</param>
  /// <param name="output">The stream to write the compressed data to.</param>
  public void Compress(ReadOnlySpan<byte> input, Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    var pos = 0;
    while (pos < input.Length) {
      var chunkSize = Math.Min(XpressConstants.HuffChunkSize, input.Length - pos);
      var chunk = input.Slice(pos, chunkSize);
      this.CompressChunk(chunk, output);
      pos += chunkSize;
    }
  }

  private void CompressChunk(ReadOnlySpan<byte> chunk, Stream output) {
    // Pass 1: tokenize and gather symbol frequencies
    var matchFinder = new HashChainMatchFinder(XpressConstants.HuffWindowSize, this._maxChainDepth);
    var tokens = new List<HuffToken>(chunk.Length);
    var freq = new long[XpressConstants.HuffSymbolCount];
    var pos = 0;

    while (pos < chunk.Length) {
      var match = matchFinder.FindMatch(
        chunk, pos,
        XpressConstants.HuffWindowSize,
        XpressConstants.HuffMaxMatch,
        XpressConstants.MinMatch);

      if (match.Length >= XpressConstants.MinMatch) {
        var offsetLog2 = Log2Floor(match.Distance);
        var lengthHeader = Math.Min(match.Length - XpressConstants.MinMatch, 15);
        var symbol = 256 + (offsetLog2 << 4) + lengthHeader;

        tokens.Add(new(symbol, match.Distance, match.Length));
        ++freq[symbol];

        for (var i = 1; i < match.Length; ++i)
          matchFinder.InsertPosition(chunk, pos + i);

        pos += match.Length;
      } else {
        tokens.Add(new(chunk[pos], 0, 0));
        ++freq[chunk[pos]];
        ++pos;
      }
    }

    // The shortest match symbol always gets a code, because the chunk ends with
    // one — see the terminator written after the tokens below.
    ++freq[MatchTerminatorSymbol];

    // Build Huffman tree and get code lengths (max 15 bits)
    var codeLengths = BuildLengths(freq);

    // Pass 2: write 256-byte table header (512 nibbles, low nibble = even symbol)
    Span<byte> tableHeader = stackalloc byte[XpressConstants.HuffTableHeaderBytes];
    for (var i = 0; i < XpressConstants.HuffSymbolCount; i += 2)
      tableHeader[i / 2] = (byte)((codeLengths[i] & 0xF) | ((codeLengths[i + 1] & 0xF) << 4));

    output.Write(tableHeader);

    // Build canonical codes
    var codes = BuildCanonicalCodes(codeLengths);

    // Pass 3: emit the Huffman-coded bit stream with its raw bytes between the
    // words. Each match is written symbol first, then the length bytes it needs,
    // then the offset bits — the order a decoder unpicks them in, and the order
    // that decides where in the run of bytes each one lands.
    var bits = new XpressHuffmanOutputBitstream(chunk.Length);

    foreach (var (symbol, distance, length) in tokens) {
      bits.WriteBits(codes[symbol], codeLengths[symbol]);

      if (symbol < 256)
        continue;

      var lengthHeader = (symbol - 256) & 0xF;
      if (lengthHeader == 15) {
        // The byte carries what is left of the length once the fifteen in the
        // symbol and the three every match has are taken off it. A byte of 255
        // means even that did not fit, and the sixteen bits after it carry the
        // length less the three, which is as much of it as they can hold.
        var beyondHeader = length - XpressConstants.MinMatch - 15;
        bits.WriteByte((byte)Math.Min(beyondHeader, XpressConstants.LengthSentinel8));
        if (beyondHeader >= XpressConstants.LengthSentinel8)
          bits.WriteUInt16((ushort)(length - XpressConstants.MinMatch));
      }

      // The offset's own power of two is already in the symbol; what is left is
      // how far above it the offset sits.
      var offsetLog2 = (symbol - 256) >> 4;
      if (offsetLog2 > 0)
        bits.WriteBits((uint)(distance - (1 << offsetLog2)), offsetLog2);
    }

    // One more symbol than the chunk needs, and a particular one.
    //
    // A decoder that stops the moment the output is full never reads this. Not
    // every decoder does: one in wide use takes a symbol first and asks
    // afterwards, and then a literal writes past the end of the chunk and a
    // match with a long-length header reaches for bytes that are not there —
    // either of which it reports as damaged data. The shortest match asks for
    // nothing further and is simply clipped, so ending on it costs a few bits
    // and leaves nothing for such a reader to trip over. Streams from the
    // reference implementation end the same way.
    bits.WriteBits(codes[MatchTerminatorSymbol], codeLengths[MatchTerminatorSymbol]);

    var payload = bits.Finish();
    output.Write(payload, 0, payload.Length);
  }

  /// <summary>
  /// The match symbol every chunk ends on: the one meaning three bytes back one
  /// byte, which needs no bits or bytes after it.
  /// </summary>
  private const int MatchTerminatorSymbol = 256;

  // ---- Helpers ----

  private static int[] BuildLengths(long[] freq) {
    // Ensure every symbol has at least frequency 1 so the tree covers all 512 symbols
    // (not required, but simplifies encoding: unused symbols get length 0 anyway)
    // Build using HuffmanTree + LimitCodeLengths
    var hasAny = freq.Any(value => value > 0);
    if (!hasAny) {
      // Edge case: produce a flat 9-bit tree for all symbols
      var flat = new int[XpressConstants.HuffSymbolCount];
      flat.AsSpan().Fill(9);
      return flat;
    }

    // Guarantee at least two distinct symbols so BuildFromFrequencies doesn't throw
    // (if there's only one, add a dummy at position 0 or 1)
    var usedCount = freq.Count(t => t > 0);
    if (usedCount < 2)
      // Find a zero slot and add a pseudo-frequency of 1
      for (var i = 0; i < freq.Length; ++i)
        if (freq[i] == 0) {
          freq[i] = 1;
          break;
        }

    var root = HuffmanTree.BuildFromFrequencies(freq);
    var lengths = HuffmanTree.GetCodeLengths(root, XpressConstants.HuffSymbolCount);
    HuffmanTree.LimitCodeLengths(lengths, XpressConstants.HuffMaxCodeLength);
    return lengths;
  }

  private static uint[] BuildCanonicalCodes(int[] lengths) {
    var maxLen = lengths.Length > 0 ? lengths.Max() : 0;
    if (maxLen == 0)
      return new uint[lengths.Length];

    var blCount = new int[maxLen + 1];
    foreach (var value in lengths)
      if (value > 0) 
        ++blCount[value];

    var nextCode = new uint[maxLen + 1];
    var code = 0u;
    for (var b = 1; b <= maxLen; ++b) {
      code = (code + (uint)blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    var codes = new uint[lengths.Length];
    for (var i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0)
        codes[i] = nextCode[lengths[i]]++;

    return codes;
  }

  /// <summary>Returns floor(log2(x)) for x >= 1.</summary>
  internal static int Log2Floor(int x) {
    var result = 0;
    while (x > 1) { x >>= 1; ++result; }
    return result;
  }

}
