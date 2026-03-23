using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Arj;

/// <summary>
/// Decodes ARJ-compressed data (methods 1-3).
/// Uses LZSS with Huffman-coded literals/lengths and positions.
/// </summary>
/// <remarks>
/// The algorithm uses two Huffman trees per block:
/// a character/length tree (literals 0-255 + match length codes 256+)
/// and a position tree. Trees are encoded as code lengths using
/// a preliminary code-length Huffman tree.
/// </remarks>
public sealed class ArjDecoder {
  private const int NChar = 256;
  private const int Threshold = 3;
  private const int MaxMatch = 256;
  // Number of character/length codes: 256 literals + (MaxMatch - Threshold + 1) match lengths
  private const int NumCodes = NChar + MaxMatch - Threshold + 1; // 510

  // Maximum code bits for trees
  private const int MaxCodeBits = 16;

  // Code-length tree: symbols 0..16 + special codes
  private const int NumLenSymbols = 19;

  private readonly BitBuffer<LsbBitOrder> _bits;
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
  /// Initializes a new <see cref="ArjDecoder"/>.
  /// </summary>
  /// <param name="input">The stream containing compressed data.</param>
  /// <param name="method">The ARJ compression method (1, 2, or 3).</param>
  public ArjDecoder(Stream input, int method = 1) {
    this._bits = new(input);
    // Method 1: 26624 bytes, Method 2/3: 2048 bytes
    this._windowSize = method == 1 ? 26624 : 2048;
  }

  /// <summary>
  /// Decodes the compressed data.
  /// </summary>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] Decode(int originalSize) {
    if (originalSize == 0)
      return [];

    var output = new byte[originalSize];
    var window = new byte[this._windowSize];
    Array.Fill(window, (byte)0x20);
    var windowPos = 0;
    var outPos = 0;

    while (outPos < originalSize) {
      if (this._blockRemaining == 0)
        ReadBlock();

      var code = DecodeCode();
      --this._blockRemaining;

      if (code < NChar) {
        var b = (byte)code;
        output[outPos++] = b;
        window[windowPos] = b;
        windowPos = (windowPos + 1) % this._windowSize;
      } else {
        var length = code - NChar + Threshold;
        var position = DecodePosition();

        var srcPos = ((windowPos - position - 1) % this._windowSize + this._windowSize) % this._windowSize;
        for (var j = 0; j < length && outPos < originalSize; ++j) {
          var b = window[srcPos];
          output[outPos++] = b;
          window[windowPos] = b;
          windowPos = (windowPos + 1) % this._windowSize;
          srcPos = (srcPos + 1) % this._windowSize;
        }
      }
    }

