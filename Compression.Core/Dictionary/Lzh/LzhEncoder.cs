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

    while (tokenIdx < tokens.Count) {
      var blockEnd = Math.Min(tokenIdx + LzhConstants.BlockSize, tokens.Count);
      var blockCount = blockEnd - tokenIdx;

      // Collect used symbols and frequencies
      var codeFreq = new int[LzhConstants.NumCodes];
      var maxPosSlot = -1;

      for (var i = tokenIdx; i < blockEnd; ++i) {
        var token = tokens[i];
        if (token.IsLiteral) {
          ++codeFreq[token.Value];
        } else {
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

      // Write trees using compact format
      WriteTree(bits, codeLengths);
      WriteTree(bits, posLengths);

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
    while (d > 1) { d >>= 1; slot++; }
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
      var key1 = heap.Keys[0]; var node1 = heap.Values[0]; heap.RemoveAt(0);
      var key2 = heap.Keys[0]; var node2 = heap.Values[0]; heap.RemoveAt(0);

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
      if (leftChild[node] == -1) {
        lengths[nodeSym[node]] = Math.Min(depth, maxBits);
      } else {
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

    while (kraftSum > kraftMax) {
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
  }

  internal static uint[] BuildCanonicalCodes(int[] lengths) {
    var maxLen = lengths.Prepend(0).Max();
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
  /// Writes a Huffman tree to the bitstream.
  /// Format: 16-bit numUsedSymbols, then for each used symbol:
  ///   16-bit symbol index + 4-bit code length.
  /// If only 1 symbol: 1-bit flag (1) + 16-bit symbol.
  /// If multiple: 1-bit flag (0) + 16-bit count + entries.
  /// </summary>
  private static void WriteTree(BitWriter<MsbBitOrder> bits, int[] lengths) {
    var usedSymbols = new List<(int sym, int len)>();
    for (var i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0)
        usedSymbols.Add((i, lengths[i]));

    if (usedSymbols.Count <= 1) {
      bits.WriteBits(1, 1); // single-symbol flag
      bits.WriteBits((uint)(usedSymbols.Count > 0 ? usedSymbols[0].sym : 0), 16);
      return;
    }

    bits.WriteBits(0, 1); // multi-symbol flag
    bits.WriteBits((uint)usedSymbols.Count, 16);
    foreach (var (sym, len) in usedSymbols) {
      bits.WriteBits((uint)sym, 16);
      bits.WriteBits((uint)len, 5); // code length (max 17 for position)
    }
  }

  private static int CountUsedSymbols(int[] lengths) => lengths.Count(value => value > 0);

}
