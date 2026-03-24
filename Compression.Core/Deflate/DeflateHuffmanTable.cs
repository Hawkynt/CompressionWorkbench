using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Compression.Core.BitIO;

namespace Compression.Core.Deflate;

/// <summary>
/// Huffman table for Deflate using bit-reversed (LSB-first) canonical codes.
/// Supports single-level lookup-table decoding and encoding.
/// </summary>
public sealed class DeflateHuffmanTable {
  private readonly ushort[] _symbolTable;     // lookup: reversed-code → symbol
  private readonly byte[] _lengthTable;       // lookup: reversed-code → code length
  private readonly uint[] _encodeCodes;       // symbol → reversed code
  private readonly byte[] _encodeLengths;     // symbol → code length

  /// <summary>
  /// Gets the maximum code length in bits.
  /// </summary>
  public int MaxCodeLength { get; }

  /// <summary>
  /// Builds a Deflate Huffman table from code lengths.
  /// </summary>
  /// <param name="codeLengths">Array where index = symbol, value = code length (0 = unused).</param>
  public DeflateHuffmanTable(int[] codeLengths) {
    var maxLen = codeLengths.Length > 0 ? codeLengths.Max() : 0;
    this.MaxCodeLength = maxLen;

    // Assign canonical codes (MSB-first), then bit-reverse for Deflate
    Span<uint> nextCode = stackalloc uint[maxLen + 1];
    Entropy.Huffman.CanonicalCodeAssigner.ComputeNextCodes(codeLengths, maxLen, nextCode);

    // Step 3: Assign codes and build encode/decode tables
    this._encodeCodes = new uint[codeLengths.Length];
    this._encodeLengths = new byte[codeLengths.Length];

    var tableSize = maxLen > 0 ? 1 << maxLen : 0;
    this._symbolTable = new ushort[tableSize];
    this._lengthTable = new byte[tableSize];

    for (var symbol = 0; symbol < codeLengths.Length; ++symbol) {
      var len = codeLengths[symbol];
      if (len == 0)
        continue;

      var canonical = nextCode[len]++;
      var reversed = ReverseBits(canonical, len);

      this._encodeCodes[symbol] = reversed;
      this._encodeLengths[symbol] = (byte)len;

      // Fill all table entries that share this prefix
      // The reversed code occupies the low 'len' bits; we vary the upper bits
      var fill = 1 << len;
      for (var entry = (int)reversed; entry < tableSize; entry += fill) {
        this._symbolTable[entry] = (ushort)symbol;
        this._lengthTable[entry] = (byte)len;
      }
    }
  }

  /// <summary>
  /// Creates the static literal/length Huffman table for Deflate (BTYPE=1).
  /// </summary>
  public static DeflateHuffmanTable CreateStaticLiteralTable() =>
    new(DeflateConstants.GetStaticLiteralLengths());

  /// <summary>
  /// Creates the static distance Huffman table for Deflate (BTYPE=1).
  /// </summary>
  public static DeflateHuffmanTable CreateStaticDistanceTable() =>
    new(DeflateConstants.GetStaticDistanceLengths());

  /// <summary>
  /// Decodes a symbol from the bit buffer using lookup-table decoding.
  /// The bit buffer must be in LSB-first mode.
  /// </summary>
  /// <param name="bitBuffer">The bit buffer to read from.</param>
  /// <returns>The decoded symbol.</returns>
  public int DecodeSymbol(BitBuffer<LsbBitOrder> bitBuffer) {
    if (bitBuffer.EnsureBits(this.MaxCodeLength)) {
      // Fast path: we have enough bits for a direct lookup
      var peek = bitBuffer.PeekBits(this.MaxCodeLength);
      var symbol = this._symbolTable[peek];
      var len = this._lengthTable[peek];

      if (len == 0)
        ThrowInvalidHuffmanCode();

      bitBuffer.DropBits(len);
      return symbol;
    }

    // Slow path: near end of stream, decode bit-by-bit
    var available = bitBuffer.BitsAvailable;
    if (available == 0)
      ThrowNoBitsAvailable();

    // Peek what we have, zero-extend to maxCodeLength
    var partialPeek = bitBuffer.PeekBits(available);
    // The lookup table works because upper bits are zero
    var slowSymbol = this._symbolTable[partialPeek];
    var slowLen = this._lengthTable[partialPeek];

    if (slowLen == 0 || slowLen > available)
      ThrowTruncatedHuffman();

    bitBuffer.DropBits(slowLen);
    return slowSymbol;
  }

  /// <summary>
  /// Gets the bit-reversed code and length for encoding a symbol.
  /// </summary>
  /// <param name="symbol">The symbol to encode.</param>
  /// <returns>The reversed code and its length in bits.</returns>
  public (uint ReversedCode, int Length) GetCode(int symbol) {
    if ((uint)symbol >= (uint)this._encodeCodes.Length)
      throw new ArgumentOutOfRangeException(nameof(symbol), symbol,
        $"Symbol {symbol} is outside the Huffman table range [0, {this._encodeCodes.Length}).");
    return (this._encodeCodes[symbol], this._encodeLengths[symbol]);
  }

  /// <summary>
  /// Reverses the bit order of a code.
  /// </summary>
  /// <param name="code">The code to reverse.</param>
  /// <param name="length">The number of bits in the code.</param>
  /// <returns>The bit-reversed code.</returns>
  internal static uint ReverseBits(uint code, int length) => BitHelpers.ReverseBits(code, length);

  [DoesNotReturn]
  [StackTraceHidden]
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidHuffmanCode() => throw new InvalidDataException("Invalid Huffman code encountered.");

  [DoesNotReturn]
  [StackTraceHidden]
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowNoBitsAvailable() => throw new EndOfStreamException("No bits available for Huffman decoding.");

  [DoesNotReturn]
  [StackTraceHidden]
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowTruncatedHuffman() => throw new InvalidDataException("Invalid or truncated Huffman code at end of stream.");

}
