using Compression.Core.BitIO;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Arj;

/// <summary>
/// Encodes data using ARJ compression (methods 1-3).
/// Uses LZSS with Huffman-coded literals/lengths and positions.
/// </summary>
public sealed class ArjEncoder {
  private const int NChar = 256;
  private const int Threshold = 3;
  private const int MaxMatch = 256;
  private const int NumCodes = NChar + MaxMatch - Threshold + 1; // 510
  private const int BlockSize = 16384;
  private const int MaxCodeBits = 16;

  private readonly int _windowSize;
  private readonly int _method;

  /// <summary>
  /// Initializes a new <see cref="ArjEncoder"/>.
  /// </summary>
  /// <param name="method">The ARJ compression method (1, 2, or 3).</param>
  public ArjEncoder(int method = 1) {
    this._method = method;
    this._windowSize = method == 1 ? 26624 : 2048;
  }

  /// <summary>
  /// Compresses data using ARJ encoding.
  /// </summary>
  /// <param name="data">The input data to compress.</param>
  /// <returns>The compressed data.</returns>
  public byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var tokens = GenerateTokens(data);

    using var output = new MemoryStream();
    var bits = new BitWriter<LsbBitOrder>(output);
    var tokenIdx = 0;

    while (tokenIdx < tokens.Count) {
      var blockEnd = Math.Min(tokenIdx + BlockSize, tokens.Count);
      var blockCount = blockEnd - tokenIdx;

      // Collect frequencies
      var codeFreq = new int[NumCodes];
      var maxPosSlot = 0;
      for (var i = tokenIdx; i < blockEnd; ++i) {
        var (isLit, val, len, dist) = tokens[i];
        if (isLit)
          ++codeFreq[val];
        else {
          var lengthCode = len - Threshold + NChar;
          ++codeFreq[lengthCode];
          var posSlot = GetPositionSlot(dist);
          if (posSlot > maxPosSlot) maxPosSlot = posSlot;
        }
      }

      var numPosSlots = maxPosSlot + 1;
      var posFreq = new int[numPosSlots];
      for (var i = tokenIdx; i < blockEnd; ++i) {
        var (isLit, _, _, dist) = tokens[i];
        if (!isLit)
          ++posFreq[GetPositionSlot(dist)];
      }

      var codeLengths = BuildCodeLengths(codeFreq, MaxCodeBits);
      var posLengths = BuildCodeLengths(posFreq, MaxCodeBits);

      // Write block count
      bits.WriteBits((uint)blockCount, 16);

      // Write character tree
      WriteCharTree(bits, codeLengths);

      // Write position tree
      WritePosTree(bits, posLengths);

      // Build codes and encode
      var codeCodes = BuildCanonicalCodes(codeLengths);
      var posCodes = BuildCanonicalCodes(posLengths);

      var codeSingle = CountUsed(codeLengths) <= 1;
      var posSingle = CountUsed(posLengths) <= 1;

      for (var i = tokenIdx; i < blockEnd; ++i) {
        var (isLit, val, len, dist) = tokens[i];
        if (isLit) {
          if (!codeSingle)
            WriteBitsReversed(bits, codeCodes[val], codeLengths[val]);
        } else {
          var lengthCode = len - Threshold + NChar;
          if (!codeSingle)
            WriteBitsReversed(bits, codeCodes[lengthCode], codeLengths[lengthCode]);

          var posSlot = GetPositionSlot(dist);
          if (!posSingle)
            WriteBitsReversed(bits, posCodes[posSlot], posLengths[posSlot]);

          if (posSlot > 1) {
            var extraBits = posSlot - 1;
            var extraValue = dist - (1 << extraBits);
            bits.WriteBits((uint)extraValue, extraBits);
          }
        }
      }

      tokenIdx = blockEnd;
    }

