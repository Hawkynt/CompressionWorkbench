using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Compression.Core.BitIO;

namespace Compression.Core.Entropy.Huffman;

/// <summary>
/// Builds canonical Huffman encode/decode tables from code lengths.
/// Canonical codes assign codes in a deterministic manner given only the code lengths.
/// </summary>
public sealed class CanonicalHuffman {
  private readonly (uint Code, int Length)[] _encodeTable;
  private readonly int[] _decodeLookup;   // symbol lookup indexed by left-aligned code
  private readonly int[] _decodeLengths;  // code length lookup

  // For table-based fast decoding
  private readonly int _lookupBits;
  private readonly int[] _lookupTable;    // symbol for each index, or -1

  /// <summary>
  /// Gets the maximum symbol value in this table.
  /// </summary>
  public int MaxSymbol => this._encodeTable.Length;

  /// <summary>
  /// Gets the maximum code length in bits.
  /// </summary>
  public int MaxCodeLength { get; }

  /// <summary>
  /// Builds canonical Huffman encode and decode tables from the given code lengths.
  /// </summary>
  /// <param name="codeLengths">Array where index = symbol, value = code length (0 = unused).</param>
  public CanonicalHuffman(int[] codeLengths) {
    var maxSymbol = codeLengths.Length;
    this._encodeTable = new (uint Code, int Length)[maxSymbol];
    this.MaxCodeLength = 0;

    for (var i = 0; i < maxSymbol; ++i)
      if (codeLengths[i] > this.MaxCodeLength)
        this.MaxCodeLength = codeLengths[i];

    if (this.MaxCodeLength == 0) {
      this._lookupBits = 0;
      this._lookupTable = [];
      this._decodeLookup = [];
      this._decodeLengths = [];
      return;
    }

    // Steps 1-2: Count codes per length and compute first code for each length
    var nextCode = new uint[this.MaxCodeLength + 1];
    CanonicalCodeAssigner.ComputeNextCodes(codeLengths, this.MaxCodeLength, nextCode);

    // Step 3: Assign codes to symbols
    for (var symbol = 0; symbol < maxSymbol; ++symbol) {
      var len = codeLengths[symbol];
      if (len <= 0)
        continue;

      this._encodeTable[symbol] = (nextCode[len], len);
      ++nextCode[len];
    }

    // Step 4: Build lookup table for decoding
    this._lookupBits = Math.Min(this.MaxCodeLength, 10);
    var lookupSize = 1 << this._lookupBits;
    this._lookupTable = new int[lookupSize];
    this._lookupTable.AsSpan().Fill(-1);
    
    this._decodeLookup = new int[1 << this.MaxCodeLength];
    this._decodeLengths = new int[1 << this.MaxCodeLength];
    this._decodeLookup.AsSpan().Fill(-1);

    for (var symbol = 0; symbol < maxSymbol; ++symbol) {
      var len = codeLengths[symbol];
      if (len <= 0)
        continue;

      var code = this._encodeTable[symbol].Code;

      if (len <= this._lookupBits) {
        // Fill all entries that share this prefix
        var shift = this._lookupBits - len;
        var start = (int)(code << shift);
        var count = 1 << shift;
        this._lookupTable.AsSpan(start, count).Fill(symbol);
      }

      // Full decode table
      {
        var shift = this.MaxCodeLength - len;
        var start = (int)(code << shift);
        var count = 1 << shift;
        this._decodeLookup.AsSpan(start, count).Fill(symbol);
        this._decodeLengths.AsSpan(start, count).Fill(len);
      }
    }
  }

  /// <summary>
  /// Gets the canonical code and length for the specified symbol.
  /// </summary>
  /// <param name="symbol">The symbol to look up.</param>
  /// <returns>The code and its length in bits.</returns>
  public (uint Code, int Length) GetCode(int symbol) => this._encodeTable[symbol];

  /// <summary>
  /// Decodes a symbol from the bit buffer using the canonical Huffman table.
  /// </summary>
  /// <typeparam name="TOrder">The bit-order strategy (e.g. <see cref="MsbBitOrder"/>).</typeparam>
  /// <param name="bitBuffer">The bit buffer to read from (MSB-first codes).</param>
  /// <returns>The decoded symbol.</returns>
  /// <exception cref="InvalidDataException">The bit sequence does not match any symbol.</exception>
  public int DecodeSymbol<TOrder>(BitBuffer<TOrder> bitBuffer) where TOrder : struct, IBitOrder {
    if (this.MaxCodeLength == 0)
      ThrowEmptyHuffmanTable();

    bitBuffer.EnsureBits(this.MaxCodeLength);

    var bitsAvailable = Math.Min(this.MaxCodeLength, bitBuffer.BitsAvailable);
    var peekValue = bitBuffer.PeekBits(bitsAvailable);

    // Left-align the peeked value to MaxCodeLength bits for decode table lookup
    if (bitsAvailable < this.MaxCodeLength)
      peekValue <<= (this.MaxCodeLength - bitsAvailable);

    var index = (int)peekValue;
    if (index < this._decodeLookup.Length && this._decodeLookup[index] >= 0) {
      var codeLen = this._decodeLengths[index];
      if (codeLen <= bitsAvailable) {
        bitBuffer.DropBits(codeLen);
        return this._decodeLookup[index];
      }
    }

    ThrowInvalidHuffmanCode();
    return 0; // unreachable
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
    var code = 0u;

    for (var len = 1; len <= this.MaxCodeLength; ++len) {
      code = (code << 1) | (uint)bitReader.ReadBit();

      // Check if this code matches any symbol at this length
      for (var symbol = 0; symbol < this._encodeTable.Length; ++symbol)
        if (this._encodeTable[symbol].Length == len && this._encodeTable[symbol].Code == code)
          return symbol;
    }

    ThrowInvalidHuffmanCode();
    return 0; // unreachable
  }

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowEmptyHuffmanTable() => throw new InvalidDataException("Empty Huffman table.");

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidHuffmanCode() => throw new InvalidDataException("Invalid Huffman code encountered.");

}
