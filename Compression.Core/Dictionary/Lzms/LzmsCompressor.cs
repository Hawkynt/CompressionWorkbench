using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// Compresses data using the LZMS algorithm (used in WIM/ESD).
/// </summary>
/// <remarks>
/// LZMS uses two interleaved bitstreams: a forward bitstream for Huffman codes
/// and a backward bitstream for range-coded match/literal type decisions.
/// Huffman tables are adaptive (rebuilt periodically).
/// </remarks>
public sealed class LzmsCompressor {
  // Range encoder state — outputs 16-bit LE words placed backward at end of buffer
  private readonly List<ushort> _rcWords = [];
  private uint _rcRange;
  private ulong _rcLow;

  // Forward bitstream (writes forward, MSB-first)
  private byte[] _fwdOutput = [];
  private int _fwdPos;
  private ulong _fwdBitBuf;
  private int _fwdBitsUsed;

  // Adaptive probabilities
  private int _probLzMatch = LzmsConstants.InitialProb;
  private readonly int[] _probLzRepeat = new int[LzmsConstants.NumRecentLzOffsets];

  // Delta match probabilities (not used in compressor, but tracked for symmetry)
  private int _probDeltaMatch = LzmsConstants.InitialProb;

  // Recent match offsets
  private readonly long[] _recentLzOffsets = [1, 1, 1];

  // Huffman frequencies (adaptive)
  private int[] _literalFreqs = new int[LzmsConstants.NumLiteralSymbols];
  private int _literalCount;
  private int[] _literalCodeLens = [];

  private int[] _lzOffsetFreqs = new int[LzmsConstants.NumLzOffsetSlots];
  private int _lzOffsetCount;
  private int[] _lzOffsetCodeLens = [];

  private int[] _lengthFreqs = new int[LzmsConstants.NumLengthSymbols];
  private int _lengthCount;
  private int[] _lengthCodeLens = [];

  /// <summary>
  /// Compresses data using LZMS.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return [];

    var dataArray = data.ToArray();

    // Allocate forward output buffer (worst case: slightly larger than input)
    var maxSize = data.Length + 256;
    _fwdOutput = new byte[maxSize];
    _fwdPos = 0;
    _fwdBitBuf = 0;
    _fwdBitsUsed = 0;

    // Initialize range encoder
    _rcWords.Clear();
    _rcRange = 0xFFFFFFFF;
    _rcLow = 0;

    // Initialize probabilities
    _probLzMatch = LzmsConstants.InitialProb;
    _probDeltaMatch = LzmsConstants.InitialProb;
    Array.Fill(_probLzRepeat, LzmsConstants.InitialProb);

    // Initialize Huffman frequencies
    Array.Fill(_literalFreqs, 1);
    _literalCount = 0;
    RebuildCodeLengths(_literalFreqs, LzmsConstants.NumLiteralSymbols, out _literalCodeLens);

    Array.Fill(_lzOffsetFreqs, 1);
    _lzOffsetCount = 0;
    RebuildCodeLengths(_lzOffsetFreqs, LzmsConstants.NumLzOffsetSlots, out _lzOffsetCodeLens);

    Array.Fill(_lengthFreqs, 1);
    _lengthCount = 0;
    RebuildCodeLengths(_lengthFreqs, LzmsConstants.NumLengthSymbols, out _lengthCodeLens);

    // Find matches
    var matchFinder = new HashChainMatchFinder(data.Length, 64);
    var pos = 0;

    while (pos < data.Length) {
      // Try to find an LZ match
      Match bestMatch = default;
      if (pos + 3 <= data.Length) {
        var maxDist = Math.Min(pos, data.Length);
        var maxLen = Math.Min(224, data.Length - pos);
        bestMatch = matchFinder.FindMatch(dataArray, pos, maxDist, maxLen, LzmsConstants.MinMatchLength);
      } else {
        if (pos + 2 < data.Length)
          matchFinder.InsertPosition(dataArray, pos);
      }

      if (bestMatch.Length >= LzmsConstants.MinMatchLength) {
        // Check recent offsets
        var recentIdx = -1;
        for (var i = 0; i < LzmsConstants.NumRecentLzOffsets; ++i) {
          if (_recentLzOffsets[i] != bestMatch.Distance)
            continue;
          recentIdx = i;
          break;
        }

        // Encode: match
        RangeEncodeBit(ref _probLzMatch, true);

        // Not a delta match
        RangeEncodeBit(ref _probDeltaMatch, false);

        if (recentIdx >= 0) {
          // Encode recent offset usage
          for (var i = 0; i < recentIdx; ++i)
            RangeEncodeBit(ref _probLzRepeat[i], false);
          RangeEncodeBit(ref _probLzRepeat[recentIdx], true);

          // Move to front
          var off = _recentLzOffsets[recentIdx];
          for (var j = recentIdx; j > 0; --j)
            _recentLzOffsets[j] = _recentLzOffsets[j - 1];
          _recentLzOffsets[0] = off;
        } else {
          // Not a recent offset
          for (var i = 0; i < LzmsConstants.NumRecentLzOffsets; ++i)
            RangeEncodeBit(ref _probLzRepeat[i], false);

          // Encode offset via Huffman
          var offsetSlot = EncodeOffsetToSlot(bestMatch.Distance);
          WriteHuffman(offsetSlot, _lzOffsetCodeLens, LzmsConstants.NumLzOffsetSlots);

          // Write extra bits for the offset
          WriteOffsetExtraBits(bestMatch.Distance, offsetSlot);

          ++_lzOffsetFreqs[offsetSlot];
          if (++_lzOffsetCount >= LzmsConstants.LzOffsetRebuildInterval) {
            RebuildCodeLengths(_lzOffsetFreqs, LzmsConstants.NumLzOffsetSlots, out _lzOffsetCodeLens);
            HalveFrequencies(_lzOffsetFreqs, LzmsConstants.NumLzOffsetSlots);
            _lzOffsetCount = 0;
          }

          // Update recent offsets
          _recentLzOffsets[2] = _recentLzOffsets[1];
          _recentLzOffsets[1] = _recentLzOffsets[0];
          _recentLzOffsets[0] = bestMatch.Distance;
        }

        // Encode match length
        EncodeMatchLength(bestMatch.Length);

        // Insert skipped positions
        for (var i = 1; i < bestMatch.Length && pos + i + 2 < data.Length; ++i)
          matchFinder.InsertPosition(dataArray, pos + i);
        pos += bestMatch.Length;
      } else {
        // Encode: literal
        RangeEncodeBit(ref _probLzMatch, false);
        WriteLiteral(dataArray[pos]);
        ++pos;
      }
    }

