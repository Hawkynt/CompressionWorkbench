using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes LHA -lh1- compressed data.
/// Uses a 4KB sliding window with dynamic (adaptive) Huffman coding.
/// </summary>
/// <remarks>
/// The -lh1- format encodes 314 symbols: 256 literals (0-255) and
/// 58 length-position codes (256-313). The Huffman tree is rebuilt
/// periodically every <see cref="BlockSize"/> symbols based on
/// accumulated frequencies. Positions use a fixed 6-bit encoding
/// with extra bits.
/// </remarks>
public sealed class Lh1Decoder {
  private const int NChar = 256;
  private const int WindowSize = 4096;
  private const int WindowMask = WindowSize - 1;
  private const int Threshold = 2;
  private const int MaxMatch = 60;
  private const int NumCodes = NChar + MaxMatch - Threshold + 1; // 314
  private const int BlockSize = 4096;
  private const int PositionBits = 6;
  private const int PositionSlots = 64;

  private readonly BitBuffer<MsbBitOrder> _bits;

  /// <summary>
  /// Initializes a new <see cref="Lh1Decoder"/>.
  /// </summary>
  /// <param name="input">The stream containing -lh1- compressed data.</param>
  public Lh1Decoder(Stream input) {
    this._bits = new(input);
  }

  /// <summary>
  /// Decodes the compressed data.
  /// </summary>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] Decode(int originalSize) {
    var output = new byte[originalSize];
    var window = new byte[WindowSize];
    // LH1 initializes the window with spaces (0x20)
    Array.Fill(window, (byte)0x20);
    int windowPos = 0;
    int outPos = 0;

    // Frequency table for adaptive Huffman
    int[] freq = new int[NumCodes];
    Array.Fill(freq, 1);
    int symbolCount = 0;

    // Build initial Huffman tree
    (int[] codeTable, int tableBits) = BuildDecodeTable(freq);

    while (outPos < originalSize) {
      // Rebuild tree periodically
      if (symbolCount >= BlockSize) {
        // Halve frequencies to adapt (prevents overflow, forgets old data)
        for (int i = 0; i < NumCodes; ++i)
          freq[i] = (freq[i] + 1) >> 1;
        (codeTable, tableBits) = BuildDecodeTable(freq);
        symbolCount = 0;
      }

      int code = DecodeFromTable(codeTable, tableBits);
      ++freq[code];
      ++symbolCount;

      if (code < NChar) {
        byte b = (byte)code;
        output[outPos++] = b;
        window[windowPos] = b;
        windowPos = (windowPos + 1) & WindowMask;
      } else {
        int length = code - NChar + Threshold;
        int posHigh = (int)this._bits.ReadBits(PositionBits);
        int posLow = (int)this._bits.ReadBits(6);
        int position = (posHigh << 6) | posLow;

        int srcPos = (windowPos - position - 1 + WindowSize) & WindowMask;
        for (int j = 0; j < length && outPos < originalSize; ++j) {
          byte b = window[srcPos];
          output[outPos++] = b;
          window[windowPos] = b;
          windowPos = (windowPos + 1) & WindowMask;
          srcPos = (srcPos + 1) & WindowMask;
        }
      }
    }

    return output;
  }

  private int DecodeFromTable(int[] table, int maxBits) {
    this._bits.EnsureBits(maxBits);
    uint peekBits = this._bits.PeekBits(maxBits);

    int entry = table[(int)peekBits];
    if (entry < 0)
      throw new InvalidDataException("Failed to decode Huffman symbol in -lh1- data.");

    int symbol = entry & 0xFFFF;
    int codeLen = entry >> 16;
    this._bits.DropBits(codeLen);
    return symbol;
  }

  private static (int[] Table, int TableBits) BuildDecodeTable(int[] freq) {
    // Build canonical Huffman codes from frequencies
    int n = freq.Length;
    int[] codeLengths = BuildCodeLengths(freq, n, 16);

    int maxLen = 0;
    for (int i = 0; i < n; ++i)
      if (codeLengths[i] > maxLen)
        maxLen = codeLengths[i];

    if (maxLen == 0) {
      // All zero - shouldn't happen with freq>=1 init
      maxLen = 1;
      codeLengths[0] = 1;
    }

    int tableBits = Math.Min(maxLen, 12);
    int tableSize = 1 << tableBits;
    var table = new int[tableSize];
    table.AsSpan().Fill(-1);

    var blCount = new int[maxLen + 1];
    foreach (int v in codeLengths)
      if (v > 0 && v <= maxLen)
        ++blCount[v];

    var nextCode = new int[maxLen + 1];
    int code = 0;
    for (int bits = 1; bits <= maxLen; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    for (int sym = 0; sym < n; ++sym) {
      int len = codeLengths[sym];
      if (len == 0 || len > tableBits)
        continue;

      int symCode = nextCode[len]++;
      int fillBits = tableBits - len;
      int baseIdx = symCode << fillBits;
      int packedValue = sym | (len << 16);
      for (int fill = 0; fill < (1 << fillBits); ++fill)
        table[baseIdx + fill] = packedValue;
    }

    return (table, tableBits);
  }

  private static int[] BuildCodeLengths(int[] freq, int n, int maxBits) {
    // Package-merge algorithm simplified: build from frequencies
    // Use a simple heap-based Huffman tree construction
    var codeLengths = new int[n];

    // Count non-zero
    int activeCount = 0;
    for (int i = 0; i < n; ++i)
      if (freq[i] > 0)
        ++activeCount;

    if (activeCount <= 1) {
      // Find the one symbol (or pick 0)
      for (int i = 0; i < n; ++i)
        if (freq[i] > 0) {
          codeLengths[i] = 1;
          break;
        }
      return codeLengths;
    }

    // Build Huffman tree using priority queue
    // Node: (freq, depth, symbol or -1 for internal, left, right)
    var pq = new SortedList<(long Freq, int Order), int>();
    int order = 0;
    int nodeCount = 0;
    int capacity = 2 * n;
    int[] nodeLeft = new int[capacity];
    int[] nodeRight = new int[capacity];
    int[] nodeSymbol = new int[capacity]; // -1 for internal

    for (int i = 0; i < n; ++i) {
      if (freq[i] > 0) {
        int idx = nodeCount++;
        nodeLeft[idx] = -1;
        nodeRight[idx] = -1;
        nodeSymbol[idx] = i;
        pq.Add((freq[i], order++), idx);
      }
    }

    while (pq.Count > 1) {
      var key1 = pq.Keys[0];
      int n1 = pq[key1];
      pq.RemoveAt(0);

      var key2 = pq.Keys[0];
      int n2 = pq[key2];
      pq.RemoveAt(0);

      int parent = nodeCount++;
      nodeLeft[parent] = n1;
      nodeRight[parent] = n2;
      nodeSymbol[parent] = -1;
      pq.Add((key1.Freq + key2.Freq, order++), parent);
    }

    // Assign depths
    int root = pq.Values[0];
    AssignDepths(root, 0, nodeLeft, nodeRight, nodeSymbol, codeLengths, maxBits);

    return codeLengths;
  }

  private static void AssignDepths(int node, int depth,
      int[] left, int[] right, int[] symbol, int[] codeLengths, int maxBits) {
    if (symbol[node] >= 0) {
      codeLengths[symbol[node]] = Math.Min(depth, maxBits);
      return;
    }

    AssignDepths(left[node], depth + 1, left, right, symbol, codeLengths, maxBits);
    AssignDepths(right[node], depth + 1, left, right, symbol, codeLengths, maxBits);
  }
}
