namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// Decompresses data encoded with the LZMS algorithm (used in WIM/ESD).
/// </summary>
/// <remarks>
/// LZMS uses two interleaved bitstreams: a forward bitstream for Huffman codes
/// and a backward bitstream for range-coded match/literal type decisions.
/// Huffman tables are adaptive (rebuilt periodically).
/// </remarks>
public sealed class LzmsDecompressor {
  // Range decoder state (reads backward from end)
  private byte[] _input = [];
  private int _rcPos;  // Current position reading backward
  private uint _rcRange;
  private uint _rcCode;

  // Forward bitstream (reads forward from start)
  private int _fwdPos; // Byte position reading forward
  private ulong _fwdBitBuf;
  private int _fwdBitsAvail;

  // Adaptive probabilities for range coder decisions
  private int _probLzMatch = LzmsConstants.InitialProb;
  private readonly int[] _probLzRepeat = new int[LzmsConstants.NumRecentLzOffsets];

  // Delta match state
  private int _probDeltaMatch = LzmsConstants.InitialProb;
  private readonly int[] _probDeltaRepeat = new int[LzmsConstants.NumRecentDeltaOffsets];

  // Recent match offsets
  private readonly long[] _recentLzOffsets = [1, 1, 1];

  // Recent delta match state: (power, offset) pairs
  private readonly int[] _recentDeltaPower = [0, 0, 0];
  private readonly long[] _recentDeltaOffset = [1, 1, 1];

  // Huffman tables (adaptive, rebuilt periodically)
  private int[] _literalFreqs = new int[LzmsConstants.NumLiteralSymbols];
  private int _literalCount;
  private int[] _literalDecodeTable = [];
  private int _literalTableBits;

  private int[] _lzOffsetFreqs = new int[LzmsConstants.NumLzOffsetSlots];
  private int _lzOffsetCount;
  private int[] _lzOffsetDecodeTable = [];
  private int _lzOffsetTableBits;

  private int[] _lengthFreqs = new int[LzmsConstants.NumLengthSymbols];
  private int _lengthCount;
  private int[] _lengthDecodeTable = [];
  private int _lengthTableBits;

  // Delta Huffman tables
  private int[] _deltaPowerFreqs = new int[LzmsConstants.NumDeltaPowerSymbols];
  private int _deltaPowerCount;
  private int[] _deltaPowerDecodeTable = [];
  private int _deltaPowerTableBits;

  private int[] _deltaOffsetFreqs = new int[LzmsConstants.NumDeltaOffsetSlots];
  private int _deltaOffsetCount;
  private int[] _deltaOffsetDecodeTable = [];
  private int _deltaOffsetTableBits;

  /// <summary>
  /// Decompresses LZMS data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="uncompressedSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedSize) {
    if (uncompressedSize == 0) return [];
    if (compressed.Length == 0)
      throw new InvalidDataException("LZMS compressed data is empty.");

    var output = new byte[uncompressedSize];
    this._input = compressed.ToArray();

    // Initialize range decoder (reads from end backward)
    InitializeRangeDecoder();

    // Initialize forward bitstream (reads from start)
    this._fwdPos = 0;
    this._fwdBitBuf = 0;
    this._fwdBitsAvail = 0;

    // Initialize probabilities
    this._probLzMatch = LzmsConstants.InitialProb;
    this._probDeltaMatch = LzmsConstants.InitialProb;
    Array.Fill(this._probLzRepeat, LzmsConstants.InitialProb);
    Array.Fill(this._probDeltaRepeat, LzmsConstants.InitialProb);

    // Initialize Huffman tables with uniform frequencies
    Array.Fill(this._literalFreqs, 1);
    this._literalCount = 0;
    RebuildHuffmanTable(this._literalFreqs, LzmsConstants.NumLiteralSymbols,
      out this._literalDecodeTable, out this._literalTableBits);

    Array.Fill(this._lzOffsetFreqs, 1);
    this._lzOffsetCount = 0;
    RebuildHuffmanTable(this._lzOffsetFreqs, LzmsConstants.NumLzOffsetSlots,
      out this._lzOffsetDecodeTable, out this._lzOffsetTableBits);

    Array.Fill(this._lengthFreqs, 1);
    this._lengthCount = 0;
    RebuildHuffmanTable(this._lengthFreqs, LzmsConstants.NumLengthSymbols,
      out this._lengthDecodeTable, out this._lengthTableBits);

    Array.Fill(this._deltaPowerFreqs, 1);
    this._deltaPowerCount = 0;
    RebuildHuffmanTable(this._deltaPowerFreqs, LzmsConstants.NumDeltaPowerSymbols,
      out this._deltaPowerDecodeTable, out this._deltaPowerTableBits);

