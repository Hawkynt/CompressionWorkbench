namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// Decodes SQX LZH compressed data using the real 310-symbol alphabet.
/// </summary>
/// <remarks>
/// Instance-based to support solid mode (persistent window + repeated offsets across files).
/// </remarks>
public sealed class SqxDecoder {
  private readonly byte[] _window;
  private readonly int _dictSize;
  private int _windowPos;
  private int _lastLen;
  private int _lastDist;
  private readonly int[] _prevDists = new int[4];
  private int _prevDistIndex;

  /// <summary>
  /// Initializes a new SQX decoder.
  /// </summary>
  /// <param name="dictSize">Dictionary size in bytes.</param>
  public SqxDecoder(int dictSize = SqxConstants.DefaultDictSize) {
    this._dictSize = dictSize;
    this._window = new byte[dictSize];
  }

  /// <summary>
  /// Resets decoder state (for non-solid mode between files).
  /// </summary>
  public void Reset() {
    this._windowPos = 0;
    this._lastLen = 0;
    this._lastDist = 0;
    Array.Clear(this._prevDists);
    this._prevDistIndex = 0;
  }

  /// <summary>
  /// Decompresses SQX LZH data.
  /// </summary>
  public byte[] Decode(byte[] compressed, int originalSize) {
    var output = new byte[originalSize];
    int outPos = 0;
    int bitPos = 0;

    int distSlots = SqxConstants.GetDistSlots(this._dictSize);

    while (outPos < originalSize) {
      // Read block symbol count (16 bits)
      int blockSymbolCount = ReadBits(compressed, ref bitPos, 16);
      if (blockSymbolCount == 0) break;

      // Read pre-tree (19 x 4-bit raw code lengths)
      var preTreeLens = new int[SqxConstants.PreTreeSymbols];
      for (int i = 0; i < SqxConstants.PreTreeSymbols; ++i)
        preTreeLens[i] = ReadBits(compressed, ref bitPos, 4);
      int[] preTree = BuildDecodeTable(preTreeLens, SqxConstants.PreTreeSymbols, SqxConstants.PreTreeMaxBits);

      // Read main tree code lengths via pre-tree
      var mainLens = ReadCodeLengths(compressed, ref bitPos, preTree, SqxConstants.PreTreeMaxBits, SqxConstants.NC);
      int[] mainTree = BuildDecodeTable(mainLens, SqxConstants.NC, SqxConstants.MainTreeMaxBits);

      // Read distance tree code lengths via pre-tree
      var distLens = ReadCodeLengths(compressed, ref bitPos, preTree, SqxConstants.PreTreeMaxBits, distSlots);
      int[] distTree = BuildDecodeTable(distLens, distSlots, SqxConstants.MainTreeMaxBits);

      // Decode symbols
      int symbolsDecoded = 0;
      while (symbolsDecoded < blockSymbolCount && outPos < originalSize) {
        int sym = DecodeSymbol(compressed, ref bitPos, mainTree, SqxConstants.MainTreeMaxBits);
        ++symbolsDecoded;

        if (sym < 256) {
          // Literal
          output[outPos++] = (byte)sym;
          this._window[this._windowPos] = (byte)sym;
          this._windowPos = (this._windowPos + 1) & (this._dictSize - 1);
        }
        else if (sym == SqxConstants.DupLastSymbol) {
          // Duplicate last match
          CopyMatch(output, ref outPos, this._lastDist, this._lastLen, originalSize);
        }
        else if (sym >= SqxConstants.RepStart && sym < SqxConstants.RepStart + SqxConstants.RepCodes) {
          // Repeated offset (257-260)
          int repIdx = (this._prevDistIndex - (sym - SqxConstants.RepStart)) & 3;
          int distance = this._prevDists[repIdx];
          if (distance == 0) distance = 1;

          // Decode length from length-4+ base table
          int lenSym = DecodeSymbol(compressed, ref bitPos, mainTree, SqxConstants.MainTreeMaxBits);
          int length;
          if (lenSym >= SqxConstants.LenStart && lenSym < SqxConstants.LenStart + SqxConstants.LenCodes) {
            int lenIdx = lenSym - SqxConstants.LenStart;
            length = SqxConstants.LenOffsets[lenIdx] + 4;
            if (SqxConstants.LenExtraBits[lenIdx] > 0)
              length += ReadBits(compressed, ref bitPos, SqxConstants.LenExtraBits[lenIdx]);
            // Length adjustment for large distances
            if (distance > SqxConstants.MaxDistLen3) ++length;
            if (distance > SqxConstants.MaxDistLen4) ++length;
          }
          else {
            length = SqxConstants.MinMatch; // fallback
          }
          ++symbolsDecoded; // length symbol counts

          this._lastLen = length;
          this._lastDist = distance;
          UpdateRepDists(distance);
          CopyMatch(output, ref outPos, distance, length, originalSize);
        }
        else if (sym >= SqxConstants.Len2Start && sym < SqxConstants.Len2Start + SqxConstants.Len2Codes) {
          // Length-2 match (261-268) — distance is inline
          int idx = sym - SqxConstants.Len2Start;
          int distance = SqxConstants.Len2Offsets[idx];
          if (SqxConstants.Len2ExtraBits[idx] > 0)
            distance += ReadBits(compressed, ref bitPos, SqxConstants.Len2ExtraBits[idx]);
          ++distance; // distances are 1-based

          this._lastLen = 2;
          this._lastDist = distance;
          UpdateRepDists(distance);
          CopyMatch(output, ref outPos, distance, 2, originalSize);
        }
        else if (sym >= SqxConstants.Len3Start && sym < SqxConstants.Len3Start + SqxConstants.Len3Codes) {
          // Length-3 match (269-283) — distance is inline
          int idx = sym - SqxConstants.Len3Start;
          int distance = SqxConstants.Len3Offsets[idx];
          if (SqxConstants.Len3ExtraBits[idx] > 0)
            distance += ReadBits(compressed, ref bitPos, SqxConstants.Len3ExtraBits[idx]);
          ++distance; // distances are 1-based

          this._lastLen = 3;
          this._lastDist = distance;
          UpdateRepDists(distance);
          CopyMatch(output, ref outPos, distance, 3, originalSize);
        }
        else if (sym >= SqxConstants.LenStart && sym < SqxConstants.LenStart + SqxConstants.LenCodes) {
          // Length 4+ match (284-308) — length inline, distance from distance tree
          int lenIdx = sym - SqxConstants.LenStart;
          int length;

          if (lenIdx == SqxConstants.LenCodes - 1) {
            // Special: code 308 = read 14 raw bits + 257
            length = ReadBits(compressed, ref bitPos, 14) + 257;
          }
          else {
            length = SqxConstants.LenOffsets[lenIdx] + 4;
            if (SqxConstants.LenExtraBits[lenIdx] > 0)
              length += ReadBits(compressed, ref bitPos, SqxConstants.LenExtraBits[lenIdx]);
          }

          // Decode distance from distance tree
          int distSym = DecodeSymbol(compressed, ref bitPos, distTree, SqxConstants.MainTreeMaxBits);
          int distance = DecodeDistance(compressed, ref bitPos, distSym);

          // Length adjustment for large distances
          if (lenIdx != SqxConstants.LenCodes - 1) {
            if (distance > SqxConstants.MaxDistLen3) ++length;
            if (distance > SqxConstants.MaxDistLen4) ++length;
          }

          this._lastLen = length;
          this._lastDist = distance;
          UpdateRepDists(distance);
          CopyMatch(output, ref outPos, distance, length, originalSize);
        }
      }
    }

    return output;
  }

