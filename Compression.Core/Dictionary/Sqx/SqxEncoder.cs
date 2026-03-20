using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// Encodes data using the real SQX LZH compression (310-symbol alphabet).
/// </summary>
/// <remarks>
/// Instance-based to support solid mode (persistent window + repeated offsets across files).
/// </remarks>
public sealed class SqxEncoder {
  private readonly int _dictSize;
  private readonly int[] _prevDists = new int[4];
  private int _prevDistIndex;
  private int _lastLen;
  private int _lastDist;

  /// <summary>
  /// Initializes a new SQX encoder.
  /// </summary>
  public SqxEncoder(int dictSize = SqxConstants.DefaultDictSize) {
    this._dictSize = dictSize;
  }

  /// <summary>
  /// Resets encoder state (for non-solid mode between files).
  /// </summary>
  public void Reset() {
    Array.Clear(this._prevDists);
    this._prevDistIndex = 0;
    this._lastLen = 0;
    this._lastDist = 0;
  }

  /// <summary>
  /// Compresses data using the SQX LZH algorithm.
  /// </summary>
  public byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return [];

    var output = new SqxBitWriter();
    var matchFinder = new HashChainMatchFinder(this._dictSize);
    int distSlots = SqxConstants.GetDistSlots(this._dictSize);

    int pos = 0;
    while (pos < data.Length) {
      var tokens = CollectBlockTokens(data, ref pos, matchFinder);
      EmitBlock(output, tokens, distSlots);
    }

