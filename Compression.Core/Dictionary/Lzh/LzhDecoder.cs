using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes LZH-compressed data (methods -lh5-, -lh6-, -lh7-).
/// Uses LZSS with Huffman-coded literals/lengths and positions.
/// </summary>
public sealed class LzhDecoder {
  private readonly BitBuffer<MsbBitOrder> _bits;
  private readonly int _windowSize;

  // Current block state
  private int _blockRemaining;
  private int _singleCodeSymbol = -1;
  private int[]? _codeTable;
  private int _codeTableBits;
  private int _singlePosSymbol = -1;
  private int[]? _posTable;
  private int _posTableBits;

  /// <summary>
  /// Initializes a new <see cref="LzhDecoder"/>.
  /// </summary>
  /// <param name="input">The stream containing compressed data.</param>
  /// <param name="positionBits">Number of position bits (13 for lh5, 15 for lh6, 16 for lh7).</param>
  public LzhDecoder(Stream input, int positionBits = LzhConstants.Lh5PositionBits) {
    this._bits = new(input);
    this._windowSize = 1 << positionBits;
  }

  /// <summary>
  /// Decodes the compressed data.
  /// </summary>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] Decode(int originalSize) {
    var output = new byte[originalSize];
    var window = new byte[this._windowSize];
    var windowPos = 0;
    var outPos = 0;

    while (outPos < originalSize) {
      if (this._blockRemaining == 0)
        this.ReadBlock();

      var code = this.DecodeCode();
      --this._blockRemaining;

      if (code < LzhConstants.NChar) {
        var b = (byte)code;
        output[outPos++] = b;
        window[windowPos] = b;
        windowPos = (windowPos + 1) & (this._windowSize - 1);
      } else {
        var length = code - LzhConstants.NChar + LzhConstants.Threshold;
        var position = this.DecodePosition();

        var srcPos = (windowPos - position - 1 + this._windowSize) & (this._windowSize - 1);
        for (var j = 0; j < length && outPos < originalSize; ++j) {
          var b = window[srcPos];
          output[outPos++] = b;
          window[windowPos] = b;
          windowPos = (windowPos + 1) & (this._windowSize - 1);
          srcPos = (srcPos + 1) & (this._windowSize - 1);
        }
      }
    }

    return output;
  }

  private void ReadBlock() {
    this._blockRemaining = (int)this._bits.ReadBits(16);
    this.ReadTree(out this._singleCodeSymbol, out this._codeTable, out this._codeTableBits);
    this.ReadTree(out this._singlePosSymbol, out this._posTable, out this._posTableBits);
  }

  private void ReadTree(out int singleSymbol, out int[]? table, out int tableBits) {
    var flag = (int)this._bits.ReadBits(1);

    if (flag == 1) {
      // Single symbol
      singleSymbol = (int)this._bits.ReadBits(16);
      table = null;
      tableBits = 0;
      return;
    }

    singleSymbol = -1;
    var count = (int)this._bits.ReadBits(16);

    // Read symbol/length pairs
    var maxSym = 0;
    var entries = new (int sym, int len)[count];
    var maxLen = 0;
    for (var i = 0; i < count; ++i) {
      var sym = (int)this._bits.ReadBits(16);
      var len = (int)this._bits.ReadBits(5);
      entries[i] = (sym, len);
      if (sym > maxSym) maxSym = sym;
      if (len > maxLen) maxLen = len;
    }

    // Build code lengths array
    var codeLengths = new int[maxSym + 1];
    foreach (var (sym, len) in entries)
      codeLengths[sym] = len;

    tableBits = Math.Min(maxLen, 16);
    table = BuildDecodeTable(codeLengths, tableBits);
  }

  private int DecodeCode() {
    if (this._singleCodeSymbol >= 0)
      return this._singleCodeSymbol;
    return this.DecodeFromTable(this._codeTable!, this._codeTableBits);
  }

  private int DecodePosition() {
    var slot = this._singlePosSymbol >= 0 ? this._singlePosSymbol : this.DecodeFromTable(this._posTable!, this._posTableBits);
    if (slot <= 1)
      return slot;

    var extraBits = slot - 1;
    return (1 << extraBits) + (int)this._bits.ReadBits(extraBits);
  }

  private int DecodeFromTable(int[] table, int maxBits) {
    this._bits.EnsureBits(maxBits);
    var peekBits = this._bits.PeekBits(maxBits);

    var entry = table[(int)peekBits];
    if (entry < 0)
      throw new InvalidDataException("Failed to decode Huffman symbol.");

    var symbol = entry & 0xFFFF;
    var codeLen = entry >> 16;
    this._bits.DropBits(codeLen);
    return symbol;
  }

  private static int[] BuildDecodeTable(int[] codeLengths, int maxBits) {
    var tableSize = 1 << maxBits;
    var table = new int[tableSize];
    table.AsSpan().Fill(-1);

    var blCount = new int[maxBits + 1];
    foreach (var value in codeLengths)
      if (value > 0 && value <= maxBits)
        ++blCount[value];

    var nextCode = new int[maxBits + 1];
    var code = 0;
    for (var bits = 1; bits <= maxBits; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    for (var sym = 0; sym < codeLengths.Length; ++sym) {
      var len = codeLengths[sym];
      if (len == 0 || len > maxBits)
        continue;

      var symCode = nextCode[len]++;
      var fillBits = maxBits - len;
      var baseIdx = symCode << fillBits;
      var packedValue = sym | (len << 16);
      for (var fill = 0; fill < (1 << fillBits); ++fill)
        table[baseIdx + fill] = packedValue;
    }

    return table;
  }
}