  /// <summary>
  /// Static convenience method for non-solid single-file decoding.
  /// </summary>
  public static byte[] Decode(byte[] compressed, int originalSize, int dictSize) {
    var decoder = new SqxDecoder(dictSize);
    return decoder.Decode(compressed, originalSize);
  }

  private void UpdateRepDists(int distance) {
    this._prevDists[this._prevDistIndex & 3] = distance;
    ++this._prevDistIndex;
  }

  private void CopyMatch(byte[] output, ref int outPos, int distance, int length, int maxSize) {
    for (int i = 0; i < length && outPos < maxSize; ++i) {
      int srcWinPos = (this._windowPos - distance) & (this._dictSize - 1);
      byte b = this._window[srcWinPos];
      output[outPos++] = b;
      this._window[this._windowPos] = b;
      this._windowPos = (this._windowPos + 1) & (this._dictSize - 1);
    }
  }

  private static int DecodeDistance(byte[] data, ref int bitPos, int distSym) {
    if (distSym == 0) return 1;
    if (distSym == 1) return 2;
    int extraBits = distSym - 1;
    return (1 << extraBits) + ReadBits(data, ref bitPos, extraBits);
  }

  private static int[] ReadCodeLengths(byte[] data, ref int bitPos, int[] preTree, int preTreeBits, int count) {
    var lengths = new int[count];
    int i = 0;
    while (i < count) {
      int sym = DecodeSymbol(data, ref bitPos, preTree, preTreeBits);
      if (sym < 16) {
        lengths[i++] = sym;
      }
      else if (sym == 16) {
        // Repeat previous length 3-6 times
        int repeat = ReadBits(data, ref bitPos, 2) + 3;
        int prev = i > 0 ? lengths[i - 1] : 0;
        for (int j = 0; j < repeat && i < count; ++j)
          lengths[i++] = prev;
      }
      else if (sym == 17) {
        // Repeat zero 3-10 times
        int repeat = ReadBits(data, ref bitPos, 3) + 3;
        for (int j = 0; j < repeat && i < count; ++j)
          lengths[i++] = 0;
      }
      else if (sym == 18) {
        // Repeat zero 11-138 times
        int repeat = ReadBits(data, ref bitPos, 7) + 11;
        for (int j = 0; j < repeat && i < count; ++j)
          lengths[i++] = 0;
      }
    }
    return lengths;
  }

