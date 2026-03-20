namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Huffman encoder for RAR3 compression. Builds canonical codes from frequency counts
/// and encodes symbols using MSB-first bit output matching <see cref="Rar3Decoder"/>.
/// </summary>
internal sealed class Rar3HuffmanEncoder {
  private const int MaxCodeLength = 15;

  private int[] _codeLengths = [];
  private uint[] _codes = [];

  /// <summary>Gets the code lengths array.</summary>
  public int[] CodeLengths => this._codeLengths;

  /// <summary>
  /// Builds Huffman codes from frequency counts.
  /// </summary>
  public void Build(int[] frequencies, int numSymbols) {
    this._codeLengths = BuildCodeLengths(frequencies, numSymbols, MaxCodeLength);
    this._codes = BuildCanonicalCodes(this._codeLengths, numSymbols);
  }

  /// <summary>
  /// Writes a single symbol to the bit writer (MSB-first).
  /// </summary>
  public void EncodeSymbol(Rar3BitWriter writer, int symbol) {
    writer.WriteBits(this._codes[symbol], this._codeLengths[symbol]);
  }

  private static int[] BuildCodeLengths(int[] freq, int numSymbols, int maxBits) {
    var lengths = new int[numSymbols];
    var symbols = new List<(int sym, int freq)>();

    for (int i = 0; i < numSymbols; ++i)
      if (freq[i] > 0)
        symbols.Add((i, freq[i]));

    if (symbols.Count == 0) return lengths;
    if (symbols.Count == 1) {
      lengths[symbols[0].sym] = 1;
      return lengths;
    }

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
      if (node.sym >= 0) {
        lengths[node.sym] = Math.Max(depth, 1);
        return;
      }
      Walk(node.left, depth + 1);
      Walk(node.right, depth + 1);
    }
    Walk(root, 0);

    ClampAndFix(lengths, numSymbols, maxBits);
    return lengths;
  }

  private static void ClampAndFix(int[] lengths, int numSymbols, int maxBits) {
    for (int i = 0; i < numSymbols; ++i)
      if (lengths[i] > maxBits) lengths[i] = maxBits;

    long kraftMax = 1L << maxBits;
    long kraftSum = 0;
    for (int i = 0; i < numSymbols; ++i)
      if (lengths[i] > 0) kraftSum += kraftMax >> lengths[i];

    while (kraftSum > kraftMax) {
      for (int i = numSymbols - 1; i >= 0; --i) {
        if (lengths[i] > 0 && lengths[i] < maxBits) {
          kraftSum -= kraftMax >> lengths[i];
          ++lengths[i];
          kraftSum += kraftMax >> lengths[i];
          if (kraftSum <= kraftMax) break;
        }
      }
    }
  }

  /// <summary>
  /// Builds canonical Huffman codes (MSB-first, no bit reversal).
  /// </summary>
  private static uint[] BuildCanonicalCodes(int[] lengths, int numSymbols) {
    int maxLen = 0;
    foreach (int l in lengths)
      if (l > maxLen) maxLen = l;
    if (maxLen == 0) return new uint[numSymbols];

    var blCount = new int[maxLen + 1];
    foreach (int l in lengths)
      if (l > 0) ++blCount[l];

    var nextCode = new uint[maxLen + 1];
    uint code = 0;
    for (int b = 1; b <= maxLen; ++b) {
      code = (code + (uint)blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    // MSB-first: no bit reversal needed
    var codes = new uint[numSymbols];
    for (int i = 0; i < numSymbols; ++i)
      if (lengths[i] > 0)
        codes[i] = nextCode[lengths[i]]++;

    return codes;
  }
}
