using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes LZH-compressed data (methods -lh5-, -lh6-, -lh7-).
/// Uses LZSS with Huffman-coded literals/lengths and positions.
/// </summary>
public sealed class LzhDecoder {
  private readonly BitBuffer<MsbBitOrder> _bits;
  private readonly int _windowSize;
  private readonly int _pBit;

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
    this._pBit = positionBits switch {
      12 => 4, // lh4
      13 => 4, // lh5
      15 => 5, // lh6
      16 => 5, // lh7
      _ => 4
    };
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
    this.ReadCTree();
    this.ReadPTree();
  }

  /// <summary>
  /// Reads the C tree (literal/length codes) using standard LZH encoding.
  /// First reads a T tree (code-length tree), then decodes C tree lengths using T.
  /// </summary>
  private void ReadCTree() {
    // Read T tree first (code-length tree for encoding C tree)
    var tLengths = this.ReadPtTree(5, 3);
    int[]? tTable = null;
    var tTableBits = 0;
    var tSingleSym = -1;

    if (tLengths == null) {
      // Single-symbol T tree already read by ReadPtTree — symbol stored in return
      // Actually ReadPtTree returns null for single. Re-read.
    }

    // We need a different approach: ReadPtTree should give us what we need
    // Let me inline the T tree reading here

    // Re-read approach: we already consumed bits in ReadPtTree...
    // Let me refactor: ReadPtTree returns the code lengths array (or indicates single)

    // Actually, let's use a different approach
    // null means we need to re-read. Let me fix the API.

    // Simplified: ReadPtTree already returns the array. If only 1 symbol, it will
    // have exactly one non-zero entry.
    if (tLengths != null) {
      var tUsed = 0;
      for (var j = 0; j < tLengths.Length; ++j)
        if (tLengths[j] > 0) { tSingleSym = j; ++tUsed; }
      if (tUsed > 1) {
        tSingleSym = -1;
        var maxLen = tLengths.Max();
        tTableBits = Math.Min(maxLen, 12);
        tTable = BuildDecodeTable(tLengths, tTableBits);
      }
    }

    // Read CNUM
    var numC = (int)this._bits.ReadBits(9);
    if (numC == 0) {
      // Single-symbol C tree
      this._singleCodeSymbol = (int)this._bits.ReadBits(9);
      this._codeTable = null;
      this._codeTableBits = 0;
      return;
    }

    // Decode C tree lengths using T tree
    var codeLengths = new int[Math.Max(numC, LzhConstants.NumCodes)];
    var i = 0;
    while (i < numC) {
      int tSym;
      if (tSingleSym >= 0)
        tSym = tSingleSym;
      else if (tTable != null)
        tSym = DecodeFromTableStatic(this._bits, tTable, tTableBits);
      else
        tSym = 0;

      switch (tSym) {
        case 0:
          // Zero code length for this symbol
          codeLengths[i++] = 0;
          break;
        case 1: {
          // Run of (3 + next 4 bits) zeros
          var run = 3 + (int)this._bits.ReadBits(4);
          for (var j = 0; j < run && i < numC; ++j)
            codeLengths[i++] = 0;
          break;
        }
        case 2: {
          // Run of (20 + next 9 bits) zeros
          var run = 20 + (int)this._bits.ReadBits(9);
          for (var j = 0; j < run && i < numC; ++j)
            codeLengths[i++] = 0;
          break;
        }
        default:
          // Actual code length = tSym - 2
          codeLengths[i++] = tSym - 2;
          break;
      }
    }

    this._singleCodeSymbol = -1;
    var maxCodeLen = codeLengths.Max();
    if (maxCodeLen == 0) {
      this._singleCodeSymbol = 0;
      this._codeTable = null;
      this._codeTableBits = 0;
    } else {
      this._codeTableBits = Math.Min(maxCodeLen, 16);
      this._codeTable = BuildDecodeTable(codeLengths, this._codeTableBits);
    }
  }

  /// <summary>
  /// Reads a PT-style tree (code-length tree or position tree).
  /// </summary>
  private int[]? ReadPtTree(int nBit, int specialBit) {
    var numSym = (int)this._bits.ReadBits(nBit);
    if (numSym == 0) {
      // Single symbol
      var sym = (int)this._bits.ReadBits(nBit);
      var lengths = new int[sym + 1];
      lengths[sym] = 1; // mark as used
      return lengths;
    }

    var codeLengths = new int[numSym];
    for (var i = 0; i < numSym; ++i) {
      var len = (int)this._bits.ReadBits(3);
      if (len == 7) {
        // Unary extension: read '1' bits, then a '0' bit
        while (this._bits.ReadBits(1) == 1)
          ++len;
      }
      codeLengths[i] = len;

      // After index 2 in T tree (specialBit==3): read 2-bit skip count
      if (i == 2 && specialBit == 3) {
        var skip = (int)this._bits.ReadBits(2);
        for (var j = 0; j < skip && i + 1 < numSym; ++j) {
          ++i;
          codeLengths[i] = 0;
        }
      }
    }

    return codeLengths;
  }

  /// <summary>
  /// Reads the P tree (position codes) using standard LZH encoding.
  /// </summary>
  private void ReadPTree() {
    var ptLengths = this.ReadPtTree(this._pBit, this._pBit);

    if (ptLengths == null) {
      this._singlePosSymbol = 0;
      this._posTable = null;
      this._posTableBits = 0;
      return;
    }

    // Check for single symbol
    var usedCount = 0;
    var singleSym = -1;
    for (var i = 0; i < ptLengths.Length; ++i)
      if (ptLengths[i] > 0) { singleSym = i; ++usedCount; }

    if (usedCount <= 1) {
      this._singlePosSymbol = usedCount > 0 ? singleSym : 0;
      this._posTable = null;
      this._posTableBits = 0;
    } else {
      this._singlePosSymbol = -1;
      var maxLen = ptLengths.Max();
      this._posTableBits = Math.Min(maxLen, 16);
      this._posTable = BuildDecodeTable(ptLengths, this._posTableBits);
    }
  }

  private static int DecodeFromTableStatic(BitBuffer<MsbBitOrder> bits, int[] table, int maxBits) {
    if (!bits.EnsureBits(maxBits)) {
      var avail = bits.BitsAvailable;
      if (avail == 0) return 0;
      var peekBits = bits.PeekBits(avail) << (maxBits - avail);
      var entry = table[(int)peekBits];
      if (entry < 0) return 0;
      bits.DropBits(entry >> 16);
      return entry & 0xFFFF;
    }
    var peek = bits.PeekBits(maxBits);
    var e = table[(int)peek];
    if (e < 0) return 0;
    bits.DropBits(e >> 16);
    return e & 0xFFFF;
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
    if (!this._bits.EnsureBits(maxBits)) {
      // Near end of stream: the last token's code may be shorter than tableBits.
      // Peek available bits and zero-pad to table width (MSB-first: pad at bottom).
      // This is standard for Huffman table decoders — the fill entries ensure any
      // padding pattern still maps to the correct shorter code.
      var avail = this._bits.BitsAvailable;
      if (avail == 0)
        throw new EndOfStreamException("No bits available for Huffman decode.");

      var peekBits = this._bits.PeekBits(avail) << (maxBits - avail);
      var entry = table[(int)peekBits];
      if (entry < 0)
        throw new InvalidDataException("Failed to decode Huffman symbol.");

      var codeLen = entry >> 16;
      // The encoder wrote codeLen bits; they must all be available.
      this._bits.DropBits(codeLen);
      return entry & 0xFFFF;
    }

    var peek = this._bits.PeekBits(maxBits);
    var e = table[(int)peek];
    if (e < 0)
      throw new InvalidDataException("Failed to decode Huffman symbol.");

    var len = e >> 16;
    this._bits.DropBits(len);
    return e & 0xFFFF;
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
