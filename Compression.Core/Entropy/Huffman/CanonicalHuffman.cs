using Compression.Core.BitIO;

namespace Compression.Core.Entropy.Huffman;

/// <summary>
/// Builds canonical Huffman encode/decode tables from code lengths.
/// Canonical codes assign codes in a deterministic manner given only the code lengths.
/// </summary>
public sealed class CanonicalHuffman {
  private readonly (uint Code, int Length)[] _encodeTable;
  private readonly int _maxCodeLength;
  private readonly int[] _decodeLookup;   // symbol lookup indexed by left-aligned code
  private readonly int[] _decodeLengths;  // code length lookup

  // For table-based fast decoding
  private readonly int _lookupBits;
  private readonly int[] _lookupTable;    // symbol for each index, or -1

  /// <summary>
  /// Gets the maximum symbol value in this table.
  /// </summary>
  public int MaxSymbol => _encodeTable.Length;

  /// <summary>
  /// Gets the maximum code length in bits.
  /// </summary>
  public int MaxCodeLength => this._maxCodeLength;

  /// <summary>
  /// Builds canonical Huffman encode and decode tables from the given code lengths.
  /// </summary>
  /// <param name="codeLengths">Array where index = symbol, value = code length (0 = unused).</param>
  public CanonicalHuffman(int[] codeLengths) {
    int maxSymbol = codeLengths.Length;
    _encodeTable = new (uint Code, int Length)[maxSymbol];
    this._maxCodeLength = 0;

    for (int i = 0; i < maxSymbol; ++i)
      if (codeLengths[i] > this._maxCodeLength)
        this._maxCodeLength = codeLengths[i];

    if (this._maxCodeLength == 0) {
      this._lookupBits = 0;
      this._lookupTable = [];
      this._decodeLookup = [];
      this._decodeLengths = [];
      return;
    }

    // Step 1: Count the number of codes for each code length
    var blCount = new int[this._maxCodeLength + 1];
    for (int i = 0; i < maxSymbol; ++i) {
      if (codeLengths[i] > 0)
        ++blCount[codeLengths[i]];
    }

    // Step 2: Find the numerical value of the smallest code for each code length
    var nextCode = new uint[this._maxCodeLength + 1];
    uint code = 0;
    for (int bits = 1; bits <= this._maxCodeLength; ++bits) {
      code = (code + (uint)blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    // Step 3: Assign codes to symbols
    for (int symbol = 0; symbol < maxSymbol; ++symbol) {
      int len = codeLengths[symbol];
      if (len > 0) {
        _encodeTable[symbol] = (nextCode[len], len);
        ++nextCode[len];
      }
    }

    // Step 4: Build lookup table for decoding
    this._lookupBits = Math.Min(this._maxCodeLength, 10);
    int lookupSize = 1 << this._lookupBits;
    this._lookupTable = new int[lookupSize];
    Array.Fill(this._lookupTable, -1);

    this._decodeLookup = new int[1 << this._maxCodeLength];
    this._decodeLengths = new int[1 << this._maxCodeLength];
    Array.Fill(this._decodeLookup, -1);

    for (int symbol = 0; symbol < maxSymbol; ++symbol) {
      int len = codeLengths[symbol];
      if (len <= 0)
        continue;

      uint c = _encodeTable[symbol].Code;

      if (len <= this._lookupBits) {
        // Fill all entries that share this prefix
        int shift = this._lookupBits - len;
        int start = (int)(c << shift);
        int count = 1 << shift;
        for (int j = 0; j < count; ++j)
          this._lookupTable[start + j] = symbol;
      }

      // Full decode table
      {
        int shift = this._maxCodeLength - len;
        int start = (int)(c << shift);
        int count = 1 << shift;
        for (int j = 0; j < count; ++j) {
          this._decodeLookup[start + j] = symbol;
          this._decodeLengths[start + j] = len;
        }
      }
    }
  }

  /// <summary>
  /// Gets the canonical code and length for the specified symbol.
  /// </summary>
  /// <param name="symbol">The symbol to look up.</param>
  /// <returns>The code and its length in bits.</returns>
  public (uint Code, int Length) GetCode(int symbol) => _encodeTable[symbol];

  /// <summary>
  /// Decodes a symbol from the bit buffer using the canonical Huffman table.
  /// </summary>
  /// <typeparam name="TOrder">The bit-order strategy (e.g. <see cref="MsbBitOrder"/>).</typeparam>
  /// <param name="bitBuffer">The bit buffer to read from (MSB-first codes).</param>
  /// <returns>The decoded symbol.</returns>
  /// <exception cref="InvalidDataException">The bit sequence does not match any symbol.</exception>
  public int DecodeSymbol<TOrder>(BitBuffer<TOrder> bitBuffer) where TOrder : struct, IBitOrder {
    if (this._maxCodeLength == 0)
      throw new InvalidDataException("Empty Huffman table.");

    if (!bitBuffer.EnsureBits(this._maxCodeLength)) {
      // Try with what we have
    }

    uint peekValue = bitBuffer.PeekBits(Math.Min(this._maxCodeLength, bitBuffer.BitsAvailable));

    if (bitBuffer.BitsAvailable >= this._maxCodeLength) {
      // Pad to max code length for lookup
      int index = (int)peekValue;
      if (index < this._decodeLookup.Length && this._decodeLookup[index] >= 0) {
        int symbol = this._decodeLookup[index];
        bitBuffer.DropBits(this._decodeLengths[index]);
        return symbol;
      }
    }

    throw new InvalidDataException("Invalid Huffman code encountered.");
  }

  /// <summary>
  /// Decodes a symbol by reading bits one at a time from a <see cref="BitReader{TOrder}"/>.
  /// This is slower than <see cref="DecodeSymbol{TOrder}(BitBuffer{TOrder})"/> but works without lookahead.
  /// </summary>
  /// <typeparam name="TOrder">The bit-order strategy.</typeparam>
  /// <param name="bitReader">The bit reader to read from.</param>
  /// <returns>The decoded symbol.</returns>
  /// <exception cref="InvalidDataException">The bit sequence does not match any symbol.</exception>
  public int DecodeSymbol<TOrder>(BitReader<TOrder> bitReader) where TOrder : struct, IBitOrder {
    uint code = 0;

    for (int len = 1; len <= this._maxCodeLength; ++len) {
      code = (code << 1) | (uint)bitReader.ReadBit();

      // Check if this code matches any symbol at this length
      for (int symbol = 0; symbol < _encodeTable.Length; ++symbol) {
        if (_encodeTable[symbol].Length == len && _encodeTable[symbol].Code == code)
          return symbol;
      }
    }

    throw new InvalidDataException("Invalid Huffman code encountered.");
  }
}
