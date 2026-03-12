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
/// The Huffman-coded bitstream follows immediately, using 16-bit LE words, LSB first.
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
///     If <c>length_header == 15</c>: read extra byte <c>E</c>;
///     if <c>E != 255</c>: <c>length = E + 3</c>;
///     else: read 16-bit LE length.
///   </description></item>
/// </list>
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
    var reader = new SpanBitReader(input);
    var outputPos = 0;

    while (outputPos < uncompressedSize) {
      // Reset bit buffer between chunks — each chunk starts byte-aligned
      reader.BitBuf = 0;
      reader.BitsAvailable = 0;

      // Read the 256-byte table header
      if (reader.InputPos + XpressConstants.HuffTableHeaderBytes > input.Length)
        ThrowTruncated();

      var codeLengths = new int[XpressConstants.HuffSymbolCount];
      for (var i = 0; i < XpressConstants.HuffTableHeaderBytes; ++i) {
        codeLengths[i * 2]     =  input[reader.InputPos + i] & 0xF;
        codeLengths[i * 2 + 1] = (input[reader.InputPos + i] >> 4) & 0xF;
      }
      reader.AdvanceBytes(XpressConstants.HuffTableHeaderBytes);

      // Build canonical decode table (LSB-first)
      var decodeTable = BuildDecodeTable(codeLengths, out var maxCodeLength);

      var chunkUncompressedSize = Math.Min(XpressConstants.HuffChunkSize, uncompressedSize - outputPos);
      var chunkEnd = outputPos + chunkUncompressedSize;

      while (outputPos < chunkEnd) {
        var sym = DecodeSymbol(input, ref reader, decodeTable, maxCodeLength);

        if (sym < 256)
          output[outputPos++] = (byte)sym;
        else {
          var offsetLog2   = (sym - 256) >> 4;
          var lengthHeader = (sym - 256) & 0xF;

          // Decode distance
          int distance;
          if (offsetLog2 == 0)
            distance = 1;
          else
            distance = (1 << offsetLog2) + (int)ReadBits(input, ref reader, offsetLog2);

          // Decode length
          int length;
          if (lengthHeader < 15)
            length = lengthHeader + XpressConstants.MinMatch;
          else {
            var extra = (int)ReadBits(input, ref reader, 8);
            if (extra != XpressConstants.LengthSentinel8)
              length = extra + XpressConstants.MinMatch;
            else
              length = (int)ReadBits(input, ref reader, 16);
          }

          var copyFrom = outputPos - distance;
          if (copyFrom < 0)
            ThrowInvalidMatch();

          var copyEnd = Math.Min(outputPos + length, chunkEnd);
          while (outputPos < copyEnd)
            output[outputPos++] = output[copyFrom++];
        }
      }
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

  private static uint ReadBits(ReadOnlySpan<byte> input, ref SpanBitReader reader, int count) {
    while (reader.BitsAvailable < count)
      if (reader.InputPos + 1 < input.Length) {
        var word = BinaryPrimitives.ReadUInt16LittleEndian(input[reader.InputPos..]);
        reader.InputPos += 2;
        reader.BitBuf |= (uint)word << reader.BitsAvailable;
        reader.BitsAvailable += 16;
      } else if (reader.InputPos < input.Length) {
        reader.BitBuf |= (uint)input[reader.InputPos++] << reader.BitsAvailable;
        reader.BitsAvailable += 8;
      } else
        break; // truncated; caller will catch downstream

    var result = reader.BitBuf & ((1u << count) - 1u);
    reader.BitBuf >>= count;
    reader.BitsAvailable -= count;
    return result;
  }

  private static int DecodeSymbol(ReadOnlySpan<byte> input, ref SpanBitReader reader, int[] decodeTable, int maxCodeLength) {
    // Ensure we have enough bits
    while (reader.BitsAvailable < maxCodeLength && reader.InputPos + 1 < input.Length) {
      var word = BinaryPrimitives.ReadUInt16LittleEndian(input[reader.InputPos..]);
      reader.InputPos += 2;
      reader.BitBuf |= (uint)word << reader.BitsAvailable;
      reader.BitsAvailable += 16;
    }

    var peek = (int)(reader.BitBuf & ((1u << maxCodeLength) - 1u));
    var entry = decodeTable[peek];
    if (entry < 0)
      ThrowInvalidHuffmanCode();

    var codeLen = entry >> 16;
    reader.BitBuf >>= codeLen;
    reader.BitsAvailable -= codeLen;
    return entry & 0xFFFF;
  }

  // ---- Decode table -------------------------------------------------------

  // Builds a flat decode table for LSB-first canonical Huffman codes.
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

      var symCode = nextCode[len]++;

      // Bit-reverse the canonical code for LSB-first table lookup
      var reversed = ReverseBits(symCode, len);
      var fillCount = 1 << (maxLen - len);
      var packed = sym | (len << 16);
      for (var fill = 0; fill < fillCount; ++fill)
        table[(int)reversed + (fill << len)] = packed;
    }

    return table;
  }

  private static uint ReverseBits(uint code, int length) {
    uint result = 0;
    for (var i = 0; i < length; ++i) {
      result = (result << 1) | (code & 1);
      code >>= 1;
    }
    return result;
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