    Array.Fill(this._deltaOffsetFreqs, 1);
    this._deltaOffsetCount = 0;
    RebuildHuffmanTable(this._deltaOffsetFreqs, LzmsConstants.NumDeltaOffsetSlots,
      out this._deltaOffsetDecodeTable, out this._deltaOffsetTableBits);

    var outPos = 0;
    while (outPos < uncompressedSize) {
      // Decide: literal or match? (range-coded bit)
      if (!RangeDecodeBit(ref this._probLzMatch)) {
        // Literal
        var sym = DecodeHuffman(this._literalDecodeTable, this._literalTableBits,
          LzmsConstants.NumLiteralSymbols);
        output[outPos++] = (byte)sym;

        ++this._literalFreqs[sym];
        if (++this._literalCount >= LzmsConstants.LiteralRebuildInterval) {
          RebuildHuffmanTable(this._literalFreqs, LzmsConstants.NumLiteralSymbols,
            out this._literalDecodeTable, out this._literalTableBits);
          HalveFrequencies(this._literalFreqs, LzmsConstants.NumLiteralSymbols);
          this._literalCount = 0;
        }
      } else {
        // Match: LZ or delta?
        if (!RangeDecodeBit(ref this._probDeltaMatch)) {
          // LZ match
          long offset;
          int length;

          // Check recent offsets first
          var usedRecent = false;
          for (var i = 0; i < LzmsConstants.NumRecentLzOffsets; ++i) {
            if (!RangeDecodeBit(ref this._probLzRepeat[i]))
              continue;

            offset = this._recentLzOffsets[i];
            for (var j = i; j > 0; --j)
              this._recentLzOffsets[j] = this._recentLzOffsets[j - 1];
            this._recentLzOffsets[0] = offset;

            length = DecodeMatchLength();
            CopyMatch(output, ref outPos, offset, length, uncompressedSize);
            usedRecent = true;
            break;
          }

          if (usedRecent)
            continue;

          // Explicit offset
          var offsetSlot = DecodeHuffman(this._lzOffsetDecodeTable, this._lzOffsetTableBits,
            LzmsConstants.NumLzOffsetSlots);
          offset = DecodeOffsetFromSlot(offsetSlot);

          ++this._lzOffsetFreqs[offsetSlot];
          if (++this._lzOffsetCount >= LzmsConstants.LzOffsetRebuildInterval) {
            RebuildHuffmanTable(this._lzOffsetFreqs, LzmsConstants.NumLzOffsetSlots,
              out this._lzOffsetDecodeTable, out this._lzOffsetTableBits);
            HalveFrequencies(this._lzOffsetFreqs, LzmsConstants.NumLzOffsetSlots);
            this._lzOffsetCount = 0;
          }

          this._recentLzOffsets[2] = this._recentLzOffsets[1];
          this._recentLzOffsets[1] = this._recentLzOffsets[0];
          this._recentLzOffsets[0] = offset;

          length = DecodeMatchLength();
          CopyMatch(output, ref outPos, offset, length, uncompressedSize);
        } else {
          // Delta match
          int power;
          long deltaOffset;
          int length;

          // Check recent delta offsets
          var usedRecent = false;
          for (var i = 0; i < LzmsConstants.NumRecentDeltaOffsets; ++i) {
            if (!RangeDecodeBit(ref this._probDeltaRepeat[i]))
              continue;

            power = this._recentDeltaPower[i];
            deltaOffset = this._recentDeltaOffset[i];
            for (var j = i; j > 0; --j) {
              this._recentDeltaPower[j] = this._recentDeltaPower[j - 1];
              this._recentDeltaOffset[j] = this._recentDeltaOffset[j - 1];
            }
            this._recentDeltaPower[0] = power;
            this._recentDeltaOffset[0] = deltaOffset;

            length = DecodeMatchLength();
            CopyDeltaMatch(output, ref outPos, power, deltaOffset, length, uncompressedSize);
            usedRecent = true;
            break;
          }

          if (usedRecent)
            continue;

          // Explicit delta: decode power and offset
          var powerSym = DecodeHuffman(this._deltaPowerDecodeTable, this._deltaPowerTableBits,
            LzmsConstants.NumDeltaPowerSymbols);
          power = powerSym;
          ++this._deltaPowerFreqs[powerSym];
          if (++this._deltaPowerCount >= LzmsConstants.DeltaPowerRebuildInterval) {
            RebuildHuffmanTable(this._deltaPowerFreqs, LzmsConstants.NumDeltaPowerSymbols,
              out this._deltaPowerDecodeTable, out this._deltaPowerTableBits);
            HalveFrequencies(this._deltaPowerFreqs, LzmsConstants.NumDeltaPowerSymbols);
            this._deltaPowerCount = 0;
          }

          var deltaSlot = DecodeHuffman(this._deltaOffsetDecodeTable, this._deltaOffsetTableBits,
            LzmsConstants.NumDeltaOffsetSlots);
          deltaOffset = DecodeOffsetFromSlot(deltaSlot);
          ++this._deltaOffsetFreqs[deltaSlot];
          if (++this._deltaOffsetCount >= LzmsConstants.DeltaOffsetRebuildInterval) {
            RebuildHuffmanTable(this._deltaOffsetFreqs, LzmsConstants.NumDeltaOffsetSlots,
              out this._deltaOffsetDecodeTable, out this._deltaOffsetTableBits);
            HalveFrequencies(this._deltaOffsetFreqs, LzmsConstants.NumDeltaOffsetSlots);
            this._deltaOffsetCount = 0;
          }

          this._recentDeltaPower[2] = this._recentDeltaPower[1];
          this._recentDeltaPower[1] = this._recentDeltaPower[0];
          this._recentDeltaPower[0] = power;
          this._recentDeltaOffset[2] = this._recentDeltaOffset[1];
          this._recentDeltaOffset[1] = this._recentDeltaOffset[0];
          this._recentDeltaOffset[0] = deltaOffset;

          length = DecodeMatchLength();
          CopyDeltaMatch(output, ref outPos, power, deltaOffset, length, uncompressedSize);
        }
      }
    }