    // End marker
    output.WriteBits(0, 16);
    return output.ToArray();
  }

  /// <summary>
  /// Static convenience method for non-solid single-file encoding.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, int dictSize) {
    var encoder = new SqxEncoder(dictSize);
    return encoder.Encode(data);
  }

  private List<Token> CollectBlockTokens(ReadOnlySpan<byte> data, ref int pos, HashChainMatchFinder matchFinder) {
    var tokens = new List<Token>();

    while (pos < data.Length && tokens.Count < SqxConstants.BlockSize) {
      var match = matchFinder.FindMatch(data, pos, this._dictSize, 258, SqxConstants.MinMatch);

      // Check if a repeated offset gives a useful match
      int bestRepIdx = -1;
      int bestRepLen = 0;
      for (int r = 0; r < 4; ++r) {
        int dist = this._prevDists[(this._prevDistIndex - r) & 3];
        if (dist == 0 || dist > pos) continue;
        int len = CountMatch(data, pos, dist);
        if (len >= 4 && len > bestRepLen) {
          bestRepLen = len;
          bestRepIdx = r;
        }
      }

      if (bestRepLen >= match.Length && bestRepLen >= 4) {
        // Use repeated offset
        int dist = this._prevDists[(this._prevDistIndex - bestRepIdx) & 3];
        tokens.Add(new Token(SqxConstants.RepStart + bestRepIdx, bestRepLen, dist));
        this._lastLen = bestRepLen;
        this._lastDist = dist;
        UpdateRepDists(dist);
        AdvanceMatchFinder(data, matchFinder, pos, bestRepLen);
        pos += bestRepLen;
      }
      else if (match.Length >= 4) {
        // Length 4+ match — token stores actual length; emission adjusts for distance
        tokens.Add(new Token(SqxConstants.LenStart, match.Length, match.Distance));
        this._lastLen = match.Length;
        this._lastDist = match.Distance;
        UpdateRepDists(match.Distance);
        AdvanceMatchFinder(data, matchFinder, pos, match.Length);
        pos += match.Length;
      }
      else if (match.Length == 3 && match.Distance <= SqxConstants.MaxDistLen3) {
        // Length-3 match
        int distCode = SqxConstants.GetLen3DistCode(match.Distance - 1);
        tokens.Add(new Token(SqxConstants.Len3Start + distCode, 3, match.Distance));
        this._lastLen = 3;
        this._lastDist = match.Distance;
        UpdateRepDists(match.Distance);
        AdvanceMatchFinder(data, matchFinder, pos, 3);
        pos += 3;
      }
      else if (match.Length == 2 && match.Distance <= SqxConstants.MaxDistLen2) {
        // Length-2 match
        int distCode = SqxConstants.GetLen2DistCode(match.Distance - 1);
        tokens.Add(new Token(SqxConstants.Len2Start + distCode, 2, match.Distance));
        this._lastLen = 2;
        this._lastDist = match.Distance;
        UpdateRepDists(match.Distance);
        AdvanceMatchFinder(data, matchFinder, pos, 2);
        pos += 2;
      }
      else {
        // Literal
        tokens.Add(new Token(data[pos], 0, 0));
        ++pos;
      }
    }

    return tokens;
  }

  private static int CountMatch(ReadOnlySpan<byte> data, int pos, int dist) {
    int len = 0;
    int maxLen = Math.Min(258, data.Length - pos);
    while (len < maxLen && data[pos + len] == data[pos - dist + len])
      ++len;
    return len;
  }

  private static void AdvanceMatchFinder(ReadOnlySpan<byte> data, HashChainMatchFinder mf, int pos, int len) {
    for (int i = 1; i < len && pos + i < data.Length; ++i)
      mf.InsertPosition(data, pos + i);
  }

  private void UpdateRepDists(int distance) {
    this._prevDists[this._prevDistIndex & 3] = distance;
    ++this._prevDistIndex;
  }

  private void EmitBlock(SqxBitWriter output, List<Token> tokens, int distSlots) {
    // Collect frequencies
    var mainFreq = new int[SqxConstants.NC];
    var distFreq = new int[distSlots];

    foreach (var t in tokens) {
      if (t.Symbol < 256) {
        ++mainFreq[t.Symbol];
      }
      else if (t.Symbol >= SqxConstants.RepStart && t.Symbol < SqxConstants.RepStart + SqxConstants.RepCodes) {
        ++mainFreq[t.Symbol];
        // Rep-offset matches also emit a length symbol (adjusted)
        int lenCode = GetAdjustedLenCode(t.Length, t.Distance);
        ++mainFreq[SqxConstants.LenStart + lenCode];
      }
      else if (t.Symbol >= SqxConstants.Len2Start && t.Symbol < SqxConstants.Len2Start + SqxConstants.Len2Codes) {
        ++mainFreq[t.Symbol];
      }
      else if (t.Symbol >= SqxConstants.Len3Start && t.Symbol < SqxConstants.Len3Start + SqxConstants.Len3Codes) {
        ++mainFreq[t.Symbol];
      }
      else if (t.Symbol == SqxConstants.LenStart) {
        // Length 4+ match: compute adjusted length code
        int lenCode = GetAdjustedLenCode(t.Length, t.Distance);
        ++mainFreq[SqxConstants.LenStart + lenCode];
        int distSym = GetDistSymbol(t.Distance);
        if (distSym < distSlots) ++distFreq[distSym];
      }
    }

    // Build Huffman trees
    int[] mainLens = BuildCodeLengths(mainFreq, SqxConstants.NC, SqxConstants.MainTreeMaxBits);
    int[] distLens = BuildCodeLengths(distFreq, distSlots, SqxConstants.MainTreeMaxBits);
    uint[] mainCodes = BuildCanonicalCodes(mainLens);
    uint[] distCodes = BuildCanonicalCodes(distLens);

    // Encode main+dist tree code lengths via pre-tree
    var mainRle = RunLengthEncode(mainLens);
    var distRle = RunLengthEncode(distLens);

    // Build pre-tree from RLE symbols
    var preFreq = new int[SqxConstants.PreTreeSymbols];
    foreach (var (sym, _, _) in mainRle) ++preFreq[sym];
    foreach (var (sym, _, _) in distRle) ++preFreq[sym];
    int[] preLens = BuildCodeLengths(preFreq, SqxConstants.PreTreeSymbols, SqxConstants.PreTreeMaxBits);
    uint[] preCodes = BuildCanonicalCodes(preLens);

    // Write block header (symbol count)
    // Account for extra symbols emitted by rep-offset tokens (length symbol)
    int totalSymbols = 0;
    foreach (var t in tokens) {
      ++totalSymbols;
      if (t.Symbol >= SqxConstants.RepStart && t.Symbol < SqxConstants.RepStart + SqxConstants.RepCodes)
        ++totalSymbols; // length symbol
    }
    output.WriteBits(totalSymbols, 16);

    // Write pre-tree (19 x 4-bit raw code lengths)
    for (int i = 0; i < SqxConstants.PreTreeSymbols; ++i)
      output.WriteBits(preLens[i], 4);

    // Write main tree code lengths via pre-tree
    EmitRle(output, mainRle, preCodes, preLens);

    // Write distance tree code lengths via pre-tree
    EmitRle(output, distRle, preCodes, preLens);

    // Write compressed tokens
    foreach (var t in tokens)
      EmitToken(output, t, mainCodes, mainLens, distCodes, distLens, distSlots);
  }

  private void EmitToken(SqxBitWriter output, Token t,
      uint[] mainCodes, int[] mainLens,
      uint[] distCodes, int[] distLens, int distSlots) {
    if (t.Symbol < 256) {
      output.WriteBits(mainCodes[t.Symbol], mainLens[t.Symbol]);
    }
    else if (t.Symbol >= SqxConstants.RepStart && t.Symbol < SqxConstants.RepStart + SqxConstants.RepCodes) {
      // Emit rep-offset symbol
      output.WriteBits(mainCodes[t.Symbol], mainLens[t.Symbol]);
      // Emit length symbol (uses adjusted length)
      int lenCode = GetAdjustedLenCode(t.Length, t.Distance);
      int lenSym = SqxConstants.LenStart + lenCode;
      output.WriteBits(mainCodes[lenSym], mainLens[lenSym]);
      if (SqxConstants.LenExtraBits[lenCode] > 0) {
        int adjLen = t.Length - 4;
        if (t.Distance > SqxConstants.MaxDistLen3) --adjLen;
        if (t.Distance > SqxConstants.MaxDistLen4) --adjLen;
        int extra = adjLen - SqxConstants.LenOffsets[lenCode];
        output.WriteBits(extra, SqxConstants.LenExtraBits[lenCode]);
      }
    }
    else if (t.Symbol >= SqxConstants.Len2Start && t.Symbol < SqxConstants.Len2Start + SqxConstants.Len2Codes) {
      output.WriteBits(mainCodes[t.Symbol], mainLens[t.Symbol]);
      int idx = t.Symbol - SqxConstants.Len2Start;
      if (SqxConstants.Len2ExtraBits[idx] > 0) {
        int extra = (t.Distance - 1) - SqxConstants.Len2Offsets[idx];
        output.WriteBits(extra, SqxConstants.Len2ExtraBits[idx]);
      }
    }
    else if (t.Symbol >= SqxConstants.Len3Start && t.Symbol < SqxConstants.Len3Start + SqxConstants.Len3Codes) {
      output.WriteBits(mainCodes[t.Symbol], mainLens[t.Symbol]);
      int idx = t.Symbol - SqxConstants.Len3Start;
      if (SqxConstants.Len3ExtraBits[idx] > 0) {
        int extra = (t.Distance - 1) - SqxConstants.Len3Offsets[idx];
        output.WriteBits(extra, SqxConstants.Len3ExtraBits[idx]);
      }
    }
    else if (t.Symbol == SqxConstants.LenStart) {
      int lenCode = GetAdjustedLenCode(t.Length, t.Distance);
      int sym = SqxConstants.LenStart + lenCode;
      output.WriteBits(mainCodes[sym], mainLens[sym]);

      if (lenCode == SqxConstants.LenCodes - 1) {
        // Special code 308: 14 raw bits (pre-adjustment length + 257)
        output.WriteBits(t.Length - 257, 14);
      }
      else if (SqxConstants.LenExtraBits[lenCode] > 0) {
        int adjLen = t.Length - 4;
        if (t.Distance > SqxConstants.MaxDistLen3) --adjLen;
        if (t.Distance > SqxConstants.MaxDistLen4) --adjLen;
        int extra = adjLen - SqxConstants.LenOffsets[lenCode];
        output.WriteBits(extra, SqxConstants.LenExtraBits[lenCode]);
      }

      // Emit distance
      int distSym = GetDistSymbol(t.Distance);
      if (distSym >= distSlots) distSym = distSlots - 1;
      output.WriteBits(distCodes[distSym], distLens[distSym]);
      if (distSym >= 2) {
        int extraBits = distSym - 1;
        int extraVal = t.Distance - (1 << extraBits);
        output.WriteBits(extraVal, extraBits);
      }
    }
  }

  private static int GetAdjustedLenCode(int length, int distance) {
    int adjLen = length - 4;
    if (distance > SqxConstants.MaxDistLen3) --adjLen;
    if (distance > SqxConstants.MaxDistLen4) --adjLen;
    if (adjLen < 0) adjLen = 0;
    // Code 24 (sym 308) is the escape code for very long lengths (14 raw bits)
    if (adjLen > 224 + (1 << SqxConstants.LenExtraBits[23]) - 1)
      return 24;
    for (int i = 23; i >= 0; --i) {
      if (adjLen >= SqxConstants.LenOffsets[i])
        return i;
    }
    return 0;
  }

  private static int GetDistSymbol(int distance) {
    if (distance <= 0) return 0;
    if (distance == 1) return 0;
    if (distance == 2) return 1;
    int sym = 1;
    int d = distance;
    while (d > 1) { d >>= 1; ++sym; }
    return sym;
  }

  private static void EmitRle(SqxBitWriter output,
      List<(int Symbol, int ExtraBits, int ExtraValue)> rle,
      uint[] preCodes, int[] preLens) {
    foreach (var (sym, extraBits, extraValue) in rle) {
      output.WriteBits(preCodes[sym], preLens[sym]);
      if (extraBits > 0)
        output.WriteBits(extraValue, extraBits);
    }
  }

  private static List<(int Symbol, int ExtraBits, int ExtraValue)> RunLengthEncode(int[] lengths) {
    var result = new List<(int, int, int)>();
    int i = 0;

    while (i < lengths.Length) {
      int value = lengths[i];
      if (value == 0) {
        int count = 1;
        while (i + count < lengths.Length && lengths[i + count] == 0)
          ++count;

        int remaining = count;
        while (remaining > 0) {
          if (remaining >= 11) {
            int run = Math.Min(remaining, 138);
            result.Add((18, 7, run - 11));
            remaining -= run;
          }
          else if (remaining >= 3) {
            result.Add((17, 3, remaining - 3));
            remaining = 0;
          }
          else {
            result.Add((0, 0, 0));
            --remaining;
          }
        }
        i += count;
      }
      else {
        result.Add((value, 0, 0));
        ++i;

        int count = 0;
        while (i + count < lengths.Length && lengths[i + count] == value)
          ++count;

        int remaining = count;
        while (remaining >= 3) {
          int run = Math.Min(remaining, 6);
          result.Add((16, 2, run - 3));
          remaining -= run;
        }
        while (remaining > 0) {
          result.Add((value, 0, 0));
          --remaining;
        }
        i += count;
      }
    }

    return result;
  }

  private static int[] BuildCodeLengths(int[] freq, int numSymbols, int maxBits) {
    var lengths = new int[numSymbols];
    var symbols = new List<(int sym, int freq)>();
    for (int i = 0; i < numSymbols; ++i)
      if (freq[i] > 0) symbols.Add((i, freq[i]));

    if (symbols.Count == 0) return lengths;
    if (symbols.Count == 1) { lengths[symbols[0].sym] = 1; return lengths; }

    var pq = new PriorityQueue<int, long>();
    var nodes = new List<(long freq, int sym, int left, int right)>();
    for (int i = 0; i < symbols.Count; ++i) {
      nodes.Add((symbols[i].freq, symbols[i].sym, -1, -1));
      pq.Enqueue(i, symbols[i].freq);
    }
    while (pq.Count > 1) {
      pq.TryDequeue(out int a, out long fa);
      pq.TryDequeue(out int b, out long fb);
      int newIdx = nodes.Count;
      nodes.Add((fa + fb, -1, a, b));
      pq.Enqueue(newIdx, fa + fb);
    }
    pq.TryDequeue(out int root, out _);

    void Walk(int idx, int depth) {
      var node = nodes[idx];
      if (node.sym >= 0) { lengths[node.sym] = Math.Clamp(depth, 1, maxBits); return; }
      Walk(node.left, depth + 1);
      Walk(node.right, depth + 1);
    }
    Walk(root, 0);

    return lengths;
  }

  private static uint[] BuildCanonicalCodes(int[] lengths) {
    int maxLen = 0;
    foreach (int l in lengths) if (l > maxLen) maxLen = l;
    if (maxLen == 0) return new uint[lengths.Length];

    var blCount = new int[maxLen + 1];
    foreach (int l in lengths) if (l > 0) ++blCount[l];

    var nextCode = new uint[maxLen + 1];
    uint code = 0;
    for (int b = 1; b <= maxLen; ++b) {
      code = (code + (uint)blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    var codes = new uint[lengths.Length];
    for (int i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0) codes[i] = nextCode[lengths[i]]++;
    return codes;
  }

  private readonly record struct Token(int Symbol, int Length, int Distance);
}

internal sealed class SqxBitWriter {
  private readonly List<byte> _output = [];
  private int _bitPos;
  private byte _current;

  public void WriteBits(int value, int count) {
    for (int i = count - 1; i >= 0; --i) {
      if (((value >> i) & 1) != 0)
        this._current |= (byte)(1 << (7 - this._bitPos));
      if (++this._bitPos == 8) {
        this._output.Add(this._current);
        this._current = 0;
        this._bitPos = 0;
      }
    }
  }

  public void WriteBits(uint value, int count) => WriteBits((int)value, count);

  public byte[] ToArray() {
    if (this._bitPos > 0)
      this._output.Add(this._current);
    return [.. this._output];
  }
}
