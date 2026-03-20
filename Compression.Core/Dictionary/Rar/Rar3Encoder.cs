using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// RAR3 LZ+Huffman compression engine.
/// Encodes data using adaptive multi-table Huffman coding with LZ77 match references,
/// producing output compatible with <see cref="Rar3Decoder"/> and real WinRAR.
/// 4 Huffman tables: Main(299) + Dist(60) + LowDist(17) + RepLen(28).
/// </summary>
public sealed class Rar3Encoder {
  private const int MainTableSize = 299;
  private const int DistTableSize = 60;
  private const int LowDistTableSize = 17;
  private const int RepLenTableSize = 28;

  // Length table (first 28 entries used for RepLen, all 36 for main table lengths)
  private static readonly int[] LenBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0, 0, 0, 0, 0, 0, 0, 0];
  private static readonly int[] LenBase = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768];

  // Max match length encodable with continuous length slots (slot 27: base 224 + 31 extra + 3 offset)
  private const int MaxMatchLength = 258;

  // Max repeat-offset match length (slot 27 of RepLen: base 224 + 31 extra + 2 offset)
  private const int MaxRepMatchLength = 257;

  private readonly int _windowSize;
  private readonly int _windowMask;
  private readonly byte[] _window;
  private int _windowPos;
  private readonly int[] _rep = [0, 0, 0, 0];

  // Previous table code lengths for delta coding across solid blocks
  private readonly int[] _prevMainLens = new int[MainTableSize];
  private readonly int[] _prevDistLens = new int[DistTableSize];
  private readonly int[] _prevLowDistLens = new int[LowDistTableSize];
  private readonly int[] _prevRepLenLens = new int[RepLenTableSize];

  /// <summary>
  /// Initializes a new <see cref="Rar3Encoder"/> with the specified window size.
  /// </summary>
  /// <param name="windowBits">Window size as log2 (15-22). Default 22 (4MB).</param>
  public Rar3Encoder(int windowBits = 22) {
    windowBits = Math.Clamp(windowBits, 15, 22);
    this._windowSize = 1 << windowBits;
    this._windowMask = this._windowSize - 1;
    this._window = new byte[this._windowSize];
  }

  /// <summary>
  /// Compresses data using the RAR3 algorithm.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var writer = new Rar3BitWriter();

    // First bit: 0 = LZ mode (not PPMd)
    writer.WriteBit(0);

    // Collect tokens via LZ77 match finding with repeat-offset detection
    var workBuffer = data.ToArray();
    var matchFinder = new HashChainMatchFinder(this._windowSize);
    var tokens = CollectTokens(workBuffer, 0, workBuffer.Length, matchFinder);

    // Build frequency tables for all 4 Huffman tables
    var mainFreq = new int[MainTableSize];
    var distFreq = new int[DistTableSize];
    var lowDistFreq = new int[LowDistTableSize];
    var repLenFreq = new int[RepLenTableSize];

    foreach (var token in tokens)
      CountFrequencies(token, mainFreq, distFreq, lowDistFreq, repLenFreq);

    EnsureNonEmpty(mainFreq);
    EnsureNonEmpty(distFreq);
    EnsureNonEmpty(lowDistFreq);
    EnsureNonEmpty(repLenFreq);

    // Build Huffman encoders
    var mainEnc = new Rar3HuffmanEncoder();
    var distEnc = new Rar3HuffmanEncoder();
    var lowDistEnc = new Rar3HuffmanEncoder();
    var repLenEnc = new Rar3HuffmanEncoder();

    mainEnc.Build(mainFreq, MainTableSize);
    distEnc.Build(distFreq, DistTableSize);
    lowDistEnc.Build(lowDistFreq, LowDistTableSize);
    repLenEnc.Build(repLenFreq, RepLenTableSize);

    // Write 4 Huffman tables (delta-coded against previous block for solid)
    WriteTables(writer, mainEnc, distEnc, lowDistEnc, repLenEnc,
      this._prevMainLens, this._prevDistLens, this._prevLowDistLens, this._prevRepLenLens);

    // Update previous table code lengths for next solid block
    Array.Copy(mainEnc.CodeLengths, this._prevMainLens, MainTableSize);
    Array.Copy(distEnc.CodeLengths, this._prevDistLens, DistTableSize);
    Array.Copy(lowDistEnc.CodeLengths, this._prevLowDistLens, LowDistTableSize);
    Array.Copy(repLenEnc.CodeLengths, this._prevRepLenLens, RepLenTableSize);

    // Write tokens
    foreach (var token in tokens)
      EncodeToken(writer, token, mainEnc, distEnc, lowDistEnc, repLenEnc);

    // Update window for solid continuation
    for (int i = 0; i < workBuffer.Length; ++i) {
      this._window[this._windowPos] = workBuffer[i];
      this._windowPos = (this._windowPos + 1) & this._windowMask;
    }

    return writer.ToArray();
  }

  private List<Rar3Token> CollectTokens(byte[] data, int start, int end, HashChainMatchFinder matchFinder) {
    var tokens = new List<Rar3Token>();
    int pos = start;

    while (pos < end) {
      var match = matchFinder.FindMatch(data, pos, this._windowSize, MaxMatchLength, 3);

      if (match.Length >= 3) {
        // Check if match distance equals a repeated offset — use shorter repeat code
        int repIdx = -1;
        for (int r = 0; r < 4; ++r) {
          if (this._rep[r] == match.Distance) { repIdx = r; break; }
        }

        int effectiveLen = match.Length;

        if (repIdx == 0 && effectiveLen == 2) {
          // Symbol 258: repeat last match with length 2 (special case)
          tokens.Add(new Rar3Token { Type = TokenType.RepeatLast });
        } else if (repIdx >= 0) {
          // Symbol 259-262: repeat offset with length from RepLen table
          if (effectiveLen > MaxRepMatchLength) effectiveLen = MaxRepMatchLength;
          tokens.Add(new Rar3Token {
            Type = TokenType.RepeatOffset,
            Length = effectiveLen,
            RepIndex = repIdx,
          });

          // Rotate rep distances (bring repIdx to front)
          int dist = this._rep[repIdx];
          for (int i = repIdx; i > 0; --i)
            this._rep[i] = this._rep[i - 1];
          this._rep[0] = dist;
        } else {
          // New match with explicit distance
          tokens.Add(new Rar3Token {
            Type = TokenType.Match,
            Length = effectiveLen,
            Distance = match.Distance,
          });

          this._rep[3] = this._rep[2];
          this._rep[2] = this._rep[1];
          this._rep[1] = this._rep[0];
          this._rep[0] = match.Distance;
        }

        for (int i = 1; i < effectiveLen && pos + i < data.Length; ++i)
          matchFinder.InsertPosition(data, pos + i);
        pos += effectiveLen;
      } else {
        // Check for 2-byte repeat of last distance
        if (this._rep[0] > 0 && pos + 2 <= end && pos >= this._rep[0]) {
          if (data[pos] == data[pos - this._rep[0]] && data[pos + 1] == data[pos + 1 - this._rep[0]]) {
            tokens.Add(new Rar3Token { Type = TokenType.RepeatLast });
            matchFinder.InsertPosition(data, pos);
            if (pos + 1 < data.Length) matchFinder.InsertPosition(data, pos + 1);
            pos += 2;
            continue;
          }
        }

        tokens.Add(new Rar3Token {
          Type = TokenType.Literal,
          Literal = data[pos],
        });
        matchFinder.InsertPosition(data, pos);
        pos++;
      }
    }

    return tokens;
  }

  private static void CountFrequencies(Rar3Token token, int[] mainFreq, int[] distFreq,
      int[] lowDistFreq, int[] repLenFreq) {
    switch (token.Type) {
      case TokenType.Literal:
        ++mainFreq[token.Literal];
        break;
      case TokenType.RepeatLast:
        ++mainFreq[258]; // Symbol 258: repeat last with length 2
        break;
      case TokenType.RepeatOffset: {
        int mainSym = 259 + token.RepIndex;
        ++mainFreq[mainSym];
        // RepLen table for the length
        int length = token.Length - 2; // RepLen decoder adds 2
        int lenSlot = GetLenSlot(length, RepLenTableSize);
        ++repLenFreq[lenSlot];
        break;
      }
      case TokenType.Match: {
        int length = token.Length - 3; // Decoder adds 3 for new matches
        int lenSlot = GetLenSlot(length, 36);
        int mainSym = 263 + lenSlot;
        ++mainFreq[mainSym];

        int distSlot = GetDistSlot(token.Distance);
        ++distFreq[distSlot];

        // LowDist table frequency for distances with bits >= 4
        if (distSlot >= 4) {
          int bits = distSlot / 2 - 1;
          if (bits >= 4) {
            int baseDist = ((2 | (distSlot & 1)) << bits) + 1;
            int extra = token.Distance - baseDist;
            int lowBits = extra & 0xF;
            ++lowDistFreq[lowBits];
          }
        }
        break;
      }
    }
  }

  private static void EncodeToken(Rar3BitWriter writer, Rar3Token token,
      Rar3HuffmanEncoder mainEnc, Rar3HuffmanEncoder distEnc,
      Rar3HuffmanEncoder lowDistEnc, Rar3HuffmanEncoder repLenEnc) {
    switch (token.Type) {
      case TokenType.Literal:
        mainEnc.EncodeSymbol(writer, token.Literal);
        break;

      case TokenType.RepeatLast:
        mainEnc.EncodeSymbol(writer, 258);
        break;

      case TokenType.RepeatOffset: {
        mainEnc.EncodeSymbol(writer, 259 + token.RepIndex);
        // Encode length via RepLen table (28 symbols, decoder adds 2)
        int length = token.Length - 2;
        int lenSlot = GetLenSlot(length, RepLenTableSize);
        repLenEnc.EncodeSymbol(writer, lenSlot);
        if (LenBits[lenSlot] > 0) {
          int extra = length - LenBase[lenSlot];
          writer.WriteBits((uint)extra, LenBits[lenSlot]);
        }
        break;
      }

      case TokenType.Match: {
        int length = token.Length - 3;
        int lenSlot = GetLenSlot(length, 36);
        int mainSym = 263 + lenSlot;
        mainEnc.EncodeSymbol(writer, mainSym);

        if (LenBits[lenSlot] > 0) {
          int extra = length - LenBase[lenSlot];
          writer.WriteBits((uint)extra, LenBits[lenSlot]);
        }

        // Encode distance: RAR3 formula
        int distSlot = GetDistSlot(token.Distance);
        distEnc.EncodeSymbol(writer, distSlot);

        if (distSlot >= 4) {
          int bits = distSlot / 2 - 1;
          int baseDist = ((2 | (distSlot & 1)) << bits) + 1;
          int extra = token.Distance - baseDist;
          if (bits >= 4) {
            // High bits as raw, low 4 bits via LowDist Huffman
            if (bits > 4)
              writer.WriteBits((uint)(extra >> 4), bits - 4);
            lowDistEnc.EncodeSymbol(writer, extra & 0xF);
          } else if (bits > 0) {
            writer.WriteBits((uint)extra, bits);
          }
        }
        break;
      }
    }
  }

  private static void WriteTables(Rar3BitWriter writer,
      Rar3HuffmanEncoder mainEnc, Rar3HuffmanEncoder distEnc,
      Rar3HuffmanEncoder lowDistEnc, Rar3HuffmanEncoder repLenEnc,
      int[] prevMainLens, int[] prevDistLens,
      int[] prevLowDistLens, int[] prevRepLenLens) {
    var rleMain = BuildRleSequence(mainEnc.CodeLengths, prevMainLens, MainTableSize);
    var rleDist = BuildRleSequence(distEnc.CodeLengths, prevDistLens, DistTableSize);
    var rleLowDist = BuildRleSequence(lowDistEnc.CodeLengths, prevLowDistLens, LowDistTableSize);
    var rleRepLen = BuildRleSequence(repLenEnc.CodeLengths, prevRepLenLens, RepLenTableSize);

    // Count code-length symbol frequencies across all 4 tables
    var clFreq = new int[20];
    foreach (var (sym, _, _) in rleMain) ++clFreq[sym];
    foreach (var (sym, _, _) in rleDist) ++clFreq[sym];
    foreach (var (sym, _, _) in rleLowDist) ++clFreq[sym];
    foreach (var (sym, _, _) in rleRepLen) ++clFreq[sym];
    EnsureNonEmpty(clFreq);

    var clEnc = new Rar3HuffmanEncoder();
    clEnc.Build(clFreq, 20);

    // Write code-length code lengths (4 bits each, 20 values)
    for (int i = 0; i < 20; ++i)
      writer.WriteBits((uint)clEnc.CodeLengths[i], 4);

    // Write all 4 tables in order: Main, Dist, LowDist, RepLen
    WriteRle(writer, rleMain, clEnc);
    WriteRle(writer, rleDist, clEnc);
    WriteRle(writer, rleLowDist, clEnc);
    WriteRle(writer, rleRepLen, clEnc);
  }

  private static List<(int sym, int extraBits, int extraValue)> BuildRleSequence(
      int[] codeLengths, int[] prevLengths, int numSymbols) {
    var rle = new List<(int sym, int extraBits, int extraValue)>();

    int i = 0;
    while (i < numSymbols) {
      int delta = (codeLengths[i] - prevLengths[i]) & 0x0F;

      if (codeLengths[i] == 0 && delta == 0) {
        int runStart = i;
        while (i < numSymbols && codeLengths[i] == 0 && ((codeLengths[i] - prevLengths[i]) & 0x0F) == 0)
          ++i;
        int run = i - runStart;

        while (run > 0) {
          if (run >= 11) {
            int count = Math.Min(run, 138);
            rle.Add((18, 7, count - 11));
            run -= count;
          } else if (run >= 3) {
            rle.Add((17, 3, run - 3));
            run = 0;
          } else {
            rle.Add((0, 0, 0));
            --run;
          }
        }
      } else if (delta == 0) {
        rle.Add((0, 0, 0));
        int prevVal = codeLengths[i];
        ++i;

        int rep = 0;
        while (i < numSymbols && codeLengths[i] == prevVal
            && ((codeLengths[i] - prevLengths[i]) & 0x0F) == 0 && rep < 6) {
          ++rep;
          ++i;
        }

        while (rep >= 3) {
          int batch = Math.Min(rep, 6);
          rle.Add((16, 2, batch - 3));
          rep -= batch;
        }
        while (rep > 0) {
          rle.Add((0, 0, 0));
          --rep;
        }
      } else {
        rle.Add((delta, 0, 0));
        int prevVal = codeLengths[i];
        ++i;

        int rep = 0;
        while (i < numSymbols && codeLengths[i] == prevVal && rep < 6) {
          ++rep;
          ++i;
        }

        while (rep >= 3) {
          int batch = Math.Min(rep, 6);
          rle.Add((16, 2, batch - 3));
          rep -= batch;
        }
        while (rep > 0) {
          int nextDelta = (codeLengths[i - rep] - prevLengths[i - rep]) & 0x0F;
          rle.Add((nextDelta, 0, 0));
          --rep;
        }
      }
    }

    return rle;
  }

  private static void WriteRle(Rar3BitWriter writer,
      List<(int sym, int extraBits, int extraValue)> rle, Rar3HuffmanEncoder clEnc) {
    foreach (var (sym, extraBits, extraValue) in rle) {
      clEnc.EncodeSymbol(writer, sym);
      if (extraBits > 0)
        writer.WriteBits((uint)extraValue, extraBits);
    }
  }

  private static int GetLenSlot(int length, int maxSlots) {
    for (int i = Math.Min(maxSlots, LenBase.Length) - 1; i >= 0; --i) {
      if (length >= LenBase[i]) {
        int maxExtra = LenBits[i] > 0 ? (1 << LenBits[i]) - 1 : 0;
        if (length <= LenBase[i] + maxExtra)
          return i;
      }
    }
    return 0;
  }

  private static int GetDistSlot(int distance) {
    if (distance <= 4) return distance - 1;

    int d = distance - 1;
    int highBit = 31 - int.LeadingZeroCount(d);
    int bits = highBit - 1;
    int lowBit = (d >> bits) & 1;
    return bits * 2 + 2 + lowBit;
  }

  private static void EnsureNonEmpty(int[] freq) {
    for (int i = 0; i < freq.Length; ++i)
      if (freq[i] > 0) return;
    freq[0] = 1;
  }

  private enum TokenType { Literal, Match, RepeatLast, RepeatOffset }

  private struct Rar3Token {
    public TokenType Type;
    public byte Literal;
    public int Length;
    public int Distance;
    public int RepIndex;
  }
}
