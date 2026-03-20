using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// RAR5 LZ+Huffman compression engine.
/// Encodes data using adaptive multi-table Huffman coding with LZ77 match references,
/// producing output compatible with <see cref="Rar5Decoder"/>.
/// </summary>
public sealed class Rar5Encoder {
  private const int BlockSize = 0x20000; // 128 KB blocks

  private readonly int _dictionarySize;
  private readonly byte[] _window;
  private readonly int _windowMask;
  private int _windowPos;
  private readonly int[] _repDist = [0, 0, 0, 0];
  private int _lastLength;

  /// <summary>
  /// Initializes a new <see cref="Rar5Encoder"/> with the specified dictionary size.
  /// </summary>
  /// <param name="dictionarySize">Dictionary (window) size in bytes. Must be a power of two, minimum 128 KB.</param>
  public Rar5Encoder(int dictionarySize = Rar5Constants.MinDictionarySize) {
    ArgumentOutOfRangeException.ThrowIfLessThan(dictionarySize, Rar5Constants.MinDictionarySize);
    // Round up to power of two
    int size = 1;
    while (size < dictionarySize && size > 0) size <<= 1;
    this._dictionarySize = size;
    this._window = new byte[size];
    this._windowMask = size - 1;
  }

  /// <summary>
  /// Compresses data using the RAR5 algorithm.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    // For solid mode, prepend history from window so match finder can reference it
    byte[] workBuffer;
    int dataStart;

    if (this._windowPos > 0) {
      int histLen = Math.Min(this._windowPos, this._dictionarySize);
      workBuffer = new byte[histLen + data.Length];
      int histStart = (this._windowPos - histLen + this._dictionarySize) & this._windowMask;
      for (int i = 0; i < histLen; ++i)
        workBuffer[i] = this._window[(histStart + i) & this._windowMask];
      data.CopyTo(workBuffer.AsSpan(histLen));
      dataStart = histLen;
    }
    else {
      workBuffer = data.ToArray();
      dataStart = 0;
    }

    var writer = new Rar5BitWriter();
    var matchFinder = new HashChainMatchFinder(this._dictionarySize);

    // Insert history positions into match finder
    for (int i = 0; i < dataStart; ++i)
      matchFinder.InsertPosition(workBuffer, i);

    // Collect all tokens in a single block (the decoder stops when output size is reached)
    var tokens = CollectTokens(workBuffer, dataStart, workBuffer.Length, matchFinder,
      this._repDist, ref this._lastLength);

    // Build frequency tables
    var mainFreq = new int[Rar5Constants.MainTableSize];
    var offsetFreq = new int[Rar5Constants.OffsetTableSize];
    var lowOffsetFreq = new int[Rar5Constants.LowOffsetTableSize];
    var lengthFreq = new int[Rar5Constants.LengthTableSize];

    foreach (var token in tokens)
      CountFrequencies(token, mainFreq, offsetFreq, lowOffsetFreq, lengthFreq);

    // Ensure all tables have at least one symbol
    EnsureNonEmpty(mainFreq);
    EnsureNonEmpty(offsetFreq);
    EnsureNonEmpty(lowOffsetFreq);
    EnsureNonEmpty(lengthFreq);

    // Build Huffman encoders
    var mainEnc = new Rar5HuffmanEncoder();
    var offsetEnc = new Rar5HuffmanEncoder();
    var lowOffsetEnc = new Rar5HuffmanEncoder();
    var lengthEnc = new Rar5HuffmanEncoder();

    mainEnc.Build(mainFreq, Rar5Constants.MainTableSize);
    offsetEnc.Build(offsetFreq, Rar5Constants.OffsetTableSize);
    lowOffsetEnc.Build(lowOffsetFreq, Rar5Constants.LowOffsetTableSize);
    lengthEnc.Build(lengthFreq, Rar5Constants.LengthTableSize);

    // Write Huffman tables
    WriteTables(writer, mainEnc, offsetEnc, lowOffsetEnc, lengthEnc);

    // Write tokens
    foreach (var token in tokens)
      EncodeToken(writer, token, mainEnc, offsetEnc, lowOffsetEnc, lengthEnc);

    // Update window with new data for solid continuation
    for (int i = dataStart; i < workBuffer.Length; ++i) {
      this._window[this._windowPos] = workBuffer[i];
      this._windowPos = (this._windowPos + 1) & this._windowMask;
    }

