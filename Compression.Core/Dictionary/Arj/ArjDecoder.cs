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
    int windowPos = 0;
    int outPos = 0;

    while (outPos < originalSize) {
      if (this._blockRemaining == 0)
        ReadBlock();

      int code = DecodeCode();
      --this._blockRemaining;

      if (code < NChar) {
        byte b = (byte)code;
        output[outPos++] = b;
        window[windowPos] = b;
        windowPos = (windowPos + 1) % this._windowSize;
      } else {
        int length = code - NChar + Threshold;
        int position = DecodePosition();

        int srcPos = ((windowPos - position - 1) % this._windowSize + this._windowSize) % this._windowSize;
        for (int j = 0; j < length && outPos < originalSize; ++j) {
          byte b = window[srcPos];
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
    int num = (int)this._bits.ReadBits(9);
    if (num == 0) {
      // Single symbol
      this._singleCodeSymbol = (int)this._bits.ReadBits(9);
      this._codeTable = null;
      this._codeTableBits = 0;
      return;
    }

    this._singleCodeSymbol = -1;

    // Read code-length tree first
    int[] lenCodeLengths = ReadCodeLengthTree();

    // Use code-length tree to read the actual char/length code lengths
    int[] codeLengths = new int[NumCodes];
    (int[] lenTable, int lenBits) = BuildLenDecodeTable(lenCodeLengths);

    int i = 0;
    while (i < num) {
      int code = DecodeFromTable(lenTable, lenBits);
      if (code <= 2) {
        // Run of zeros: code 0 = 1 zero, code 1 = 3+next3bits zeros, code 2 = 20+next9bits zeros
        int runLen = code switch {
          0 => 1,
          1 => (int)this._bits.ReadBits(4) + 3,
          _ => (int)this._bits.ReadBits(9) + 20
        };
        i += runLen;
      } else {
        codeLengths[i++] = code - 2;
      }
    }

    int maxLen = codeLengths.Max();
    this._codeTableBits = Math.Min(maxLen > 0 ? maxLen : 1, 12);
    this._codeTable = BuildDecodeTable(codeLengths, this._codeTableBits);
  }

  private void ReadPosTree() {
    int num = (int)this._bits.ReadBits(5);
    if (num == 0) {
      this._singlePosSymbol = (int)this._bits.ReadBits(5);
      this._posTable = null;
      this._posTableBits = 0;
      return;
    }

    this._singlePosSymbol = -1;
    int maxPosBits = this._windowSize <= 2048 ? 11 : 15;
    int[] posLengths = new int[maxPosBits + 1];

    for (int i = 0; i < num && i <= maxPosBits; ++i) {
      int len = (int)this._bits.ReadBits(4);
      posLengths[i] = len;
    }

    int maxLen = posLengths.Max();
    this._posTableBits = Math.Min(maxLen > 0 ? maxLen : 1, 12);
    this._posTable = BuildDecodeTable(posLengths, this._posTableBits);
  }

  private int[] ReadCodeLengthTree() {
    int num = (int)this._bits.ReadBits(5);
    if (num == 0) {
      // Single code-length symbol
      int single = (int)this._bits.ReadBits(5);
      return [single]; // Special case handled below
    }

    int[] lengths = new int[NumLenSymbols];
    for (int i = 0; i < num && i < NumLenSymbols; ++i) {
      int len = (int)this._bits.ReadBits(3);
      lengths[i] = len;
      // Special: after symbol 2, skip 'n' symbols (for zero runs in the code-length tree itself)
      if (i == 2) {
        int skip = (int)this._bits.ReadBits(2);
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
    int slot = this._singlePosSymbol >= 0
      ? this._singlePosSymbol
      : DecodeFromTable(this._posTable!, this._posTableBits);

    if (slot <= 1)
      return slot;

    int extraBits = slot - 1;
    return (1 << extraBits) + (int)this._bits.ReadBits(extraBits);
  }

  private int DecodeFromTable(int[] table, int maxBits) {
    if (maxBits == 0)
      return 0;

    this._bits.EnsureBits(maxBits);
    uint peekBits = this._bits.PeekBits(maxBits);

    int entry = table[(int)peekBits];
    if (entry < 0) {
      // Slow path: walk the tree bit by bit
      this._bits.DropBits(maxBits);
      return 0;
    }

    int symbol = entry & 0xFFFF;
    int codeLen = entry >> 16;
    this._bits.DropBits(codeLen);
    return symbol;
  }

  private static (int[] Table, int Bits) BuildLenDecodeTable(int[] lengths) {
    if (lengths.Length == 1) {
      // Single symbol - create a table that always returns it
      int sym = lengths[0];
      var t = new int[2];
      int packed = sym | (1 << 16);
      t[0] = packed;
      t[1] = packed;
      return (t, 1);
    }

    int maxLen = lengths.Max();
    if (maxLen == 0) maxLen = 1;
    int bits = Math.Min(maxLen, 12);
    return (BuildDecodeTable(lengths, bits), bits);
  }

  private static int[] BuildDecodeTable(int[] codeLengths, int maxBits) {
    int tableSize = 1 << maxBits;
    var table = new int[tableSize];
    table.AsSpan().Fill(-1);

    var blCount = new int[maxBits + 1];
    foreach (int v in codeLengths)
      if (v > 0 && v <= maxBits)
        ++blCount[v];

    var nextCode = new int[maxBits + 1];
    int code = 0;
    for (int bits = 1; bits <= maxBits; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    for (int sym = 0; sym < codeLengths.Length; ++sym) {
      int len = codeLengths[sym];
      if (len == 0 || len > maxBits)
        continue;

      int symCode = nextCode[len]++;
      // ARJ uses LSB-first bit packing, but canonical codes are assigned MSB-first.
      // We need to reverse the code bits for the LSB-first lookup table.
      int reversed = ReverseBits(symCode, len);
      int fillBits = maxBits - len;
      int packedValue = sym | (len << 16);
      for (int fill = 0; fill < (1 << fillBits); ++fill)
        table[reversed | (fill << len)] = packedValue;
    }

    return table;
  }

  private static int ReverseBits(int value, int bits) {
    int result = 0;
    for (int i = 0; i < bits; ++i) {
      result = (result << 1) | (value & 1);
      value >>= 1;
    }
    return result;
  }
}