    bits.FlushBits();
    return output.ToArray();
  }

  private List<(bool IsLit, int Val, int Len, int Dist)> GenerateTokens(ReadOnlySpan<byte> data) {
    var tokens = new List<(bool, int, int, int)>();
    var matchFinder = new HashChainMatchFinder(this._windowSize);
    var pos = 0;

    while (pos < data.Length) {
      var match = matchFinder.FindMatch(data, pos, this._windowSize, MaxMatch, Threshold);
      if (match.Length >= Threshold) {
        tokens.Add((false, 0, match.Length, match.Distance - 1));
        for (var i = 1; i < match.Length && pos + i < data.Length; ++i)
          matchFinder.InsertPosition(data, pos + i);
        pos += match.Length;
      } else {
        tokens.Add((true, data[pos], 0, 0));
        ++pos;
      }
    }
    return tokens;
  }

  private static void WriteCharTree(BitWriter<LsbBitOrder> bits, int[] codeLengths) {
    var usedCount = 0;
    for (var i = 0; i < codeLengths.Length; ++i)
      if (codeLengths[i] > 0)
        ++usedCount;

    if (usedCount <= 1) {
      bits.WriteBits(0, 9); // num = 0 → single symbol mode
      var sym = 0;
      for (var i = 0; i < codeLengths.Length; ++i)
        if (codeLengths[i] > 0) { sym = i; break; }
      bits.WriteBits((uint)sym, 9);
      return;
    }

    // Find how many code length entries to write (last non-zero index + 1)
    var num = codeLengths.Length;
    while (num > 0 && codeLengths[num - 1] == 0) --num;

    bits.WriteBits((uint)num, 9);

    // Write code-length tree (for encoding the main code lengths)
    // Build code-length sequence with run-length encoding
    var clSequence = new List<int>();
    var idx = 0;
    while (idx < num) {
      if (codeLengths[idx] == 0) {
        // Count zero run
        var run = 0;
        while (idx + run < num && codeLengths[idx + run] == 0) ++run;
        if (run >= 20) {
          clSequence.Add(2); // code 2 = 20 + 9-bit count
          clSequence.Add(run - 20);
          idx += run;
        } else if (run >= 3) {
          clSequence.Add(1); // code 1 = 3 + 4-bit count
          clSequence.Add(run - 3);
          idx += run;
        } else {
          clSequence.Add(0); // code 0 = single zero
          idx += 1;
        }
      } else {
        clSequence.Add(codeLengths[idx] + 2);
        ++idx;
      }
    }

    // Determine which code-length symbols are used
    var clFreq = new int[19];
    for (var i = 0; i < clSequence.Count; i += (clSequence[i] <= 2 && i + 1 < clSequence.Count) ? 2 : 1) {
      var sym = clSequence[i];
      ++clFreq[sym];
      // Skip the extra data value for codes 1 and 2
    }

    var clLengths = BuildCodeLengths(clFreq, 7);
    // Write code-length tree header
    var clNum = clLengths.Length;
    while (clNum > 0 && clLengths[clNum - 1] == 0) --clNum;
    bits.WriteBits((uint)clNum, 5);
    if (clNum == 0) {
      // Single symbol in code-length tree
      var sym = 0;
      for (var i = 0; i < clFreq.Length; ++i)
        if (clFreq[i] > 0) { sym = i; break; }
      bits.WriteBits((uint)sym, 5);
    } else {
      for (var i = 0; i < clNum; ++i) {
        bits.WriteBits((uint)clLengths[i], 3);
        if (i == 2) {
          // After symbol 2, write skip count (2 bits)
          // We don't skip any
          bits.WriteBits(0, 2);
        }
      }
    }

    // Encode the code-length sequence using the code-length Huffman
    var clCodes = BuildCanonicalCodes(clLengths);
    var clSingle = CountUsed(clLengths) <= 1;

    var seqIdx = 0;
    while (seqIdx < clSequence.Count) {
      var sym = clSequence[seqIdx++];
      if (!clSingle)
        WriteBitsReversed(bits, clCodes[sym], clLengths[sym]);
      if (sym == 1 && seqIdx < clSequence.Count)
        bits.WriteBits((uint)clSequence[seqIdx++], 4);
      else if (sym == 2 && seqIdx < clSequence.Count)
        bits.WriteBits((uint)clSequence[seqIdx++], 9);
    }
  }

  private static void WritePosTree(BitWriter<LsbBitOrder> bits, int[] posLengths) {
    var usedCount = CountUsed(posLengths);
    if (usedCount <= 1) {
      bits.WriteBits(0, 5);
      var sym = 0;
      for (var i = 0; i < posLengths.Length; ++i)
        if (posLengths[i] > 0) { sym = i; break; }
      bits.WriteBits((uint)sym, 5);
      return;
    }

    var num = posLengths.Length;
    while (num > 0 && posLengths[num - 1] == 0) --num;
    bits.WriteBits((uint)num, 5);

    for (var i = 0; i < num; ++i)
      bits.WriteBits((uint)posLengths[i], 4);
  }

  private static int GetPositionSlot(int distance) {
    if (distance <= 1)
      return distance;
    var slot = 1;
    var d = distance;
    while (d > 1) { d >>= 1; ++slot; }
    return slot;
  }

  private static int[] BuildCodeLengths(int[] frequencies, int maxBits) {
    var n = frequencies.Length;
    var lengths = new int[n];
    var symbols = new List<(int symbol, int freq)>();

    for (var i = 0; i < n; ++i)
      if (frequencies[i] > 0)
        symbols.Add((i, frequencies[i]));

    if (symbols.Count == 0) return lengths;
    if (symbols.Count == 1) {
      lengths[symbols[0].symbol] = 1;
      return lengths;
    }

    // Build Huffman tree
    var pq = new SortedList<(long Freq, int Order), int>();
    var order = 0;
    var nodeCount = 0;
    var cap = 2 * n;
    var nodeLeft = new int[cap];
    var nodeRight = new int[cap];
    var nodeSym = new int[cap];
    Array.Fill(nodeSym, -1);

    foreach (var (sym, freq) in symbols) {
      var idx = nodeCount++;
      nodeLeft[idx] = -1;
      nodeRight[idx] = -1;
      nodeSym[idx] = sym;
      pq.Add((freq, order++), idx);
    }

    while (pq.Count > 1) {
      var k1 = pq.Keys[0]; var n1 = pq[k1]; pq.RemoveAt(0);
      var k2 = pq.Keys[0]; var n2 = pq[k2]; pq.RemoveAt(0);
      var parent = nodeCount++;
      nodeLeft[parent] = n1;
      nodeRight[parent] = n2;
      pq.Add((k1.Freq + k2.Freq, order++), parent);
    }

    var root = pq.Values[0];
    AssignDepths(root, 0, nodeLeft, nodeRight, nodeSym, lengths, maxBits);

    // Ensure Kraft inequality
    FixCodeLengths(lengths, maxBits);
    return lengths;
  }

  private static void AssignDepths(int node, int depth,
      int[] left, int[] right, int[] symbol, int[] lengths, int maxBits) {
    if (symbol[node] >= 0) {
      lengths[symbol[node]] = Math.Min(depth, maxBits);
      return;
    }
    AssignDepths(left[node], depth + 1, left, right, symbol, lengths, maxBits);
    AssignDepths(right[node], depth + 1, left, right, symbol, lengths, maxBits);
  }

  private static void FixCodeLengths(int[] lengths, int maxBits) {
    var kraftMax = 1L << maxBits;
    var kraftSum = lengths.Where(c => c > 0).Sum(c => kraftMax >> c);
    while (kraftSum > kraftMax)
      for (var i = lengths.Length - 1; i >= 0; --i) {
        if (lengths[i] <= 0 || lengths[i] >= maxBits) continue;
        kraftSum -= kraftMax >> lengths[i];
        ++lengths[i];
        kraftSum += kraftMax >> lengths[i];
        if (kraftSum <= kraftMax) break;
      }
  }

  private static uint[] BuildCanonicalCodes(int[] lengths) {
    var maxLen = lengths.Length > 0 ? lengths.Max() : 0;
    if (maxLen == 0) return new uint[lengths.Length];

    var blCount = new int[maxLen + 1];
    foreach (var v in lengths)
      if (v > 0) ++blCount[v];

    var nextCode = new uint[maxLen + 1];
    uint code = 0;
    for (var b = 1; b <= maxLen; ++b) {
      code = (code + (uint)blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    var codes = new uint[lengths.Length];
    for (var i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0) codes[i] = nextCode[lengths[i]]++;

    return codes;
  }

  private static void WriteBitsReversed(BitWriter<LsbBitOrder> bits, uint code, int len) {
    // Canonical codes are MSB-first; LSB-first bitwriter needs them reversed
    uint reversed = 0;
    for (var i = 0; i < len; ++i) {
      reversed = (reversed << 1) | (code & 1);
      code >>= 1;
    }
    bits.WriteBits(reversed, len);
  }

  private static int CountUsed(int[] lengths) => lengths.Count(v => v > 0);
}