    return output;
  }

  private int DecodeMatchLength() {
    var sym = DecodeHuffman(this._lengthDecodeTable, this._lengthTableBits,
      LzmsConstants.NumLengthSymbols);

    ++this._lengthFreqs[sym];
    if (++this._lengthCount >= LzmsConstants.LengthRebuildInterval) {
      RebuildHuffmanTable(this._lengthFreqs, LzmsConstants.NumLengthSymbols,
        out this._lengthDecodeTable, out this._lengthTableBits);
      HalveFrequencies(this._lengthFreqs, LzmsConstants.NumLengthSymbols);
      this._lengthCount = 0;
    }

    var length = LzmsConstants.LengthBase[sym];
    var extraBits = LzmsConstants.LengthExtraBits[sym];
    if (extraBits > 0)
      length += (int)ReadForwardBits(extraBits);
    return length;
  }

  private long DecodeOffsetFromSlot(int slot) {
    if (slot < 2)
      return slot + 1;
    var extraBits = (slot - 2) / 2;
    var baseOffset = (long)((2 + (slot & 1)) << extraBits);
    var extra = extraBits > 0 ? (long)ReadForwardBits(extraBits) : 0;
    return baseOffset + extra + 1; // +1 because offset is 1-based
  }

  private static void CopyMatch(byte[] output, ref int outPos, long offset, int length, int limit) {
    var srcStart = outPos - (int)offset;
    if (srcStart < 0)
      throw new InvalidDataException("LZMS match offset exceeds output buffer.");
    for (var i = 0; i < length && outPos < limit; ++i)
      output[outPos++] = output[srcStart + i];
  }

  /// <summary>
  /// Copies a delta match. Delta matching finds positions where the byte-level
  /// differences (at stride 2^power) repeat at the given offset.
  /// output[pos] = output[pos - span] + (output[pos - offset] - output[pos - offset - span])
  /// where span = 1 &lt;&lt; power.
  /// </summary>
  private static void CopyDeltaMatch(byte[] output, ref int outPos, int power, long deltaOffset,
      int length, int limit) {
    var span = 1 << power;
    var srcOffset = (int)deltaOffset + span;
    for (var i = 0; i < length && outPos < limit; ++i) {
      var prevAtSpan = outPos - span >= 0 ? output[outPos - span] : (byte)0;
      var srcPos = outPos - srcOffset;
      var srcPrev = srcPos - span;
      var matchByte = srcPos >= 0 ? output[srcPos] : (byte)0;
      var matchPrev = srcPrev >= 0 ? output[srcPrev] : (byte)0;
      output[outPos++] = (byte)(prevAtSpan + matchByte - matchPrev);
    }
  }

  // Range decoder methods (backward bitstream)
  private void InitializeRangeDecoder() {
    this._rcPos = this._input.Length;
    this._rcRange = 0xFFFFFFFF;
    this._rcCode = 0;
    // Read initial 4 bytes backward
    for (var i = 0; i < 4; ++i) {
      this._rcCode <<= 8;
      if (this._rcPos > 0)
        this._rcCode |= this._input[--this._rcPos];
    }
  }

  private bool RangeDecodeBit(ref int prob) {
    var bound = (this._rcRange >> LzmsConstants.NumProbBits) * (uint)prob;
    bool bit;
    if (this._rcCode < bound) {
      this._rcRange = bound;
      prob += (LzmsConstants.ProbDenominator - prob) >> 4;
      bit = false;
    } else {
      this._rcCode -= bound;
      this._rcRange -= bound;
      prob -= prob >> 4;
      bit = true;
    }

    // Normalize
    while (this._rcRange < 0x10000) {
      this._rcRange <<= 16;
      this._rcCode <<= 16;
      if (this._rcPos >= 2) {
        this._rcCode |= (uint)(this._input[this._rcPos - 2] | (this._input[this._rcPos - 1] << 8));
        this._rcPos -= 2;
      }
    }

    return bit;
  }

  // Forward bitstream methods (MSB-first, reading forward)
  private void EnsureForwardBits(int count) {
    while (this._fwdBitsAvail < count) {
      if (this._fwdPos < this._input.Length) {
        this._fwdBitBuf |= (ulong)this._input[this._fwdPos++] << (56 - this._fwdBitsAvail);
        this._fwdBitsAvail += 8;
      } else {
        this._fwdBitsAvail += 8; // Pad with zeros
      }
    }
  }

  private ulong ReadForwardBits(int count) {
    EnsureForwardBits(count);
    var result = this._fwdBitBuf >> (64 - count);
    this._fwdBitBuf <<= count;
    this._fwdBitsAvail -= count;
    return result;
  }

  private int DecodeHuffman(int[] decodeTable, int tableBits, int numSymbols) {
    if (decodeTable.Length == 0 || tableBits == 0)
      return 0;

    EnsureForwardBits(tableBits);
    var peek = (int)(this._fwdBitBuf >> (64 - tableBits));
    if (peek < decodeTable.Length) {
      var entry = decodeTable[peek];
      var sym = entry & 0xFFFF;
      var len = entry >> 16;
      if (len < 1) len = tableBits; // Fallback
      // Consume only the actual code length, not the full tableBits
      this._fwdBitBuf <<= len;
      this._fwdBitsAvail -= len;
      return sym < numSymbols ? sym : 0;
    }
    // Consume all bits on error
    this._fwdBitBuf <<= tableBits;
    this._fwdBitsAvail -= tableBits;
    return 0;
  }

  private static void RebuildHuffmanTable(int[] freqs, int numSymbols,
      out int[] decodeTable, out int tableBits) {
    // Build code lengths from frequencies using package-merge or simple heuristic
    var codeLens = BuildCodeLengthsFromFreqs(freqs, numSymbols, 15);

    var maxLen = 0;
    for (var i = 0; i < numSymbols; ++i)
      maxLen = Math.Max(maxLen, codeLens[i]);

    if (maxLen == 0) {
      tableBits = 1;
      decodeTable = [0, 0];
      return;
    }

    tableBits = Math.Min(maxLen, 12); // Cap table bits for memory
    var tableSize = 1 << tableBits;
    decodeTable = new int[tableSize];

    // Build canonical codes
    var blCount = new int[maxLen + 1];
    for (var i = 0; i < numSymbols; ++i)
      if (codeLens[i] > 0)
        ++blCount[codeLens[i]];

    var nextCode = new int[maxLen + 1];
    var code = 0;
    for (var bits = 1; bits <= maxLen; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    // Fill decode table
    for (var sym = 0; sym < numSymbols; ++sym) {
      var len = codeLens[sym];
      if (len <= 0 || len > tableBits)
        continue;

      var c = nextCode[len]++;
      var prefix = c << (tableBits - len);
      var fill = 1 << (tableBits - len);
      var entry = sym | (len << 16);
      for (var j = 0; j < fill && prefix + j < tableSize; ++j)
        decodeTable[prefix + j] = entry;
    }
  }

  private static int[] BuildCodeLengthsFromFreqs(int[] freqs, int numSymbols, int maxLen) {
    var codeLens = new int[numSymbols];

    // Count non-zero symbols
    var nonZero = 0;
    for (var i = 0; i < numSymbols; ++i)
      if (freqs[i] > 0)
        ++nonZero;

    if (nonZero <= 1) {
      for (var i = 0; i < numSymbols; ++i)
        if (freqs[i] > 0)
          codeLens[i] = 1;
      return codeLens;
    }

    // Assign lengths in symbol order (must match compressor exactly)
    var bitsNeeded = 1;
    while ((1 << bitsNeeded) < nonZero)
      ++bitsNeeded;

    bitsNeeded = Math.Min(bitsNeeded, maxLen);

    var shortCount = (1 << bitsNeeded) - nonZero;
    var assigned = 0;
    for (var i = 0; i < numSymbols; ++i) {
      if (freqs[i] <= 0) continue;
      codeLens[i] = assigned < shortCount && bitsNeeded > 1 ? bitsNeeded - 1 : bitsNeeded;
      ++assigned;
    }

    return codeLens;
  }

  private static void HalveFrequencies(int[] freqs, int count) {
    for (var i = 0; i < count; ++i)
      freqs[i] = Math.Max(1, (freqs[i] + 1) >> 1);
  }
}
