using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;
using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Compresses data using the XPRESS Huffman variant.
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
///     <c>256 + (offset_log2 &lt;&lt; 4) + min(length - 3, 15)</c>.
///     If the length header is 15, an extra byte follows (raw, not Huffman-coded),
///     giving <c>length = extra + 3</c> unless extra == 255, in which case a
///     16-bit LE length follows the extra byte.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Bits are written LSB-first, packed into 16-bit LE words.
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
    var matchFinder = new HashChainMatchFinder(XpressConstants.WindowSize, this._maxChainDepth);
    var tokens = new List<HuffToken>(chunk.Length);
    var freq = new long[XpressConstants.HuffSymbolCount];
    var pos = 0;

    while (pos < chunk.Length) {
      var match = matchFinder.FindMatch(
        chunk, pos,
        XpressConstants.WindowSize,
        XpressConstants.MaxMatch,
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

    // Build Huffman tree and get code lengths (max 15 bits)
    var codeLengths = BuildLengths(freq);

    // Pass 2: write 256-byte table header (512 nibbles, low nibble = even symbol)
    Span<byte> tableHeader = stackalloc byte[XpressConstants.HuffTableHeaderBytes];
    for (var i = 0; i < XpressConstants.HuffSymbolCount; i += 2)
      tableHeader[i / 2] = (byte)((codeLengths[i] & 0xF) | ((codeLengths[i + 1] & 0xF) << 4));

    output.Write(tableHeader);

    // Build canonical codes
    var codes = BuildCanonicalCodes(codeLengths);

    // Pass 3: emit Huffman-coded bitstream (LSB-first, 16-bit words)
    using var bitStream = new MemoryStream(chunk.Length);
    var bitBuf = 0u;
    var bitsInBuf = 0;

    void FlushWord() {
      // Write current word little-endian
      bitStream.WriteByte((byte)bitBuf);
      bitStream.WriteByte((byte)(bitBuf >> 8));
      bitBuf = 0;
      bitsInBuf = 0;
    }

    void WriteBits(uint value, int count) {
      // LSB first: pack bits from LSB of value into the buffer from low bits
      var remaining = count;
      while (remaining > 0) {
        var space = 16 - bitsInBuf;
        var take = Math.Min(space, remaining);
        var mask = (1u << take) - 1u;
        bitBuf |= (value & mask) << bitsInBuf;
        value >>= take;
        remaining -= take;
        bitsInBuf += take;
        if (bitsInBuf == 16)
          FlushWord();
      }
    }

    foreach (var (symbol, distance, length) in tokens) {
      WriteBits(codes[symbol], codeLengths[symbol]);

      if (symbol < 256)
        continue;

      // Write raw offset bits (offset_log2 bits, LSB of (distance - base))
      var offsetLog2 = (symbol - 256) >> 4;
      if (offsetLog2 > 0) {
        var baseOffset = 1u << offsetLog2;
        var extraOffset = (uint)(distance - (int)baseOffset);
        WriteBits(extraOffset, offsetLog2);
      }

      // Extra length bytes (raw, not Huffman)
      var lengthHeader = (symbol - 256) & 0xF;
      if (lengthHeader != 15)
        continue;

      var adj = length - XpressConstants.MinMatch;  // 0-based
      if (adj < XpressConstants.LengthSentinel8)
        WriteBits((uint)adj, 8);
      else {
        WriteBits(XpressConstants.LengthSentinel8, 8);
        WriteBits((uint)length, 16);
      }
    }

    // Flush remaining bits (pad to full 16-bit word boundary)
    if (bitsInBuf > 0)
      FlushWord();

    output.Write(bitStream.GetBuffer(), 0, (int)bitStream.Length);
  }

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
    var maxLen = lengths.Prepend(0).Max();
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
        codes[i] = ReverseBits(nextCode[lengths[i]]++, lengths[i]);

    return codes;
  }

  /// <summary>Returns floor(log2(x)) for x >= 1.</summary>
  internal static int Log2Floor(int x) {
    var result = 0;
    while (x > 1) { x >>= 1; ++result; }
    return result;
  }

  private static uint ReverseBits(uint code, int length) {
    uint result = 0;
    for (var i = 0; i < length; i++) {
      result = (result << 1) | (code & 1);
      code >>= 1;
    }
    return result;
  }

}