  private static int DecodeSymbol(byte[] data, ref int bitPos, int[] table, int tableBits) {
    int peek = PeekBits(data, bitPos, tableBits);
    int entry = table[peek & ((1 << tableBits) - 1)];
    int symbol = entry & 0xFFFF;
    int codeLen = entry >> 16;
    if (codeLen > 0)
      bitPos += codeLen;
    else
      bitPos += 1; // single-symbol tree
    return symbol;
  }

  private static int PeekBits(byte[] data, int bitPos, int count) {
    int result = 0;
    for (int i = 0; i < count; ++i) {
      int byteIdx = (bitPos + i) >> 3;
      int bitIdx = 7 - ((bitPos + i) & 7);
      if (byteIdx < data.Length)
        result = (result << 1) | ((data[byteIdx] >> bitIdx) & 1);
      else
        result <<= 1;
    }
    return result;
  }

  private static int ReadBits(byte[] data, ref int bitPos, int count) {
    int result = 0;
    for (int i = 0; i < count; ++i) {
      int byteIdx = bitPos >> 3;
      int bitIdx = 7 - (bitPos & 7);
      if (byteIdx < data.Length)
        result = (result << 1) | ((data[byteIdx] >> bitIdx) & 1);
      else
        result <<= 1;
      ++bitPos;
    }
    return result;
  }

  private static int[] BuildDecodeTable(int[] codeLengths, int numSymbols, int tableBits) {
    int maxLen = 0;
    for (int i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > maxLen) maxLen = codeLengths[i];
    if (maxLen == 0) maxLen = 1;
    if (maxLen > tableBits) maxLen = tableBits;

    // Always build table of size 2^tableBits so DecodeSymbol peek is safe
    int tableSize = 1 << tableBits;
    var table = new int[tableSize];

    // Check for single-symbol case
    int usedCount = 0;
    int singleSym = 0;
    for (int i = 0; i < numSymbols; ++i) {
      if (codeLengths[i] > 0) { ++usedCount; singleSym = i; }
    }
    if (usedCount <= 1) {
      int entry = singleSym | (1 << 16);
      Array.Fill(table, entry);
      return table;
    }

    var blCount = new int[maxLen + 1];
    for (int i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > 0 && codeLengths[i] <= maxLen) ++blCount[codeLengths[i]];

    var nextCode = new int[maxLen + 1];
    int code = 0;
    for (int b = 1; b <= maxLen; ++b) {
      code = (code + blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    for (int sym = 0; sym < numSymbols; ++sym) {
      int len = codeLengths[sym];
      if (len == 0 || len > maxLen) continue;
      int c = nextCode[len]++;
      int fill = 1 << (tableBits - len);
      int baseIdx = c << (tableBits - len);
      int entry = sym | (len << 16);
      for (int j = 0; j < fill && baseIdx + j < tableSize; ++j)
        table[baseIdx + j] = entry;
    }

    return table;
  }
}
