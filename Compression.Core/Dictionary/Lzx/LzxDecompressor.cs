using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Lzx;

/// <summary>
/// Decompresses data encoded with the LZX algorithm (as used in Microsoft CAB and WIM formats).
/// </summary>
/// <remarks>
/// LZX uses a unique bit-reading scheme: 16-bit little-endian words are read from the
/// stream and bits are consumed MSB-first from each word. This is distinct from both
/// standard LSB and MSB byte-oriented schemes, and is handled by a custom inline reader.
///
/// <para>Key features:</para>
/// <list type="bullet">
///   <item>Sliding window sizes 32 KB – 2 MB (configurable power-of-two).</item>
///   <item>Three block types: verbatim, aligned offset, and uncompressed.</item>
///   <item>Three Huffman trees: main (literals + match headers), length, aligned offset.</item>
///   <item>Pre-tree delta coding for Huffman code lengths (persisted across blocks).</item>
///   <item>Three repeated match offsets (R0, R1, R2) maintained across blocks.</item>
///   <item>Optional E8 x86 CALL instruction post-processing.</item>
/// </list>
/// </remarks>
public sealed class LzxDecompressor {
  private readonly int _windowBits;
  private readonly int _windowSize;
  private readonly int _numPositionSlots;
  private readonly int _numMainSymbols;

  // Sliding window
  private readonly byte[] _window;
  private int _windowPos;

  // Repeated match offsets (1-based distances), per LZX spec initialised to 1
  private int _r0 = 1;
  private int _r1 = 1;
  private int _r2 = 1;

  // Huffman code lengths — persisted across blocks for delta decoding
  private readonly int[] _mainLengths;
  private readonly int[] _lengthLengths;
  private readonly int[] _alignedLengths;

  // Decode tables built once per block
  private int[]? _mainTable;
  private int[]? _lengthTable;
  private int[]? _alignedTable;

  // Table sizes (capped to limit memory while still covering all practical codes)
  private const int MainTableBits   = 12;
  private const int LengthTableBits = 12;
  private const int AlignedTableBits = 7;

  // LZX bit reader state
  private readonly Stream _input;
  private uint _bitBuffer;
  private int _bitsLeft;
  private bool _endOfStream;

  // E8 call-instruction translation state
  private readonly bool _e8Translation;
  private long _totalDecompressedBytes;

  /// <summary>
  /// Initializes a new <see cref="LzxDecompressor"/>.
  /// </summary>
  /// <param name="input">The stream containing LZX-compressed data.</param>
  /// <param name="windowBits">
  /// The window size exponent (15–21). Window size = 2^<paramref name="windowBits"/>.
  /// </param>
  /// <param name="e8Translation">
  /// Whether to reverse E8/E9 x86 CALL/JMP instruction pre-processing
  /// (used by WIM; not used by CAB).
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> is null.</exception>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="windowBits"/> is outside [15, 21].
  /// </exception>
  public LzxDecompressor(Stream input, int windowBits = 15, bool e8Translation = false) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentOutOfRangeException.ThrowIfLessThan(windowBits, LzxConstants.MinWindowBits);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(windowBits, LzxConstants.MaxWindowBits);

    this._input = input;
    this._windowBits = windowBits;
    this._windowSize = 1 << windowBits;
    this._numPositionSlots = LzxConstants.GetPositionSlotCount(windowBits);
    this._numMainSymbols = LzxConstants.NumChars + this._numPositionSlots * LzxConstants.NumLengthHeaders;

    this._window = new byte[this._windowSize];
    this._mainLengths = new int[this._numMainSymbols];
    this._lengthLengths = new int[LzxConstants.NumLengthSymbols];
    this._alignedLengths = new int[LzxConstants.NumAlignedSymbols];

