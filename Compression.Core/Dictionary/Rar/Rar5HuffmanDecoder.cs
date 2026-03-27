using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Multi-table Huffman decoder for RAR5 decompression.
/// Supports up to 15-bit codes with fast table-based lookup.
/// </summary>
internal sealed class Rar5HuffmanDecoder {
  private const int QuickBits = 10;
  private const int QuickSize = 1 << Rar5HuffmanDecoder.QuickBits;

  private int[]? _quickTable;
  private int[]? _codeLengths;
  private int _maxCodeLength;
  private int _numSymbols;

  /// <summary>
  /// Builds the decode table from an array of code lengths.
  /// Uses MSB-first bit ordering.
  /// </summary>
  /// <param name="codeLengths">Per-symbol code lengths (0 = unused symbol).</param>
  /// <param name="numSymbols">Number of symbols.</param>
  public void Build(ReadOnlySpan<int> codeLengths, int numSymbols) {
    this._numSymbols = numSymbols;
    this._codeLengths = codeLengths[..numSymbols].ToArray();
    this._maxCodeLength = 0;

    var numUsed = 0;
    for (var i = 0; i < numSymbols; ++i) {
      if (codeLengths[i] <= 0)
        continue;

      this._maxCodeLength = Math.Max(this._maxCodeLength, codeLengths[i]);
      ++numUsed;
    }

    switch (numUsed) {
      case 0:
        this._quickTable = new int[Rar5HuffmanDecoder.QuickSize];
        this._quickTable.AsSpan().Fill(-1);
        return;

      case 1: {
        // Single symbol: fill all table entries with it (any bit pattern decodes to this symbol)
        this._quickTable = new int[Rar5HuffmanDecoder.QuickSize];
        var singleSym = 0;
        for (var i = 0; i < numSymbols; ++i)
          if (codeLengths[i] > 0) {
            singleSym = i;
            break;
          }

        var entry = singleSym | (1 << 16); // length 1
        this._quickTable.AsSpan().Fill(entry);
        this._slowCount = 0;
        return;
      }
    }

    if (this._maxCodeLength > Rar5Constants.MaxCodeLength)
      this._maxCodeLength = Rar5Constants.MaxCodeLength;

    // Count codes of each length
    Span<int> blCount = stackalloc int[this._maxCodeLength + 1];
    for (var i = 0; i < numSymbols; ++i) {
      var len = codeLengths[i];
      if (len > 0 && len <= this._maxCodeLength)
        ++blCount[len];
    }

    // Compute canonical code start values
    Span<int> nextCode = stackalloc int[this._maxCodeLength + 1];
    var code = 0;
    for (var bits = 1; bits <= this._maxCodeLength; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    // Build quick lookup table (MSB-first: left-align codes to QuickBits width)
    this._quickTable = new int[Rar5HuffmanDecoder.QuickSize];
    this._quickTable.AsSpan().Fill(-1);

    for (var sym = 0; sym < numSymbols; ++sym) {
      var len = codeLengths[sym];
      if (len == 0 || len > this._maxCodeLength) continue;

      var c = nextCode[len]++;

      if (len <= Rar5HuffmanDecoder.QuickBits) {
        // Left-align the code to QuickBits width and fill all suffixes
        var entry = sym | (len << 16);
        var prefix = c << (Rar5HuffmanDecoder.QuickBits - len);
        var suffixCount = 1 << (Rar5HuffmanDecoder.QuickBits - len);
        for (var j = 0; j < suffixCount; ++j)
          this._quickTable[prefix + j] = entry;
      } else {
        // For codes longer than QuickBits, mark the prefix for slow path
        var prefix = c >> (len - Rar5HuffmanDecoder.QuickBits);
        var entry = sym | (len << 16);
        if (this._quickTable[prefix] == -1)
          this._quickTable[prefix] = entry | unchecked((int)0x80000000);
      }
    }

    // For symbols with code length > QuickBits, we use sequential decode
    // Build a sorted symbol list for the slow path
    this.BuildSlowTable(codeLengths, numSymbols);
  }

  private int[]? _slowSymbols;
  private int[]? _slowCodes;
  private int[]? _slowLengths;
  private int _slowCount;

  private void BuildSlowTable(ReadOnlySpan<int> codeLengths, int numSymbols) {
    // Count symbols with codes > QuickBits
    var count = 0;
    for (var i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > Rar5HuffmanDecoder.QuickBits && codeLengths[i] <= this._maxCodeLength)
        ++count;

    if (count == 0) {
      this._slowCount = 0;
      return;
    }

    this._slowSymbols = new int[count];
    this._slowCodes = new int[count];
    this._slowLengths = new int[count];
    this._slowCount = count;

    // Recompute codes for slow symbols
    Span<int> blCount2 = stackalloc int[this._maxCodeLength + 1];
    for (var i = 0; i < numSymbols; ++i) {
      var len = codeLengths[i];
      if (len > 0 && len <= this._maxCodeLength)
        blCount2[len]++;
    }

    Span<int> nextCode2 = stackalloc int[this._maxCodeLength + 1];
    var code2 = 0;
    for (var bits = 1; bits <= this._maxCodeLength; ++bits) {
      code2 = (code2 + blCount2[bits - 1]) << 1;
      nextCode2[bits] = code2;
    }

    var idx = 0;
    for (var sym = 0; sym < numSymbols; ++sym) {
      var len = codeLengths[sym];
      var c = nextCode2[len]++;
      if (len <= Rar5HuffmanDecoder.QuickBits || len > this._maxCodeLength)
        continue;

      this._slowSymbols[idx] = sym;
      this._slowCodes[idx] = c;
      this._slowLengths[idx] = len;
      ++idx;
    }
  }

  /// <summary>
  /// Decodes a single symbol from the bit reader.
  /// </summary>
  /// <param name="reader">The bit reader positioned at the next code.</param>
  /// <returns>The decoded symbol value.</returns>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int DecodeSymbol(Rar5BitReader reader) {
    if (this._quickTable == null)
      ThrowNotBuilt();

    reader.Fill(this._maxCodeLength);
    var bits = reader.PeekBits(Rar5HuffmanDecoder.QuickBits);
    var entry = this._quickTable[bits];

    if (entry >= 0) {
      // Quick path: symbol found directly
      var symbol = entry & 0xFFFF;
      var length = (entry >> 16) & 0x7FFF;
      reader.DropBits(length);
      return symbol;
    }

    if (entry != -1 && (entry & unchecked((int)0x80000000)) != 0)
      // Slow path: need more bits
      return this.DecodeSlowPath(reader);

    // No valid code found
    reader.DropBits(1);
    return 0;
  }

  private int DecodeSlowPath(Rar5BitReader reader) {
    if (this._slowCount == 0 || this._slowCodes == null || this._slowSymbols == null || this._slowLengths == null)
      return 0;

    // MSB-first: peek maxCodeLength bits, compare top len bits against canonical codes
    var bits = reader.PeekBits(this._maxCodeLength);

    for (var i = 0; i < this._slowCount; ++i) {
      var len = this._slowLengths[i];
      var code = this._slowCodes[i];
      // Compare top len bits
      var topBits = bits >> (this._maxCodeLength - len);
      if (topBits != (uint)code)
        continue;

      reader.DropBits(len);
      return this._slowSymbols[i];
    }

    reader.DropBits(1);
    return 0;
  }

  [DoesNotReturn]
  [StackTraceHidden]
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowNotBuilt() => throw new InvalidOperationException("Huffman table has not been built.");
}