    return output;
  }

  private void ReadBlock() {
    this._blockRemaining = (int)this._bits.ReadBits(16);
    if (this._blockRemaining == 0)
      return;

    ReadCharTree();
    ReadPosTree();
  }

  private void ReadCharTree() {
    var num = (int)this._bits.ReadBits(9);
    if (num == 0) {
      // Single symbol
      this._singleCodeSymbol = (int)this._bits.ReadBits(9);
      this._codeTable = null;
      this._codeTableBits = 0;
      return;
    }

    this._singleCodeSymbol = -1;

    // Read code-length tree first
    var lenCodeLengths = ReadCodeLengthTree();

    // Use code-length tree to read the actual char/length code lengths
    var codeLengths = new int[NumCodes];
    (var lenTable, var lenBits) = BuildLenDecodeTable(lenCodeLengths);

    var i = 0;
    while (i < num) {
      var code = DecodeFromTable(lenTable, lenBits);
      if (code <= 2) {
        // Run of zeros: code 0 = 1 zero, code 1 = 3+next3bits zeros, code 2 = 20+next9bits zeros
        var runLen = code switch {
          0 => 1,
          1 => (int)this._bits.ReadBits(4) + 3,
          _ => (int)this._bits.ReadBits(9) + 20
        };
        i += runLen;
      } else {
        codeLengths[i++] = code - 2;
      }
    }

    var maxLen = codeLengths.Max();
    this._codeTableBits = Math.Min(maxLen > 0 ? maxLen : 1, 12);
    this._codeTable = BuildDecodeTable(codeLengths, this._codeTableBits);
  }

  private void ReadPosTree() {
    var num = (int)this._bits.ReadBits(5);
    if (num == 0) {
      this._singlePosSymbol = (int)this._bits.ReadBits(5);
      this._posTable = null;
      this._posTableBits = 0;
      return;
    }

    this._singlePosSymbol = -1;
    var maxPosBits = this._windowSize <= 2048 ? 11 : 15;
    var posLengths = new int[maxPosBits + 1];

    for (var i = 0; i < num && i <= maxPosBits; ++i) {
      var len = (int)this._bits.ReadBits(4);
      posLengths[i] = len;
    }

    var maxLen = posLengths.Max();
    this._posTableBits = Math.Min(maxLen > 0 ? maxLen : 1, 12);
    this._posTable = BuildDecodeTable(posLengths, this._posTableBits);
  }

  private int[] ReadCodeLengthTree() {
    var num = (int)this._bits.ReadBits(5);
    if (num == 0) {
      // Single code-length symbol
      var single = (int)this._bits.ReadBits(5);
      return [single]; // Special case handled below
    }

    var lengths = new int[NumLenSymbols];
    for (var i = 0; i < num && i < NumLenSymbols; ++i) {
      var len = (int)this._bits.ReadBits(3);
      lengths[i] = len;
      // Special: after symbol 2, skip 'n' symbols (for zero runs in the code-length tree itself)
      if (i == 2) {
        var skip = (int)this._bits.ReadBits(2);
        i += skip;
      }
    }

    return lengths;
  }

  private int DecodeCode() {
    if (this._singleCodeSymbol >= 0)
      return this._singleCodeSymbol;
    return DecodeFromTable(this._codeTable!, this._codeTableBits);
  }

  private int DecodePosition() {
    var slot = this._singlePosSymbol >= 0
      ? this._singlePosSymbol
      : DecodeFromTable(this._posTable!, this._posTableBits);

    if (slot <= 1)
      return slot;

    var extraBits = slot - 1;
    return (1 << extraBits) + (int)this._bits.ReadBits(extraBits);
  }

  private int DecodeFromTable(int[] table, int maxBits) {
    if (maxBits == 0)
      return 0;

    this._bits.EnsureBits(maxBits);
    var peekBits = this._bits.PeekBits(maxBits);

    var entry = table[(int)peekBits];
    if (entry < 0) {
      // Slow path: walk the tree bit by bit
      this._bits.DropBits(maxBits);
      return 0;
    }

    var symbol = entry & 0xFFFF;
    var codeLen = entry >> 16;
    this._bits.DropBits(codeLen);
    return symbol;
  }

  private static (int[] Table, int Bits) BuildLenDecodeTable(int[] lengths) {
    if (lengths.Length == 1) {
      // Single symbol - create a table that always returns it
      var sym = lengths[0];
      var t = new int[2];
      var packed = sym | (1 << 16);
      t[0] = packed;
      t[1] = packed;
      return (t, 1);
    }

    var maxLen = lengths.Max();
    if (maxLen == 0) maxLen = 1;
    var bits = Math.Min(maxLen, 12);
    return (BuildDecodeTable(lengths, bits), bits);
  }

  private static int[] BuildDecodeTable(int[] codeLengths, int maxBits) {
    var tableSize = 1 << maxBits;
    var table = new int[tableSize];
    table.AsSpan().Fill(-1);

    var blCount = new int[maxBits + 1];
    foreach (var v in codeLengths)
      if (v > 0 && v <= maxBits)
        ++blCount[v];

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
      // ARJ uses LSB-first bit packing, but canonical codes are assigned MSB-first.
      // We need to reverse the code bits for the LSB-first lookup table.
      var reversed = ReverseBits(symCode, len);
      var fillBits = maxBits - len;
      var packedValue = sym | (len << 16);
      for (var fill = 0; fill < (1 << fillBits); ++fill)
        table[reversed | (fill << len)] = packedValue;
    }

    return table;
  }

  private static int ReverseBits(int value, int bits) {
    var result = 0;
    for (var i = 0; i < bits; ++i) {
      result = (result << 1) | (value & 1);
      value >>= 1;
    }
    return result;
  }
}