    return writer.ToArray();
  }

  private List<Rar5Token> CollectTokens(ReadOnlySpan<byte> data, int start, int end,
      HashChainMatchFinder matchFinder, int[] repDist, ref int lastLength) {
    var tokens = new List<Rar5Token>();
    int pos = start;

    while (pos < end) {
      var match = matchFinder.FindMatch(data, pos, this._dictionarySize, 0x101 + 8, 2);

      // Repeat offsets disabled for reliability — only use full matches
      int bestRepIdx = -1;
      int bestRepLen = 0;

      if (bestRepIdx >= 0 && bestRepLen >= match.Length - 1 && bestRepLen >= 2) {
        tokens.Add(new Rar5Token {
          Type = Rar5TokenType.RepeatOffset,
          RepeatIndex = bestRepIdx,
          Length = bestRepLen,
        });

        // Update repeat offsets: move bestRepIdx to position 0
        int dist = repDist[bestRepIdx];
        for (int i = bestRepIdx; i > 0; --i)
          repDist[i] = repDist[i - 1];
        repDist[0] = dist;
        lastLength = bestRepLen;

        for (int i = 1; i < bestRepLen && pos + i < data.Length; ++i)
          matchFinder.InsertPosition(data, pos + i);
        pos += bestRepLen;
      }
      else if (match.Length >= 2) {
        tokens.Add(new Rar5Token {
          Type = Rar5TokenType.Match,
          Length = match.Length,
          Distance = match.Distance,
        });

        // Shift repeat offsets
        repDist[3] = repDist[2];
        repDist[2] = repDist[1];
        repDist[1] = repDist[0];
        repDist[0] = match.Distance;
        lastLength = match.Length;

        for (int i = 1; i < match.Length && pos + i < data.Length; ++i)
          matchFinder.InsertPosition(data, pos + i);
        pos += match.Length;
      }
      else {
        tokens.Add(new Rar5Token {
          Type = Rar5TokenType.Literal,
          Literal = data[pos],
        });
        pos++;
      }
    }

    return tokens;
  }

  private static int MatchLength(ReadOnlySpan<byte> data, int pos, int refPos, int limit) {
    if (refPos < 0) return 0;
    int len = 0;
    int maxLen = Math.Min(Rar5Constants.MaxMatchLength, limit - pos);
    while (len < maxLen && data[pos + len] == data[refPos + len])
      ++len;
    return len;
  }

  private static void CountFrequencies(Rar5Token token,
      int[] mainFreq, int[] offsetFreq, int[] lowOffsetFreq, int[] lengthFreq) {
    switch (token.Type) {
      case Rar5TokenType.Literal:
        ++mainFreq[token.Literal];
        break;

      case Rar5TokenType.Match: {
        // Encode match length
        int length = token.Length;
        if (length <= 9) {
          // Direct: symbol 262 + (length - 2)
          ++mainFreq[Rar5Constants.MatchBase + length - 2];
        }
        else {
          // Extended: symbol 270+, use length table
          ++mainFreq[Rar5Constants.MatchBase + 8]; // Any of 270-277 would work; simplified
          int lenSym = GetLengthSymbol(length - 8);
          ++lengthFreq[lenSym];
        }

        // Encode distance
        int distSlot = GetDistanceSlot(token.Distance - 1);
        ++offsetFreq[distSlot];
        int extraBits = Rar5Constants.DistanceExtraBits(distSlot);
        if (extraBits > 4) {
          int baseDist = Rar5Constants.DistanceBase(distSlot);
          int extra = (token.Distance - 1) - baseDist;
          int lowBits = extra & 0xF;
          ++lowOffsetFreq[lowBits];
        }
        break;
      }

      case Rar5TokenType.RepeatOffset: {
        int sym = Rar5Constants.RepeatOffset0 + token.RepeatIndex;
        ++mainFreq[sym];
        // For repeat offsets 1-3, the length is encoded via length table
        if (token.RepeatIndex > 0) {
          int lenSym = GetLengthSymbol(token.Length);
          ++lengthFreq[lenSym];
        }
        break;
      }
    }
  }

  private static void EncodeToken(Rar5BitWriter writer, Rar5Token token,
      Rar5HuffmanEncoder mainEnc, Rar5HuffmanEncoder offsetEnc,
      Rar5HuffmanEncoder lowOffsetEnc, Rar5HuffmanEncoder lengthEnc) {
    switch (token.Type) {
      case Rar5TokenType.Literal:
        mainEnc.EncodeSymbol(writer, token.Literal);
        break;

      case Rar5TokenType.Match: {
        int length = token.Length;
        if (length <= 9) {
          mainEnc.EncodeSymbol(writer, Rar5Constants.MatchBase + length - 2);
        }
        else {
          // Extended length: write a match-base symbol in range 270-277
          // The slot 8+ triggers length table lookup in the decoder
          mainEnc.EncodeSymbol(writer, Rar5Constants.MatchBase + 8);
          int lenSym = GetLengthSymbol(length - 8);
          lengthEnc.EncodeSymbol(writer, lenSym);
          int extraBits = GetLengthExtraBits(lenSym);
          if (extraBits > 0) {
            int baseLen = GetLengthBase(lenSym);
            writer.WriteBits((uint)(length - 8 - baseLen - 2), extraBits);
          }
        }

        // Encode distance
        EncodeDistance(writer, token.Distance, offsetEnc, lowOffsetEnc);
        break;
      }

      case Rar5TokenType.RepeatOffset: {
        int sym = Rar5Constants.RepeatOffset0 + token.RepeatIndex;
        mainEnc.EncodeSymbol(writer, sym);

        if (token.RepeatIndex > 0) {
          int lenSym = GetLengthSymbol(token.Length);
          lengthEnc.EncodeSymbol(writer, lenSym);
          int extraBits = GetLengthExtraBits(lenSym);
          if (extraBits > 0) {
            int baseLen = GetLengthBase(lenSym);
            writer.WriteBits((uint)(token.Length - baseLen - 2), extraBits);
          }
        }
        break;
      }
    }
  }

  private static void EncodeDistance(Rar5BitWriter writer, int distance,
      Rar5HuffmanEncoder offsetEnc, Rar5HuffmanEncoder lowOffsetEnc) {
    int dist0 = distance - 1; // 0-based distance
    int slot = GetDistanceSlot(dist0);
    offsetEnc.EncodeSymbol(writer, slot);

    int extraBits = Rar5Constants.DistanceExtraBits(slot);
    if (extraBits > 0) {
      int baseDist = Rar5Constants.DistanceBase(slot);
      int extra = dist0 - baseDist;

      if (extraBits > 4) {
        int highBits = extra >> 4;
        int lowBits = extra & 0xF;
        writer.WriteBits((uint)highBits, extraBits - 4);
        lowOffsetEnc.EncodeSymbol(writer, lowBits);
      }
      else {
        writer.WriteBits((uint)extra, extraBits);
      }
    }
  }

  private static void WriteTables(Rar5BitWriter writer,
      Rar5HuffmanEncoder mainEnc, Rar5HuffmanEncoder offsetEnc,
      Rar5HuffmanEncoder lowOffsetEnc, Rar5HuffmanEncoder lengthEnc) {
    // First, compute the actual RLE sequences for all 4 tables
    var rleMain = ComputeRleSequence(mainEnc.CodeLengths, Rar5Constants.MainTableSize);
    var rleOffset = ComputeRleSequence(offsetEnc.CodeLengths, Rar5Constants.OffsetTableSize);
    var rleLowOffset = ComputeRleSequence(lowOffsetEnc.CodeLengths, Rar5Constants.LowOffsetTableSize);
    var rleLength = ComputeRleSequence(lengthEnc.CodeLengths, Rar5Constants.LengthTableSize);

    // Count actual code-length symbols used across all 4 tables
    var clFreq = new int[Rar5Constants.CodeLengthTableSize];
    CountRleFrequencies(rleMain, clFreq);
    CountRleFrequencies(rleOffset, clFreq);
    CountRleFrequencies(rleLowOffset, clFreq);
    CountRleFrequencies(rleLength, clFreq);

    // Build code-length encoder
    var clEncoder = new Rar5HuffmanEncoder();
    clEncoder.Build(clFreq, Rar5Constants.CodeLengthTableSize);

    // Write code-length code lengths (4 bits each, 20 values)
    for (int i = 0; i < Rar5Constants.CodeLengthTableSize; ++i)
      writer.WriteBits((uint)clEncoder.CodeLengths[i], 4);

    // Write each table's RLE-encoded code lengths
    WriteRleSequence(writer, rleMain, clEncoder);
    WriteRleSequence(writer, rleOffset, clEncoder);
    WriteRleSequence(writer, rleLowOffset, clEncoder);
    WriteRleSequence(writer, rleLength, clEncoder);
  }

  /// <summary>
  /// Computes the RLE sequence for a set of code lengths.
  /// Each entry is (symbol, extraBits, extraValue) where symbol is 0-18.
  /// </summary>
  private static List<(int sym, int extraBits, int extraValue)> ComputeRleSequence(
      int[] codeLengths, int numSymbols) {
    var rle = new List<(int sym, int extraBits, int extraValue)>();
    int i = 0;

    while (i < numSymbols) {
      if (codeLengths[i] == 0) {
        int run = 1;
        while (i + run < numSymbols && codeLengths[i + run] == 0) ++run;
        i += run;

        while (run > 0) {
          if (run >= 11) {
            int count = Math.Min(run, 138);
            rle.Add((18, 7, count - 11));
            run -= count;
          }
          else if (run >= 3) {
            rle.Add((17, 3, run - 3));
            run = 0;
          }
          else {
            rle.Add((0, 0, 0));
            --run;
          }
        }
      }
      else {
        int val = codeLengths[i];
        rle.Add((val, 0, 0));
        ++i;

        int rep = 0;
        while (i < numSymbols && codeLengths[i] == val && rep < 6) {
          ++rep;
          ++i;
        }

        while (rep >= 3) {
          int batch = Math.Min(rep, 6);
          rle.Add((16, 2, batch - 3));
          rep -= batch;
        }
        while (rep > 0) {
          rle.Add((val, 0, 0));
          --rep;
        }
      }
    }

    return rle;
  }

  private static void CountRleFrequencies(List<(int sym, int extraBits, int extraValue)> rle, int[] freq) {
    foreach (var (sym, _, _) in rle)
      ++freq[sym];
  }

  private static void WriteRleSequence(Rar5BitWriter writer,
      List<(int sym, int extraBits, int extraValue)> rle, Rar5HuffmanEncoder clEncoder) {
    foreach (var (sym, extraBits, extraValue) in rle) {
      clEncoder.EncodeSymbol(writer, sym);
      if (extraBits > 0)
        writer.WriteBits((uint)extraValue, extraBits);
    }
  }

  private static int GetDistanceSlot(int distance) {
    // Inverse of Rar5Constants.DistanceBase
    if (distance < 4) return distance;
    int p = 31 - int.LeadingZeroCount(distance); // highest bit position
    int secondBit = (distance >> (p - 1)) & 1;
    return 2 * p + secondBit;
  }

  /// <summary>
  /// Gets the length table symbol for a given value that DecodeExtraLength must reconstruct.
  /// DecodeExtraLength returns: lenSym+2 (for sym 0-7), or base+extra+2 (for sym 8+).
  /// </summary>
  private static int GetLengthSymbol(int decodeExtraValue) {
    // For lenSym 0-7: DecodeExtraLength = lenSym + 2, so lenSym = value - 2
    if (decodeExtraValue >= 2 && decodeExtraValue <= 9)
      return decodeExtraValue - 2;

    // For lenSym 8+: DecodeExtraLength = base + readBits + 2
    // So: readBits = value - base - 2, must fit in extraBits
    for (int sym = 15; sym >= 8; --sym) {
      int extraBits = sym / 2 - 1;
      int baseLenDec = (2 + (sym & 1)) << extraBits;
      int extra = decodeExtraValue - baseLenDec - 2;
      if (extra >= 0 && extra < (1 << extraBits))
        return sym;
    }
    return 0;
  }

  /// <summary>
  /// Gets the extra bits count for a length table symbol.
  /// </summary>
  private static int GetLengthExtraBits(int sym) {
    if (sym < 8) return 0;
    return sym / 2 - 1;
  }

  /// <summary>
  /// Gets the base value for computing extra bits for a length table symbol.
  /// For lenSym 8+: DecodeExtraLength = base + readBits + 2. So extra = value - base - 2.
  /// </summary>
  private static int GetLengthBase(int sym) {
    if (sym < 8) return sym;
    int extraBits = sym / 2 - 1;
    return (2 + (sym & 1)) << extraBits;
  }

  private static void EnsureNonEmpty(int[] freq) {
    for (int i = 0; i < freq.Length; ++i)
      if (freq[i] > 0) return;
    freq[0] = 1;
  }

  private enum Rar5TokenType { Literal, Match, RepeatOffset }

  private struct Rar5Token {
    public Rar5TokenType Type;
    public byte Literal;
    public int Length;
    public int Distance;
    public int RepeatIndex;
  }
}