    FlushRangeEncoder();
    FlushForwardBits();
    return MergeStreams();
  }

  private void WriteLiteral(byte value) {
    WriteHuffman(value, _literalCodeLens, LzmsConstants.NumLiteralSymbols);
    ++_literalFreqs[value];
    if (++_literalCount >= LzmsConstants.LiteralRebuildInterval) {
      RebuildCodeLengths(_literalFreqs, LzmsConstants.NumLiteralSymbols, out _literalCodeLens);
      HalveFrequencies(_literalFreqs, LzmsConstants.NumLiteralSymbols);
      _literalCount = 0;
    }
  }

  private void EncodeMatchLength(int length) {
    var sym = 0;
    for (var i = LzmsConstants.LengthBase.Length - 1; i >= 0; --i) {
      if (length < LzmsConstants.LengthBase[i])
        continue;
      sym = i;
      break;
    }

    WriteHuffman(sym, _lengthCodeLens, LzmsConstants.NumLengthSymbols);

    var extraBits = LzmsConstants.LengthExtraBits[sym];
    if (extraBits > 0) {
      var extra = length - LzmsConstants.LengthBase[sym];
      WriteForwardBits(extra, extraBits);
    }

    ++_lengthFreqs[sym];
    if (++_lengthCount >= LzmsConstants.LengthRebuildInterval) {
      RebuildCodeLengths(_lengthFreqs, LzmsConstants.NumLengthSymbols, out _lengthCodeLens);
      HalveFrequencies(_lengthFreqs, LzmsConstants.NumLengthSymbols);
      _lengthCount = 0;
    }
  }

  private static int EncodeOffsetToSlot(int offset) {
    if (offset <= 0) return 0;
    if (offset <= 2) return offset - 1;

    // v = offset - 1 (>= 2). Find highBit = floor(log2(v)).
    var v = offset - 1;
    var highBit = 0;
    var tmp = v;
    while (tmp > 1) { tmp >>= 1; ++highBit; }

    // The second-highest bit determines even/odd slot.
    var secondBit = (v >> (highBit - 1)) & 1;
    var slot = 2 * (highBit - 1) + secondBit + 2;

    if (slot >= LzmsConstants.NumLzOffsetSlots)
      slot = LzmsConstants.NumLzOffsetSlots - 1;
    return slot;
  }

  private void WriteOffsetExtraBits(int offset, int slot) {
    if (slot < 2) return;
    var extraBits = (slot - 2) / 2;
    if (extraBits <= 0) return;
    var baseOffset = (2 + (slot & 1)) << extraBits;
    var extra = (offset - 1) - baseOffset;
    if (extra < 0) extra = 0;
    WriteForwardBits(extra, extraBits);
  }

  // -------------------------------------------------------------------------
  // Range encoder (backward, 16-bit LE words with carry propagation)
  // -------------------------------------------------------------------------

  private void RangeEncodeBit(ref int prob, bool bit) {
    var bound = (_rcRange >> LzmsConstants.NumProbBits) * (uint)prob;
    if (!bit) {
      _rcRange = bound;
      prob += (LzmsConstants.ProbDenominator - prob) >> 4;
    } else {
      _rcLow += bound;
      _rcRange -= bound;
      prob -= prob >> 4;
    }

    while (_rcRange <= 0xFFFF) {
      // Extract carry (bit 32+) and the top 16 bits of the 32-bit value
      var carry = (int)(_rcLow >> 32);
      var word = (ushort)((_rcLow >> 16) & 0xFFFF);

      if (carry != 0) {
        // Propagate carry backward through previous words
        for (var i = _rcWords.Count - 1; i >= 0; --i) {
          _rcWords[i]++;
          if (_rcWords[i] != 0) break;
        }
      }

      _rcWords.Add(word);
      _rcLow = (_rcLow & 0xFFFF) << 16;
      _rcRange <<= 16;
    }
  }

  private void FlushRangeEncoder() {
    // Output remaining 32 bits of _rcLow (2 × 16-bit words)
    for (var i = 0; i < 2; ++i) {
      var carry = (int)(_rcLow >> 32);
      var word = (ushort)((_rcLow >> 16) & 0xFFFF);

      if (carry != 0) {
        for (var j = _rcWords.Count - 1; j >= 0; --j) {
          _rcWords[j]++;
          if (_rcWords[j] != 0) break;
        }
      }

      _rcWords.Add(word);
      _rcLow = (_rcLow & 0xFFFF) << 16;
    }

    // Ensure at least 2 words for decoder init (which reads 4 bytes)
    while (_rcWords.Count < 2)
      _rcWords.Add(0);
  }

  // -------------------------------------------------------------------------
  // Forward bitstream (MSB-first)
  // -------------------------------------------------------------------------

  private void WriteForwardBits(int value, int count) {
    _fwdBitBuf |= ((ulong)value & ((1ul << count) - 1)) << (64 - _fwdBitsUsed - count);
    _fwdBitsUsed += count;

    while (_fwdBitsUsed >= 8) {
      if (_fwdPos < _fwdOutput.Length)
        _fwdOutput[_fwdPos++] = (byte)(_fwdBitBuf >> 56);
      _fwdBitBuf <<= 8;
      _fwdBitsUsed -= 8;
    }
  }

  private void WriteHuffman(int symbol, int[] codeLens, int numSymbols) {
    if (symbol >= numSymbols) symbol = 0;
    var len = codeLens[symbol];
    if (len <= 0) len = 1;

    var code = BuildCanonicalCode(symbol, codeLens, numSymbols);
    WriteForwardBits(code, len);
  }

  private static int BuildCanonicalCode(int symbol, int[] codeLens, int numSymbols) {
    var targetLen = codeLens[symbol];
    if (targetLen <= 0) return 0;

    var maxLen = 0;
    for (var i = 0; i < numSymbols; ++i)
      maxLen = Math.Max(maxLen, codeLens[i]);

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

    for (var sym = 0; sym < numSymbols; ++sym) {
      var len = codeLens[sym];
      if (len <= 0) continue;
      if (sym == symbol) return nextCode[len];
      ++nextCode[len];
    }

    return 0;
  }

  private void FlushForwardBits() {
    while (_fwdBitsUsed > 0) {
      if (_fwdPos < _fwdOutput.Length)
        _fwdOutput[_fwdPos++] = (byte)(_fwdBitBuf >> 56);
      _fwdBitBuf <<= 8;
      _fwdBitsUsed -= 8;
    }
  }

  // -------------------------------------------------------------------------
  // Stream merging
  // -------------------------------------------------------------------------

  private byte[] MergeStreams() {
    // Layout: [forward Huffman bytes] [RC words placed backward]
    // The decoder reads Huffman forward from start, and RC backward from end.
    var fwdLen = _fwdPos;
    var rcByteLen = _rcWords.Count * 2;
    var result = new byte[fwdLen + rcByteLen];
    Array.Copy(_fwdOutput, 0, result, 0, fwdLen);

    // Place RC words: word 0 at the end, word 1 just before it, etc.
    // Each word is stored as 16-bit little-endian.
    var pos = result.Length;
    foreach (var word in _rcWords) {
      pos -= 2;
      result[pos] = (byte)word;           // low byte
      result[pos + 1] = (byte)(word >> 8); // high byte
    }

    return result;
  }

  // -------------------------------------------------------------------------
  // Huffman code length construction
  // -------------------------------------------------------------------------

  private static void RebuildCodeLengths(int[] freqs, int numSymbols, out int[] codeLens) {
    codeLens = new int[numSymbols];
    var nonZero = 0;
    for (var i = 0; i < numSymbols; ++i)
      if (freqs[i] > 0) ++nonZero;

    if (nonZero <= 1) {
      for (var i = 0; i < numSymbols; ++i)
        if (freqs[i] > 0) codeLens[i] = 1;
      return;
    }

    var bitsNeeded = 1;
    while ((1 << bitsNeeded) < nonZero) ++bitsNeeded;
    bitsNeeded = Math.Min(bitsNeeded, 15);

    var shortCount = (1 << bitsNeeded) - nonZero;
    var assigned = 0;
    for (var i = 0; i < numSymbols; ++i) {
      if (freqs[i] <= 0) continue;
      codeLens[i] = assigned < shortCount && bitsNeeded > 1 ? bitsNeeded - 1 : bitsNeeded;
      ++assigned;
    }
  }

  private static void HalveFrequencies(int[] freqs, int count) {
    for (var i = 0; i < count; ++i)
      freqs[i] = Math.Max(1, (freqs[i] + 1) >> 1);
  }
}
