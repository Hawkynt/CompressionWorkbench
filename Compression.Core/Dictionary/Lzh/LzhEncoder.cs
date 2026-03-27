using Compression.Core.BitIO;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Encodes data using LZH compression (methods -lh5-, -lh6-, -lh7-).
/// Uses LZSS with Huffman-coded literals/lengths and positions.
/// </summary>
public sealed partial class LzhEncoder {
  private readonly int _positionBits;
  private readonly int _windowSize;

  /// <summary>
  /// Initializes a new <see cref="LzhEncoder"/>.
  /// </summary>
  /// <param name="positionBits">Number of position bits (13 for lh5, 15 for lh6, 16 for lh7).</param>
  public LzhEncoder(int positionBits = LzhConstants.Lh5PositionBits) {
    this._positionBits = positionBits;
    this._windowSize = 1 << positionBits;
  }

  /// <summary>
  /// Compresses data using LZH encoding.
  /// </summary>
  /// <param name="data">The input data to compress.</param>
  /// <returns>The compressed data.</returns>
  public byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var tokens = this.GenerateTokens(data);

    using var output = new MemoryStream();
    var bits = new BitWriter<MsbBitOrder>(output);
    var tokenIdx = 0;

    var pBit = this._positionBits switch {
      12 => 4, // lh4
      13 => 4, // lh5
      15 => 5, // lh6
      16 => 5, // lh7
      _ => 4
    };

    while (tokenIdx < tokens.Count) {
      var blockEnd = Math.Min(tokenIdx + LzhConstants.BlockSize, tokens.Count);
      var blockCount = blockEnd - tokenIdx;

      // Collect used symbols and frequencies
      var codeFreq = new int[LzhConstants.NumCodes];
      var maxPosSlot = -1;

      for (var i = tokenIdx; i < blockEnd; ++i) {
        var token = tokens[i];
        if (token.IsLiteral)
          ++codeFreq[token.Value];
        else {
          var lengthCode = token.Length - LzhConstants.Threshold + LzhConstants.NChar;
          ++codeFreq[lengthCode];
          var posSlot = GetPositionSlot(token.Distance);
          if (posSlot > maxPosSlot) maxPosSlot = posSlot;
        }
      }

      var numPosSlots = maxPosSlot + 1;
      var posFreq = new int[Math.Max(numPosSlots, 1)];
      for (var i = tokenIdx; i < blockEnd; ++i)
        if (!tokens[i].IsLiteral)
          ++posFreq[GetPositionSlot(tokens[i].Distance)];

      var codeLengths = BuildCodeLengths(codeFreq, LzhConstants.MaxCodeBits);
      var posLengths = BuildCodeLengths(posFreq, LzhConstants.MaxPositionBits);

      // Determine if trees are single-symbol (decoder won't read Huffman bits)
      var codeSingle = CountUsedSymbols(codeLengths) <= 1;
      var posSingle = CountUsedSymbols(posLengths) <= 1;

      // Write block size (16 bits)
      bits.WriteBits((uint)blockCount, 16);

      // Write trees using standard LZH encoding
      WriteCTree(bits, codeLengths);
      WritePTree(bits, posLengths, pBit);

      var codeCodes = BuildCanonicalCodes(codeLengths);
      var posCodes = BuildCanonicalCodes(posLengths);

      for (var i = tokenIdx; i < blockEnd; ++i) {
        var token = tokens[i];
        if (token.IsLiteral) {
          if (!codeSingle)
            bits.WriteBits(codeCodes[token.Value], codeLengths[token.Value]);
        } else {
          var lengthCode = token.Length - LzhConstants.Threshold + LzhConstants.NChar;
          if (!codeSingle)
            bits.WriteBits(codeCodes[lengthCode], codeLengths[lengthCode]);

          var posSlot = GetPositionSlot(token.Distance);
          if (!posSingle)
            bits.WriteBits(posCodes[posSlot], posLengths[posSlot]);

          if (posSlot <= 1)
            continue;

          var extraBits = posSlot - 1;
          var extraValue = token.Distance - (1 << extraBits);
          bits.WriteBits((uint)extraValue, extraBits);
        }
      }

      tokenIdx = blockEnd;
    }