    this._e8Translation = e8Translation;
  }

  /// <summary>
  /// Decompresses the next <paramref name="uncompressedSize"/> bytes from the input stream.
  /// </summary>
  /// <param name="uncompressedSize">The number of bytes to decompress.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">Thrown when the compressed data is malformed.</exception>
  public byte[] Decompress(int uncompressedSize) {
    var output = new byte[uncompressedSize];
    var outPos = 0;

    while (outPos < uncompressedSize) {
      var blockType = (int)this.ReadBits(3);

      // Block size: 1-bit "use default 32768" flag, or 16-bit explicit size
      int blockSize;
      if (this.ReadBits(1) == 1)
        blockSize = LzxConstants.DefaultBlockSize;
      else
        blockSize = (int)this.ReadBits(16);

      blockSize = Math.Min(blockSize, uncompressedSize - outPos);

      switch (blockType) {
        case LzxConstants.BlockTypeVerbatim:
          this.ReadVerbatimBlockHeader();
          this.DecodeBlock(false, output, outPos, blockSize);
          break;

        case LzxConstants.BlockTypeAligned:
          this.ReadAlignedBlockHeader();
          this.DecodeBlock(true, output, outPos, blockSize);
          break;

        case LzxConstants.BlockTypeUncompressed:
          this.DecodeUncompressedBlock(output, outPos, blockSize);
          break;

        default:
          ThrowInvalidBlockType(blockType);
          break;
      }

      outPos += blockSize;
    }

    if (this._e8Translation && uncompressedSize > 10)
      ApplyE8Translation(output, this._totalDecompressedBytes);

    this._totalDecompressedBytes += uncompressedSize;
    return output;
  }

  // -------------------------------------------------------------------------
  // Block header reading
  // -------------------------------------------------------------------------

  private void ReadVerbatimBlockHeader() {
    this.ReadPreTreeAndApply(this._mainLengths, 0, LzxConstants.NumChars);
    this.ReadPreTreeAndApply(this._mainLengths, LzxConstants.NumChars,
      this._numMainSymbols - LzxConstants.NumChars);
    this.ReadPreTreeAndApply(this._lengthLengths, 0, LzxConstants.NumLengthSymbols);

    this._mainTable   = BuildDecodeTable(this._mainLengths,   this._numMainSymbols,       LzxDecompressor.MainTableBits);
    this._lengthTable = BuildDecodeTable(this._lengthLengths, LzxConstants.NumLengthSymbols, LzxDecompressor.LengthTableBits);
  }

  private void ReadAlignedBlockHeader() {
    // Aligned offset tree: 8 symbols × 3 raw bits, no pre-tree
    for (var i = 0; i < LzxConstants.NumAlignedSymbols; ++i)
      this._alignedLengths[i] = (int)this.ReadBits(3);

    this._alignedTable = BuildDecodeTable(this._alignedLengths, LzxConstants.NumAlignedSymbols, LzxDecompressor.AlignedTableBits);

    // Remainder identical to verbatim header
    this.ReadVerbatimBlockHeader();
  }

  private void DecodeUncompressedBlock(byte[] output, int outPos, int blockSize) {
    // Discard partial bits to align to the next 16-bit word boundary
    this.AlignTo16Bits();

    // R0/R1/R2 are stored as three LE 32-bit integers
    this._r0 = this.ReadRawInt32LE();
    this._r1 = this.ReadRawInt32LE();
    this._r2 = this.ReadRawInt32LE();

    // Copy raw bytes
    var bytesRead = this._input.Read(output, outPos, blockSize);
    if (bytesRead < blockSize)
      ThrowUnexpectedEndOfStream();

    // If block length is odd, consume one padding byte so stream stays word-aligned
    if ((blockSize & 1) != 0)
      this._input.ReadByte();

    // Mirror into the circular window
    for (var i = 0; i < blockSize; ++i) {
      this._window[this._windowPos] = output[outPos + i];
      this._windowPos = (this._windowPos + 1) & (this._windowSize - 1);
    }
  }

  // -------------------------------------------------------------------------
  // Pre-tree delta decoding
  // -------------------------------------------------------------------------

  private void ReadPreTreeAndApply(int[] lengths, int start, int count) {
    // Read the 20 pre-tree code lengths (4 bits each)
    var preLengths = new int[LzxConstants.NumPreTreeSymbols];
    for (var i = 0; i < LzxConstants.NumPreTreeSymbols; ++i)
      preLengths[i] = (int)this.ReadBits(LzxConstants.PreTreeBits);

    var preTable = BuildDecodeTable(preLengths, LzxConstants.NumPreTreeSymbols, 6);

    var pos = start;
    var end = start + count;
    while (pos < end) {
      var sym = this.DecodeSymbol(preTable, 6, preLengths, LzxConstants.NumPreTreeSymbols);

      switch (sym) {
        case < 17:
          // Delta: new_len = (prev_len - delta + 17) mod 17
          lengths[pos] = (lengths[pos] - sym + 17) % 17;
          ++pos;
          break;

        case 17: {
          // Short zero-delta run: 4 extra bits → 4..19 positions with delta = 0 (no change)
          var runLen = 4 + (int)this.ReadBits(4);
          while (runLen-- > 0 && pos < end)
            ++pos; // delta = 0: length is unchanged

          break;
        }

        case 18: {
          // Long zero-delta run: 5 extra bits → 20..51 positions with delta = 0 (no change)
          var runLen = 20 + (int)this.ReadBits(5);
          while (runLen-- > 0 && pos < end)
            ++pos; // delta = 0: length is unchanged

          break;
        }

        case 19: {
          // Same-value run: 1 extra bit → 4..5 repetitions of the next decoded delta
          var runLen = 4 + (int)this.ReadBits(1);
          var nextSym = this.DecodeSymbol(preTable, 6, preLengths, LzxConstants.NumPreTreeSymbols);
          var newLen = (lengths[pos] - nextSym + 17) % 17;
          while (runLen-- > 0 && pos < end)
            lengths[pos++] = newLen;

          break;
        }

        default: ThrowInvalidPreTreeSymbol(sym); break;
      }
    }
  }

  // -------------------------------------------------------------------------
  // Block decoding
  // -------------------------------------------------------------------------

  private void DecodeBlock(bool isAligned, byte[] output, int outPos, int blockSize) {
    var end = outPos + blockSize;
    while (outPos < end) {
      var mainSym = this.DecodeSymbol(this._mainTable!, LzxDecompressor.MainTableBits, this._mainLengths, this._numMainSymbols);

      if (mainSym < LzxConstants.NumChars) {
        // Literal byte
        var b = (byte)mainSym;
        output[outPos++] = b;
        this._window[this._windowPos] = b;
        this._windowPos = (this._windowPos + 1) & (this._windowSize - 1);
      } else {
        // Match
        var matchSym      = mainSym - LzxConstants.NumChars;
        var positionSlot  = matchSym / LzxConstants.NumLengthHeaders;
        var lengthHeader  = matchSym % LzxConstants.NumLengthHeaders;

        // Decode match length
        var matchLength = lengthHeader + LzxConstants.MinMatch;
        if (lengthHeader == LzxConstants.NumLengthHeaders - 1) {
          var lenSym = this.DecodeSymbol(this._lengthTable!, LzxDecompressor.LengthTableBits, this._lengthLengths, LzxConstants.NumLengthSymbols);
          matchLength = LzxConstants.NumLengthHeaders - 1 + LzxConstants.MinMatch + lenSym;
        }

        // Decode match offset from position slot
        var matchOffset = this.DecodeMatchOffset(isAligned, positionSlot);

        // Copy from sliding window (handles run-length / overlapping copies)
        var srcPos = (this._windowPos - matchOffset + this._windowSize) & (this._windowSize - 1);
        for (var i = 0; i < matchLength && outPos < end; ++i) {
          var b = this._window[srcPos];
          output[outPos++] = b;
          this._window[this._windowPos] = b;
          this._windowPos = (this._windowPos + 1) & (this._windowSize - 1);
          srcPos = (srcPos + 1) & (this._windowSize - 1);
        }
      }
    }
  }

  private int DecodeMatchOffset(bool isAligned, int positionSlot) {
    switch (positionSlot) {
      case 0: 
        return this._r0;
      case 1:
        (this._r0, this._r1) = (this._r1, this._r0);
        return this._r0;

      case 2:
        (this._r0, this._r2) = (this._r2, this._r0);
        return this._r0;
    }

    // Non-repeat: decode formatted offset = base + footer
    LzxConstants.GetSlotInfo(positionSlot, out var baseOffset, out var footerBits);

    int footer;
    if (isAligned && footerBits >= 3) {
      // Aligned block: split footer into (footerBits-3) verbatim bits + 3 aligned bits
      var verbatimBits  = footerBits - 3;
      var verbatimValue = verbatimBits > 0 ? (int)this.ReadBits(verbatimBits) << 3 : 0;
      var alignedSym    = this.DecodeSymbol(this._alignedTable!, LzxDecompressor.AlignedTableBits, this._alignedLengths, LzxConstants.NumAlignedSymbols);
      footer = verbatimValue | alignedSym;
    } else
      footer = footerBits > 0 ? (int)this.ReadBits(footerBits) : 0;

    // Actual match offset = formatted_offset + 2 (LZX bias)
    var matchOffset = baseOffset + footer + 2;
    (this._r2, this._r1, this._r0) = (this._r1, this._r0, matchOffset);
    return matchOffset;
  }

  // -------------------------------------------------------------------------
  // Huffman decode table construction and symbol decoding
  // -------------------------------------------------------------------------

  /// <summary>
  /// Builds a flat-array Huffman decode table for fast lookup.
  /// Each non-negative entry encodes both the symbol and the code length:
  /// <c>entry = symbol | (codeLength &lt;&lt; 16)</c>.
  /// Entries are replicated for all table indices that share the same code prefix.
  /// </summary>
  private static int[] BuildDecodeTable(int[] lengths, int numSymbols, int tableBits) {
    var tableSize = 1 << tableBits;
    var table = new int[tableSize];
    table.AsSpan().Fill(-1);
    
    // Count symbols per code length
    var blCount = new int[LzxConstants.MaxHuffmanBits + 1];
    for (var i = 0; i < numSymbols; ++i)
      if (lengths[i] > 0 && lengths[i] <= LzxConstants.MaxHuffmanBits)
        ++blCount[lengths[i]];

    // Compute the first code for each length (canonical MSB-first assignment)
    var nextCode = new int[LzxConstants.MaxHuffmanBits + 1];
    var code = 0;
    for (var bits = 1; bits <= LzxConstants.MaxHuffmanBits; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    // Count how many symbols have non-zero code lengths
    var usedCount = 0;
    var singleSym = -1;
    for (var i = 0; i < numSymbols; ++i) {
      if (lengths[i] <= 0 || lengths[i] > LzxConstants.MaxHuffmanBits)
        continue;

      ++usedCount;
      singleSym = i;
    }

    // Single-symbol tree: map ALL entries to that symbol (canonical code 0 only
    // covers half the table, but there is no valid alternative symbol)
    if (usedCount == 1) {
      var entry = singleSym | (lengths[singleSym] << 16);
      table.AsSpan().Fill(entry);
      return table;
    }

    // Fill the lookup table
    for (var sym = 0; sym < numSymbols; ++sym) {
      var len = lengths[sym];
      if (len is 0 or > LzxConstants.MaxHuffmanBits)
        continue;

      var symCode = nextCode[len]++;
      if (len > tableBits)
        continue;

      // Replicate across all table entries that share this prefix
      var fillBits  = tableBits - len;
      var baseIdx   = symCode << fillBits;
      var entry     = sym | (len << 16);
      var fillCount = 1 << fillBits;
      table.AsSpan(baseIdx, fillCount).Fill(entry);
      // Symbols with len > tableBits are handled by the linear fallback in DecodeSymbol
    }

    return table;
  }

  /// <summary>
  /// Decodes one Huffman symbol using the prebuilt table, with a bit-by-bit
  /// fallback for symbols whose code length exceeds <paramref name="tableBits"/>.
  /// </summary>
  private int DecodeSymbol(int[] table, int tableBits, int[] lengths, int numSymbols) {
    this.EnsureBits(tableBits);
    var peek   = this.PeekBits(tableBits);
    var  entry  = table[(int)peek];

    if (entry >= 0) {
      var codeLen = entry >> 16;
      this.RemoveBits(codeLen);
      return entry & 0xFFFF;
    }

    // Fallback: bit-by-bit scan for codes longer than tableBits
    // Reconstruct canonical code assignment and match bit-by-bit.
    this.EnsureBits(LzxConstants.MaxHuffmanBits);

    var blCount = new int[LzxConstants.MaxHuffmanBits + 1];
    for (var i = 0; i < numSymbols; ++i)
      if (lengths[i] > 0 && lengths[i] <= LzxConstants.MaxHuffmanBits)
        ++blCount[lengths[i]];

    var accCode    = 0;
    var firstCode  = 0;
    var symbolBase = 0;

    for (var len = 1; len <= LzxConstants.MaxHuffmanBits; ++len) {
      firstCode = (firstCode + (len == 1 ? 0 : blCount[len - 1])) << 1;
      // Accumulate one more bit (MSB-first peek of exactly 'len' bits)
      accCode = (int)this.PeekBits(len);

      // Walk through symbols of this length to find a match
      var symIdx = symbolBase;
      for (var i = 0; i < numSymbols; ++i) {
        if (lengths[i] != len) continue;
        if (firstCode + (symIdx - symbolBase) == accCode) {
          this.RemoveBits(len);
          return i;
        }
        ++symIdx;
      }

      symbolBase += blCount[len];
    }

    ThrowInvalidHuffmanCode();
    return -1; // unreachable
  }

  // -------------------------------------------------------------------------
  // LZX bit reader (16-bit LE words, MSB-first consumption)
  // -------------------------------------------------------------------------

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void EnsureBits(int count) {
    while (this._bitsLeft < count && !this._endOfStream)
      this.FillBitBuffer();
  }

  private void FillBitBuffer() {
    var lo = this._input.ReadByte();
    var hi = this._input.ReadByte();

    if (lo < 0) {
      // Stream exhausted; we have no more bits to add.
      // Any decoding that still needs bits will fail later with a proper error.
      this._endOfStream = true;
      return;
    }

    if (hi < 0)
      hi = 0; // Treat a half-word at the very end as zero-padded

    var word = (uint)((hi << 8) | lo); // 16-bit LE word
    this._bitBuffer = (this._bitBuffer << 16) | word;
    this._bitsLeft += 16;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private uint PeekBits(int count) {
    var shift = this._bitsLeft - count;
    return (this._bitBuffer >> shift) & ((1u << count) - 1);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void RemoveBits(int count) => this._bitsLeft -= count;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private uint ReadBits(int count) {
    this.EnsureBits(count);
    var v = this.PeekBits(count);
    this.RemoveBits(count);
    return v;
  }

  /// <summary>
  /// Discards bits to align the bit reader to the next 16-bit word boundary.
  /// </summary>
  private void AlignTo16Bits() {
    var mod = this._bitsLeft & 15;
    if (mod != 0)
      this.RemoveBits(mod);
  }

  /// <summary>
  /// Reads a 32-bit LE integer directly from the underlying byte stream,
  /// bypassing the bit buffer. Call only after <see cref="AlignTo16Bits"/>.
  /// </summary>
  private int ReadRawInt32LE() {
    var b0 = this._input.ReadByte();
    var b1 = this._input.ReadByte();
    var b2 = this._input.ReadByte();
    var b3 = this._input.ReadByte();
    if (b0 < 0 || b1 < 0 || b2 < 0 || b3 < 0)
      ThrowUnexpectedEndOfStream();

    return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
  }

  // -------------------------------------------------------------------------
  // E8 call-instruction translation (WIM post-processing)
  // -------------------------------------------------------------------------

  /// <summary>
  /// Reverses E8/E9 x86 CALL/JMP pre-processing applied before LZX compression
  /// (used by WIM format). Scans for 0xE8 and 0xE9 opcodes and translates the
  /// following 4-byte relative address back from absolute form.
  /// </summary>
  private static void ApplyE8Translation(byte[] data, long fileOffset) {
    long limit = data.Length >= 6 ? data.Length - 5 : 0;
    for (long i = 0; i < limit; ++i) {
      if (data[i] != 0xE8 && data[i] != 0xE9)
        continue;

      var absAddr = data[i + 1] | ((long)data[i + 2] << 8) | ((long)data[i + 3] << 16) | ((long)data[i + 4] << 24);
      var curPos  = fileOffset + i + 1;

      // Reverse: if the stored value looks like it was translated, undo it
      // TODO: write 4 bytes at once
      if (absAddr is >= 0 and < 0x8000000) {
        var relAddr = absAddr - curPos;
        data[i + 1] = (byte)relAddr;
        data[i + 2] = (byte)(relAddr >> 8);
        data[i + 3] = (byte)(relAddr >> 16);
        data[i + 4] = (byte)(relAddr >> 24);
      } else if (absAddr >= unchecked((long)0xFFF8000000) && absAddr < 0x100000000L) {
        var relAddr = absAddr - curPos;
        data[i + 1] = (byte)relAddr;
        data[i + 2] = (byte)(relAddr >> 8);
        data[i + 3] = (byte)(relAddr >> 16);
        data[i + 4] = (byte)(relAddr >> 24);
      }

      i += 4;
    }
  }

  // -------------------------------------------------------------------------
  // Throw helpers
  // -------------------------------------------------------------------------

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidBlockType(int blockType) =>
    throw new InvalidDataException($"Invalid LZX block type: {blockType}.");

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidPreTreeSymbol(int sym) =>
    throw new InvalidDataException($"Invalid LZX pre-tree symbol: {sym}.");

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidHuffmanCode() =>
    throw new InvalidDataException("Invalid LZX Huffman code encountered during decoding.");

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowUnexpectedEndOfStream() =>
    throw new EndOfStreamException("Unexpected end of stream while reading LZX data.");
}
