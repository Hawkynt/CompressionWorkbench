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
  private readonly int _maxCodeLength;

  /// <summary>
  /// Gets the maximum code length in bits.
  /// </summary>
  public int MaxCodeLength => this._maxCodeLength;

  /// <summary>
  /// Builds a Deflate Huffman table from code lengths.
  /// </summary>
  /// <param name="codeLengths">Array where index = symbol, value = code length (0 = unused).</param>
  public DeflateHuffmanTable(int[] codeLengths) {
    int maxLen = 0;
    for (int i = 0; i < codeLengths.Length; ++i)
      if (codeLengths[i] > maxLen)
        maxLen = codeLengths[i];

    this._maxCodeLength = maxLen;

    // Assign canonical codes (MSB-first), then bit-reverse for Deflate
    // Step 1: Count codes per length
    Span<int> blCount = stackalloc int[maxLen + 1];
    for (int i = 0; i < codeLengths.Length; ++i)
      if (codeLengths[i] > 0)
        ++blCount[codeLengths[i]];

    // Step 2: Compute first code for each length
    Span<uint> nextCode = stackalloc uint[maxLen + 1];
    uint code = 0;
    for (int bits = 1; bits <= maxLen; ++bits) {
      code = (code + (uint)blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    // Step 3: Assign codes and build encode/decode tables
    this._encodeCodes = new uint[codeLengths.Length];
    this._encodeLengths = new byte[codeLengths.Length];

    int tableSize = maxLen > 0 ? 1 << maxLen : 0;
    this._symbolTable = new ushort[tableSize];
    this._lengthTable = new byte[tableSize];

    for (int symbol = 0; symbol < codeLengths.Length; symbol++) {
      int len = codeLengths[symbol];
      if (len == 0)
        continue;

      uint canonical = nextCode[len]++;
      uint reversed = ReverseBits(canonical, len);

      this._encodeCodes[symbol] = reversed;
      this._encodeLengths[symbol] = (byte)len;

      // Fill all table entries that share this prefix
      // The reversed code occupies the low 'len' bits; we vary the upper bits
      int fill = 1 << len;
      for (int entry = (int)reversed; entry < tableSize; entry += fill) {
        this._symbolTable[entry] = (ushort)symbol;
        this._lengthTable[entry] = (byte)len;
      }
    }
  }

  /// <summary>
  /// Creates the static literal/length Huffman table for Deflate (BTYPE=1).
  /// </summary>
  public static DeflateHuffmanTable CreateStaticLiteralTable() =>
    new DeflateHuffmanTable(DeflateConstants.GetStaticLiteralLengths());

  /// <summary>
  /// Creates the static distance Huffman table for Deflate (BTYPE=1).
  /// </summary>
  public static DeflateHuffmanTable CreateStaticDistanceTable() =>
    new DeflateHuffmanTable(DeflateConstants.GetStaticDistanceLengths());

  /// <summary>
  /// Decodes a symbol from the bit buffer using lookup-table decoding.
  /// The bit buffer must be in LSB-first mode.
  /// </summary>
  /// <param name="bitBuffer">The bit buffer to read from.</param>
  /// <returns>The decoded symbol.</returns>
  public int DecodeSymbol(BitBuffer<LsbBitOrder> bitBuffer) {
    if (bitBuffer.EnsureBits(this._maxCodeLength)) {
      // Fast path: we have enough bits for a direct lookup
      uint peek = bitBuffer.PeekBits(this._maxCodeLength);
      int symbol = this._symbolTable[peek];
      int len = this._lengthTable[peek];

      if (len == 0)
        throw new InvalidDataException("Invalid Huffman code encountered.");

      bitBuffer.DropBits(len);
      return symbol;
    }

    // Slow path: near end of stream, decode bit-by-bit
    int available = bitBuffer.BitsAvailable;
    if (available == 0)
      throw new EndOfStreamException("No bits available for Huffman decoding.");

    // Peek what we have, zero-extend to maxCodeLength
    uint partialPeek = bitBuffer.PeekBits(available);
    // The lookup table works because upper bits are zero
    int slowSymbol = this._symbolTable[partialPeek];
    int slowLen = this._lengthTable[partialPeek];

    if (slowLen == 0 || slowLen > available)
      throw new InvalidDataException("Invalid or truncated Huffman code at end of stream.");

    bitBuffer.DropBits(slowLen);
    return slowSymbol;
  }

  /// <summary>
  /// Gets the bit-reversed code and length for encoding a symbol.
  /// </summary>
  /// <param name="symbol">The symbol to encode.</param>
  /// <returns>The reversed code and its length in bits.</returns>
  public (uint ReversedCode, int Length) GetCode(int symbol) {
    return (this._encodeCodes[symbol], this._encodeLengths[symbol]);
  }

  /// <summary>
  /// Reverses the bit order of a code.
  /// </summary>
  /// <param name="code">The code to reverse.</param>
  /// <param name="length">The number of bits in the code.</param>
  /// <returns>The bit-reversed code.</returns>
  internal static uint ReverseBits(uint code, int length) {
    uint result = 0;
    for (int i = 0; i < length; ++i) {
      result = (result << 1) | (code & 1);
      code >>= 1;
    }
    return result;
  }
}