    bits.FlushBits();
    return output.ToArray();
  }

  private List<LzhToken> GenerateTokens(ReadOnlySpan<byte> data) {
    var tokens = new List<LzhToken>();
    var matchFinder = new HashChainMatchFinder(this._windowSize);
    var maxLength = LzhConstants.MaxMatch;
    var pos = 0;

    while (pos < data.Length) {
      var match = matchFinder.FindMatch(data, pos, this._windowSize, maxLength, LzhConstants.Threshold);

      if (match.Length >= LzhConstants.Threshold) {
        tokens.Add(LzhToken.CreateMatch(match.Length, match.Distance - 1));
        for (var i = 1; i < match.Length && pos + i < data.Length; ++i)
          matchFinder.InsertPosition(data, pos + i);

        pos += match.Length;
      } else {
        tokens.Add(LzhToken.CreateLiteral(data[pos]));
        ++pos;
      }
    }

    return tokens;
  }

  internal static int GetPositionSlot(int distance) {
    if (distance <= 1)
      return distance;

    var slot = 1;
    var d = distance;
    while (d > 1) { d >>= 1; ++slot; }
    return slot;
  }

  internal static int[] BuildCodeLengths(int[] frequencies, int maxBits) {
    var n = frequencies.Length;
    var lengths = new int[n];
    var symbols = new List<(int symbol, int freq)>();

    for (var i = 0; i < n; ++i)
      if (frequencies[i] > 0)
        symbols.Add((i, frequencies[i]));

    switch (symbols.Count) {
      case 0: return lengths;
      case 1:
        lengths[symbols[0].symbol] = 1;
        return lengths;
    }

    var nodeCount = symbols.Count * 2 - 1;
    var leftChild = new int[nodeCount];
    var rightChild = new int[nodeCount];
    leftChild.AsSpan().Fill(-1);
    rightChild.AsSpan().Fill(-1);
    var nodeSym = new int[nodeCount];
    nodeSym.AsSpan().Fill(-1);

    var heap = new SortedList<long, int>();
    var tieBreaker = 0L;
    for (var i = 0; i < symbols.Count; ++i) {
      nodeSym[i] = symbols[i].symbol;
      heap.Add(((long)symbols[i].freq << 32) | (tieBreaker++), i);
    }

    var nextNode = symbols.Count;
    while (heap.Count > 1) {
      var key1 = heap.Keys[0];
      var node1 = heap.Values[0];
      heap.RemoveAt(0);
      var key2 = heap.Keys[0];
      var node2 = heap.Values[0];
      heap.RemoveAt(0);

      var parent = nextNode++;
      leftChild[parent] = node1;
      rightChild[parent] = node2;

      var parentFreq = (int)(key1 >> 32) + (int)(key2 >> 32);
      heap.Add(((long)parentFreq << 32) | (tieBreaker++), parent);
    }

    var root = heap.Values[0];
    var stack = new Stack<(int node, int depth)>();
    stack.Push((root, 0));
    while (stack.Count > 0) {
      var (node, depth) = stack.Pop();
      if (leftChild[node] == -1)
        lengths[nodeSym[node]] = Math.Min(depth, maxBits);
      else {
        if (leftChild[node] >= 0) stack.Push((leftChild[node], depth + 1));
        if (rightChild[node] >= 0) stack.Push((rightChild[node], depth + 1));
      }
    }

    FixCodeLengths(lengths, maxBits);
    return lengths;
  }

  private static void FixCodeLengths(int[] lengths, int maxBits) {
    var kraftMax = 1L << maxBits;
    var kraftSum = lengths.Where(code => code > 0).Sum(code => kraftMax >> code);

    while (kraftSum > kraftMax)
      for (var i = lengths.Length - 1; i >= 0; --i) {
        if (lengths[i] <= 0 || lengths[i] >= maxBits)
          continue;

        kraftSum -= kraftMax >> lengths[i];
        ++lengths[i];
        kraftSum += kraftMax >> lengths[i];
        if (kraftSum <= kraftMax) 
          break;
      }
  }

  internal static uint[] BuildCanonicalCodes(int[] lengths) {
    var maxLen = lengths.Length > 0 ? lengths.Max() : 0;
    if (maxLen == 0)
      return new uint[lengths.Length];

    var blCount = new int[maxLen + 1];
    foreach (var value in lengths)
      if (value > 0) 
        ++blCount[value];

    var nextCode = new uint[maxLen + 1];
    var code = 0U;
    for (var b = 1; b <= maxLen; ++b) {
      code = (code + (uint)blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    var codes = new uint[lengths.Length];
    for (var i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0) codes[i] = nextCode[lengths[i]]++;

    return codes;
  }

  /// <summary>
  /// Writes the C tree (literal/length codes) using standard LZH encoding.
  /// First writes a T tree (code-length tree), then encodes C tree lengths using T.
  /// </summary>
  private static void WriteCTree(BitWriter<MsbBitOrder> bits, int[] codeLengths) {
    // Determine actual number of symbols to transmit
    var numC = codeLengths.Length;
    while (numC > 0 && codeLengths[numC - 1] == 0) --numC;
    if (numC == 0) numC = 1; // at least 1

    // Check for single symbol
    var singleSym = -1;
    var usedCount = 0;
    for (var i = 0; i < codeLengths.Length; ++i)
      if (codeLengths[i] > 0) { singleSym = i; ++usedCount; }

    if (usedCount <= 1) {
      // Single-symbol C tree: write empty T tree, then CNUM=0, then 9-bit symbol
      WritePtTree(bits, new int[LzhConstants.NumCodeLengthSymbols], 5, 3);
      bits.WriteBits(0, 9);
      bits.WriteBits((uint)(usedCount > 0 ? singleSym : 0), 9);
      return;
    }

    // Encode C tree code lengths using run-length encoding into T-tree alphabet:
    // T-alphabet: 0 = zero length, 1 = run of (3 + next 4 bits) zeros,
    //             2 = run of (20 + next 9 bits) zeros, 3..18 = actual length (value - 2)
    var tSymbols = new List<(int sym, int extraBits, int extraValue)>();
    var i2 = 0;
    while (i2 < numC) {
      if (codeLengths[i2] == 0) {
        // Count consecutive zeros
        var zeroRun = 0;
        while (i2 + zeroRun < numC && codeLengths[i2 + zeroRun] == 0) ++zeroRun;

        var remaining = zeroRun;
        while (remaining > 0) {
          if (remaining >= 20) {
            var count = Math.Min(remaining, 20 + 511); // max for code 2
            tSymbols.Add((2, 9, count - 20));
            remaining -= count;
          } else if (remaining >= 3) {
            var count = Math.Min(remaining, 3 + 15); // max for code 1
            tSymbols.Add((1, 4, count - 3));
            remaining -= count;
          } else {
            tSymbols.Add((0, 0, 0));
            --remaining;
          }
        }
        i2 += zeroRun;
      } else {
        tSymbols.Add((codeLengths[i2] + 2, 0, 0)); // actual length + 2
        ++i2;
      }
    }

    // Build T tree from frequencies
    var tFreq = new int[LzhConstants.NumCodeLengthSymbols];
    foreach (var (sym, _, _) in tSymbols)
      tFreq[sym]++;

    var tLengths = BuildCodeLengths(tFreq, 7); // T tree max code length = 7

    // Write T tree header
    WritePtTree(bits, tLengths, 5, 3);

    // Write CNUM
    bits.WriteBits((uint)numC, 9);

    // Encode C tree lengths using T tree
    var tCodes = BuildCanonicalCodes(tLengths);
    var tSingleSym = -1;
    var tUsed = 0;
    for (var j = 0; j < tLengths.Length; ++j)
      if (tLengths[j] > 0) { tSingleSym = j; ++tUsed; }
    var tIsSingle = tUsed <= 1;

    foreach (var (sym, extraBitCount, extraValue) in tSymbols) {
      if (!tIsSingle)
        bits.WriteBits(tCodes[sym], tLengths[sym]);
      if (extraBitCount > 0)
        bits.WriteBits((uint)extraValue, extraBitCount);
    }
  }

  /// <summary>
  /// Writes a position tree using standard LZH encoding (same format as T tree).
  /// </summary>
  private static void WritePTree(BitWriter<MsbBitOrder> bits, int[] posLengths, int pBit) {
    WritePtTree(bits, posLengths, pBit, pBit);
  }

  /// <summary>
  /// Writes a PT-style tree (used for both the T tree and P tree).
  /// Format: n_sym (nBit bits), then for each symbol:
  ///   3-bit length, with unary extension for lengths >= 7.
  ///   After index 2 (for T tree only, when specialBit == 3): 2-bit skip count.
  /// If n_sym == 0: single symbol written in nBit bits.
  /// </summary>
  private static void WritePtTree(BitWriter<MsbBitOrder> bits, int[] lengths, int nBit, int specialBit) {
    // Determine actual count to transmit
    var numSym = lengths.Length;
    while (numSym > 0 && lengths[numSym - 1] == 0) --numSym;

    // Check for single symbol
    var singleSym = -1;
    var usedCount = 0;
    for (var i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0) { singleSym = i; ++usedCount; }

    if (usedCount <= 1) {
      bits.WriteBits(0, nBit); // n_sym = 0 means single symbol
      bits.WriteBits((uint)(usedCount > 0 ? singleSym : 0), nBit);
      return;
    }

    bits.WriteBits((uint)numSym, nBit);

    for (var i = 0; i < numSym; ++i) {
      var len = lengths[i];
      if (len < 7) {
        bits.WriteBits((uint)len, 3);
      } else {
        // Write 7 in 3 bits (=0b111), then (len-7) '1' bits, then a '0' bit
        bits.WriteBits(7, 3);
        for (var j = 0; j < len - 7; ++j)
          bits.WriteBits(1, 1);
        bits.WriteBits(0, 1);
      }

      // After symbol index 2 in T tree (specialBit==3), write 2-bit skip count
      if (i == 2 && specialBit == 3) {
        // Count how many of the next symbols have zero length
        var skipCount = 0;
        while (i + 1 + skipCount < numSym && skipCount < 3 && lengths[i + 1 + skipCount] == 0)
          ++skipCount;
        bits.WriteBits((uint)skipCount, 2);
        i += skipCount; // skip those zero-length symbols
      }
    }
  }

  private static int CountUsedSymbols(int[] lengths) => lengths.Count(value => value > 0);

}
