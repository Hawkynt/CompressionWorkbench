using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Arj;

/// <summary>
/// Encodes data using ARJ compression (methods 1-3).
/// Uses LZSS with Huffman-coded literals/lengths and positions.
/// Matches the real ARJ bitstream format (MSB-first bit packing,
/// three-level tree encoding compatible with 7-Zip and original ARJ).
/// </summary>
public sealed class ArjEncoder {
  // Character/length code count: 256 literals + (MaxMatch - Threshold + 1) length codes
  private const int NC = 256 + MaxMatch - Threshold + 1; // 510
  private const int Threshold = 3;
  private const int MaxMatch = 256;
  private const int NP = 17; // max position slots (MAXDICBIT+1 where MAXDICBIT=16)
  private const int NT = 19; // code-length tree symbols (CODE_BIT + 3)
  private const int BlockSize = 16384;
  private const int MaxCodeBits = 16;

  private readonly int _windowSize;

  // MSB-first bit buffer (matches original ARJ putbits)
  private ushort _bitBuf;
  private int _bitCount;
  private MemoryStream? _output;

  /// <summary>
  /// Initializes a new <see cref="ArjEncoder"/>.
  /// </summary>
  /// <param name="method">The ARJ compression method (1, 2, or 3).</param>
  public ArjEncoder(int method = 1) {
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

    this._output = new MemoryStream();
    this._bitBuf = 0;
    this._bitCount = 0;

    var tokenIdx = 0;

    while (tokenIdx < tokens.Count) {
      var blockEnd = Math.Min(tokenIdx + BlockSize, tokens.Count);
      var blockCount = blockEnd - tokenIdx;

      // Collect frequencies for char/length codes
      var cFreq = new int[NC];
      for (var i = tokenIdx; i < blockEnd; ++i) {
        var (isLit, val, len, _) = tokens[i];
        if (isLit)
          ++cFreq[val];
        else
          ++cFreq[len - Threshold + 256];
      }

      // Collect position frequencies
      var pFreq = new int[NP];
      for (var i = tokenIdx; i < blockEnd; ++i) {
        var (isLit, _, _, dist) = tokens[i];
        if (!isLit)
          ++pFreq[GetPositionSlot(dist)];
      }

      // Build Huffman trees
      var cLen = new byte[NC];
      var cCode = new ushort[NC];
      var cRoot = MakeTree(NC, cFreq, cLen, cCode);

      var ptLen = new byte[NP];
      var ptCode = new ushort[NP];

      // Write block size (16 bits, CODE_BIT)
      PutBits(16, (ushort)blockCount);

      if (cRoot >= NC) {
        // Multiple char symbols - need code-length tree
        var tFreq = new int[NT];
        CountTFreq(cLen, NC, tFreq);

        var tLen = new byte[NT];
        var tCode = new ushort[NT];
        var tRoot = MakeTree(NT, tFreq, tLen, tCode);

        if (tRoot >= NT)
          WritePtLen(tLen, NT, 5, 3); // TBIT=5, i_special=3
        else {
          PutBits(5, 0);
          PutBits(5, (ushort)tRoot);
        }

        WriteCLen(cLen, NC, tLen, tCode);
      } else {
        // Single char symbol
        PutBits(5, 0); // code-length tree: num=0
        PutBits(5, 0); // single code-length symbol = 0 (dummy)
        PutBits(9, 0); // char tree: num=0
        PutBits(9, (ushort)cRoot); // single char symbol
      }

      // Position tree
      var pRoot = MakeTree(NP, pFreq, ptLen, ptCode);
      if (pRoot >= NP)
        WritePtLen(ptLen, NP, 5, -1); // PBIT=5, no special
      else {
        PutBits(5, 0);
        PutBits(5, (ushort)pRoot);
      }

      // Encode tokens
      for (var i = tokenIdx; i < blockEnd; ++i) {
        var (isLit, val, len, dist) = tokens[i];
        if (isLit) {
          PutBits(cLen[val], cCode[val]);
        } else {
          var lengthCode = len - Threshold + 256;
          PutBits(cLen[lengthCode], cCode[lengthCode]);

          var posSlot = GetPositionSlot(dist);
          PutBits(ptLen[posSlot], ptCode[posSlot]);

          // Extra bits: low (posSlot-1) bits of dist
          if (posSlot > 1)
            PutBits(posSlot - 1, (ushort)dist);
        }
      }

      tokenIdx = blockEnd;
    }

    FlushBits();
    return this._output.ToArray();
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

  // -----------------------------------------------------------------------
  // MSB-first bit output (matches original ARJ putbits)
  // -----------------------------------------------------------------------

  private void PutBits(int n, ushort x) {
    if (n == 0) return;
    // Left-align x in 16 bits, then merge into bit buffer
    var shifted = (ushort)(x << (16 - n));
    this._bitBuf |= (ushort)(shifted >> this._bitCount);
    this._bitCount += n;

    if (this._bitCount >= 8) {
      this._output!.WriteByte((byte)(this._bitBuf >> 8));
      this._bitCount -= 8;

      if (this._bitCount >= 8) {
        this._output.WriteByte((byte)(this._bitBuf & 0xFF));
        this._bitCount -= 8;
        // Remaining bits from the original shifted value
        this._bitBuf = (ushort)(x << (16 - this._bitCount));
      } else {
        this._bitBuf <<= 8;
      }
    }
  }

  private void FlushBits() {
    if (this._bitCount > 0) {
      this._output!.WriteByte((byte)(this._bitBuf >> 8));
      if (this._bitCount > 8)
        this._output.WriteByte((byte)(this._bitBuf & 0xFF));
    }
  }

  // -----------------------------------------------------------------------
  // Position slot: count bits needed to represent distance
  // Matches original ARJ encode_p: qc=0; q=p; while(q) { q>>=1; qc++; }
  // -----------------------------------------------------------------------

  private static int GetPositionSlot(int distance) {
    if (distance == 0) return 0;
    var slot = 0;
    var d = distance;
    while (d > 0) { d >>= 1; ++slot; }
    return slot;
  }

  // -----------------------------------------------------------------------
  // Count code-length frequencies for the code-length tree (count_t_freq)
  // -----------------------------------------------------------------------

  private static void CountTFreq(byte[] cLen, int nc, int[] tFreq) {
    Array.Clear(tFreq);
    var n = nc;
    while (n > 0 && cLen[n - 1] == 0) --n;
    var i = 0;
    while (i < n) {
      var k = cLen[i++];
      if (k == 0) {
        var count = 1;
        while (i < n && cLen[i] == 0) { ++i; ++count; }
        if (count <= 2)
          tFreq[0] += count;
        else if (count <= 18)
          ++tFreq[1];
        else if (count == 19) {
          ++tFreq[0];
          ++tFreq[1];
        } else
          ++tFreq[2];
      } else
        ++tFreq[k + 2];
    }
  }

  // -----------------------------------------------------------------------
  // Write preliminary tree lengths (write_pt_len)
  // Uses 3 bits for lengths 0-6, unary (k-3 bits of 0xFFFE) for 7+
  // -----------------------------------------------------------------------

  private void WritePtLen(byte[] ptLen, int n, int nbit, int iSpecial) {
    while (n > 0 && ptLen[n - 1] == 0) --n;
    PutBits(nbit, (ushort)n);
    var i = 0;
    while (i < n) {
      var k = ptLen[i++];
      if (k <= 6)
        PutBits(3, k);
      else
        // Unary: (k-3) bits of 0xFFFE = (k-4) ones followed by a zero
        PutBits(k - 3, 0xFFFE);

      if (i == iSpecial) {
        // Skip count: how many of symbols 3..5 are zero (original: while(i<6&&pt_len[i]==0) i++)
        while (i < 6 && ptLen[i] == 0) ++i;
        PutBits(2, (ushort)(i - 3));
      }
    }
  }

  // -----------------------------------------------------------------------
  // Write char/length code lengths (write_c_len)
  // -----------------------------------------------------------------------

  private void WriteCLen(byte[] cLen, int nc, byte[] ptLen, ushort[] ptCode) {
    var n = nc;
    while (n > 0 && cLen[n - 1] == 0) --n;
    PutBits(9, (ushort)n); // CBIT = 9
    var i = 0;
    while (i < n) {
      var k = cLen[i++];
      if (k == 0) {
        var count = 1;
        while (i < n && cLen[i] == 0) { ++i; ++count; }
        if (count <= 2) {
          for (var j = 0; j < count; ++j)
            PutBits(ptLen[0], ptCode[0]);
        } else if (count <= 18) {
          PutBits(ptLen[1], ptCode[1]);
          PutBits(4, (ushort)(count - 3));
        } else if (count == 19) {
          PutBits(ptLen[0], ptCode[0]);
          PutBits(ptLen[1], ptCode[1]);
          PutBits(4, 15);
        } else {
          PutBits(ptLen[2], ptCode[2]);
          PutBits(9, (ushort)(count - 20)); // CBIT = 9
        }
      } else
        PutBits(ptLen[k + 2], ptCode[k + 2]);
    }
  }

  // -----------------------------------------------------------------------
  // Huffman tree construction (matches original ARJ make_tree)
  // -----------------------------------------------------------------------

  private static int MakeTree(int n, int[] freq, byte[] len, ushort[] code) {
    // Build min-heap of symbols with non-zero frequency
    var heap = new int[n + 1];
    var heapSize = 0;
    Array.Clear(len, 0, n);

    for (var i = 0; i < n; ++i)
      if (freq[i] > 0)
        heap[++heapSize] = i;

    if (heapSize < 2) {
      var sym = heapSize > 0 ? heap[1] : 0;
      code[sym] = 0;
      return sym;
    }

    // Internal nodes stored starting at index n
    var left = new int[2 * n];
    var right = new int[2 * n];
    var freqExt = new long[2 * n]; // long to avoid overflow
    for (var i = 0; i < n; ++i) freqExt[i] = freq[i];

    // Build min-heap
    for (var i = heapSize / 2; i >= 1; --i)
      DownHeap(heap, heapSize, i, freqExt);

    var sortOrder = new List<int>();
    var avail = n;

    while (heapSize > 1) {
      var i = heap[1];
      if (i < n) sortOrder.Add(i);
      heap[1] = heap[heapSize--];
      DownHeap(heap, heapSize, 1, freqExt);

      var j = heap[1];
      if (j < n) sortOrder.Add(j);

      var k = avail++;
      freqExt[k] = freqExt[i] + freqExt[j];
      left[k] = i;
      right[k] = j;
      heap[1] = k;
      DownHeap(heap, heapSize, 1, freqExt);
    }

    var root = heap[1];

    // Count depths
    var depth = new int[avail];
    CountDepth(root, 0, depth, left, right, n);

    // Count lengths per depth
    var lenCnt = new int[MaxCodeBits + 1];
    foreach (var sym in sortOrder) {
      var d = Math.Min(depth[sym], MaxCodeBits);
      ++lenCnt[d];
    }

    // Fix Kraft inequality (matches original make_len)
    long cum = 0;
    for (var i = MaxCodeBits; i > 0; --i)
      cum += (long)lenCnt[i] << (MaxCodeBits - i);
    while (cum != (1L << MaxCodeBits)) {
      --lenCnt[MaxCodeBits];
      for (var i = MaxCodeBits - 1; i > 0; --i) {
        if (lenCnt[i] != 0) {
          --lenCnt[i];
          lenCnt[i + 1] += 2;
          break;
        }
      }
      --cum;
    }

    // Assign lengths in sort order (longest first, matches original make_len)
    var sortIdx = 0;
    for (var i = MaxCodeBits; i > 0; --i) {
      var cnt = lenCnt[i];
      while (--cnt >= 0)
        len[sortOrder[sortIdx++]] = (byte)i;
    }

    // Build canonical codes (matches original make_code)
    MakeCode(n, len, code);

    return root;
  }

  private static void CountDepth(int node, int d, int[] depth, int[] left, int[] right, int leafCount) {
    if (node < leafCount) {
      depth[node] = d;
      return;
    }
    CountDepth(left[node], d + 1, depth, left, right, leafCount);
    CountDepth(right[node], d + 1, depth, left, right, leafCount);
  }

  private static void DownHeap(int[] heap, int heapSize, int i, long[] freq) {
    var k = heap[i];
    int j;
    while ((j = 2 * i) <= heapSize) {
      if (j < heapSize && freq[heap[j]] > freq[heap[j + 1]])
        ++j;
      if (freq[k] <= freq[heap[j]])
        break;
      heap[i] = heap[j];
      i = j;
    }
    heap[i] = k;
  }

  private static void MakeCode(int n, byte[] len, ushort[] code) {
    var lenCnt = new int[MaxCodeBits + 1];
    for (var i = 0; i < n; ++i)
      if (len[i] > 0) ++lenCnt[len[i]];

    var start = new ushort[MaxCodeBits + 2];
    start[1] = 0;
    for (var i = 1; i <= MaxCodeBits; ++i)
      start[i + 1] = (ushort)((start[i] + lenCnt[i]) << 1);

    for (var i = 0; i < n; ++i)
      if (len[i] > 0)
        code[i] = start[len[i]]++;
  }
}
