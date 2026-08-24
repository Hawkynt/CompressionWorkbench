using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Decompresses data compressed with the XPRESS Huffman variant.
/// </summary>
/// <remarks>
/// <para>
/// Each compressed chunk begins with a 256-byte table header containing 512 4-bit
/// Huffman code lengths (two nibbles per byte, low nibble = lower-indexed symbol).
/// The Huffman-coded bit stream follows immediately, in 16-bit little-endian
/// words read most significant bit first.
/// </para>
/// <para>
/// Symbol alphabet (512 symbols):
/// <list type="bullet">
///   <item><description>0–255: literal byte.</description></item>
///   <item><description>
///     256–511: LZ match.<br/>
///     <c>offset_log2 = (symbol - 256) >> 4</c><br/>
///     <c>length_header = (symbol - 256) &amp; 0xF</c><br/>
///     <c>distance = (1 &lt;&lt; offset_log2) + ReadBits(offset_log2)</c><br/>
///     Length: if <c>length_header &lt; 15</c>: <c>length = length_header + 3</c>.<br/>
///     If <c>length_header == 15</c>: take a raw byte <c>E</c> from between the
///     words; <c>length = 15 + E + 3</c>, unless <c>E</c> is 255, when a raw
///     16-bit value follows giving <c>length - 3</c> outright.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Format per [MS-XCA] section 2.2, "LZ77+Huffman Compression Algorithm".
/// </para>
/// </remarks>
public static partial class XpressHuffmanDecompressor {
  /// <summary>
  /// Decompresses XPRESS Huffman-encoded data.
  /// </summary>
  /// <param name="input">The compressed input data.</param>
  /// <param name="uncompressedSize">Expected total uncompressed output size in bytes.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">The compressed data is malformed.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> input, int uncompressedSize) {
    ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);

    if (uncompressedSize == 0)
      return [];

    var output = new byte[uncompressedSize];
    var outputPos = 0;
    var inputPos = 0;

    while (outputPos < uncompressedSize) {
      // Read the 256-byte table header.
      if (inputPos + XpressConstants.HuffTableHeaderBytes > input.Length)
        ThrowTruncated();

      var codeLengths = new int[XpressConstants.HuffSymbolCount];
      for (var i = 0; i < XpressConstants.HuffTableHeaderBytes; ++i) {
        codeLengths[i * 2]     =  input[inputPos + i] & 0xF;
        codeLengths[i * 2 + 1] = (input[inputPos + i] >> 4) & 0xF;
      }

      var decodeTable = BuildDecodeTable(codeLengths, out var maxCodeLength);

      var bitsAt = inputPos + XpressConstants.HuffTableHeaderBytes;
      var reader = new SpanBitReader(input, bitsAt);

      var chunkUncompressedSize = Math.Min(XpressConstants.HuffChunkSize, uncompressedSize - outputPos);
      var chunkEnd = outputPos + chunkUncompressedSize;

      while (outputPos < chunkEnd) {
        var sym = DecodeSymbol(ref reader, decodeTable, maxCodeLength);

        if (sym < 256) {
          output[outputPos++] = (byte)sym;
          continue;
        }

        var offsetLog2   = (sym - 256) >> 4;
        var lengthHeader = (sym - 256) & 0xF;

        // Length first: its raw bytes come from between the words, and reading
        // them in the order they were written is what keeps the two in step.
        int length;
        if (lengthHeader < 15)
          length = lengthHeader + XpressConstants.MinMatch;
        else {
          var beyondHeader = reader.ReadRawByte();
          length = beyondHeader == XpressConstants.LengthSentinel8
            ? reader.ReadRawUInt16() + XpressConstants.MinMatch
            : beyondHeader + 15 + XpressConstants.MinMatch;
        }

        var distance = (1 << offsetLog2) + (int)reader.ReadBits(offsetLog2);

        var copyFrom = outputPos - distance;
        if (copyFrom < 0)
          ThrowInvalidMatch();

        var copyEnd = Math.Min(outputPos + length, chunkEnd);
        while (outputPos < copyEnd)
          output[outputPos++] = output[copyFrom++];
      }

      if (outputPos >= uncompressedSize)
        break;

      // Where the next chunk starts is not written down anywhere: a chunk is
      // delimited by the size of what it decodes to, which a container carries
      // and this stream does not. It can still be worked out, because the writer
      // hands out its words and its raw bytes in a fixed order and the reader
      // has just taken the same ones — but only if it has taken all of them, and
      // one symbol is left after the output is full. That is the terminator
      // every chunk written here ends with.
      DecodeSymbol(ref reader, decodeTable, maxCodeLength);
      inputPos = bitsAt + reader.ChunkLength;
    }

    return output;
  }

  /// <summary>
  /// Decompresses XPRESS Huffman-encoded data from a stream.
  /// </summary>
  /// <param name="input">The stream containing compressed data.</param>
  /// <param name="uncompressedSize">Expected total uncompressed output size in bytes.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">The compressed data is malformed.</exception>
  public static byte[] Decompress(Stream input, int uncompressedSize) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);

    if (uncompressedSize == 0)
      return [];

    var compressed = new byte[input.Length - input.Position];
    input.ReadExactly(compressed);
    return Decompress(compressed.AsSpan(), uncompressedSize);
  }

  // ---- Bit reading --------------------------------------------------------

  private static int DecodeSymbol(ref SpanBitReader reader, int[] decodeTable, int maxCodeLength) {
    var entry = decodeTable[(int)reader.Peek(maxCodeLength)];
    if (entry < 0)
      ThrowInvalidHuffmanCode();

    reader.Remove(entry >> 16);
    return entry & 0xFFFF;
  }

  // ---- Decode table -------------------------------------------------------

  // Builds a flat decode table for canonical Huffman codes read most
  // significant bit first.
  // Entry format: (codeLength << 16) | symbol, or -1 for unused.
  private static int[] BuildDecodeTable(int[] codeLengths, out int maxLen) {
    maxLen = 0;
    for (var i = 0; i < codeLengths.Length; ++i)
      if (codeLengths[i] > maxLen) maxLen = codeLengths[i];

    if (maxLen == 0) {
      maxLen = 1;
      return new int[2];
    }

    var tableSize = 1 << maxLen;
    var table = new int[tableSize];
    table.AsSpan().Fill(-1);

    // Canonical code assignment (MSB-first)
    var blCount = new int[maxLen + 1];
    foreach (var value in codeLengths)
      if (value > 0) 
        ++blCount[value];

    var nextCode = new uint[maxLen + 1];
    var code = 0u;
    for (var b = 1; b <= maxLen; ++b) {
      code = (code + (uint)blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    for (var sym = 0; sym < codeLengths.Length; ++sym) {
      var len = codeLengths[sym];
      if (len == 0)
        continue;

      // The code occupies the top of the index; every value of the bits below
      // it decodes to the same symbol.
      var start = (int)(nextCode[len]++ << (maxLen - len));
      var fillCount = 1 << (maxLen - len);
      var packed = sym | (len << 16);
      for (var fill = 0; fill < fillCount; ++fill)
        table[start + fill] = packed;
    }

    return table;
  }

  // ---- Helpers ------------------------------------------------------------

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowTruncated() =>
    throw new InvalidDataException("XPRESS Huffman compressed data is truncated.");

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidHuffmanCode() =>
    throw new InvalidDataException("XPRESS Huffman compressed data contains an invalid Huffman code.");

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidMatch() =>
    throw new InvalidDataException("XPRESS Huffman compressed data contains an invalid match descriptor.");

  // ---- Inner type ---------------------------------------------------------

}
