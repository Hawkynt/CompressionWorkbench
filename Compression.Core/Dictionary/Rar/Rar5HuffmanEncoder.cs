namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Huffman encoder for RAR5 compression. Builds canonical codes from frequency counts
/// and encodes symbols using LSB-first bit output matching <see cref="Rar5HuffmanDecoder"/>.
/// </summary>
internal sealed class Rar5HuffmanEncoder {
  private int[] _codeLengths = [];
  private uint[] _codes = [];
  private int _numSymbols;

  /// <summary>Gets the code lengths array.</summary>
  public int[] CodeLengths => this._codeLengths;

  /// <summary>
  /// Builds Huffman codes from frequency counts.
  /// </summary>
  /// <param name="frequencies">Per-symbol frequency counts.</param>
  /// <param name="numSymbols">Number of symbols.</param>
  /// <param name="maxBits">Maximum code length (default 15).</param>
  public void Build(int[] frequencies, int numSymbols, int maxBits = Rar5Constants.MaxCodeLength) {
    this._numSymbols = numSymbols;
    this._codeLengths = BuildCodeLengths(frequencies, numSymbols, maxBits);
    this._codes = BuildCanonicalCodes(this._codeLengths, numSymbols);
  }

  /// <summary>
  /// Writes a single symbol to the bit writer.
  /// </summary>
  public void EncodeSymbol(Rar5BitWriter writer, int symbol) {
    writer.WriteBits(this._codes[symbol], this._codeLengths[symbol]);
  }

  /// <summary>
  /// Writes the code-length table using the code-length pre-tree with RLE encoding,
  /// matching the format expected by <see cref="Rar5Decoder.ReadTables"/>.
  /// </summary>
  /// <param name="writer">The bit writer.</param>
  /// <param name="codeLengths">The code lengths to serialize.</param>
  /// <param name="numSymbols">Number of symbols in the table.</param>
  /// <param name="clEncoder">The code-length pre-tree encoder (20 symbols).</param>
  public static void WriteCodeLengths(Rar5BitWriter writer, int[] codeLengths, int numSymbols,
      Rar5HuffmanEncoder clEncoder) {
    // RLE encode the code lengths using symbols 0-15 (direct) and 16-18 (run-length)
    var i = 0;
    while (i < numSymbols) {
      if (codeLengths[i] == 0) {
        // Count consecutive zeros
        var run = 1;
        while (i + run < numSymbols && codeLengths[i + run] == 0) ++run;
        i += run;

        while (run > 0) {
          if (run >= 11) {
            var count = Math.Min(run, 138);
            clEncoder.EncodeSymbol(writer, 18);
            writer.WriteBits((uint)(count - 11), 7);
            run -= count;
          }
          else if (run >= 3) {
            clEncoder.EncodeSymbol(writer, 17);
            writer.WriteBits((uint)(run - 3), 3);
            run = 0;
          }
          else {
            clEncoder.EncodeSymbol(writer, 0);
            --run;
          }
        }
      }
      else {
        var val = codeLengths[i];
        clEncoder.EncodeSymbol(writer, val);
        ++i;

        // Count repeats of the same value
        var rep = 0;
        while (i < numSymbols && codeLengths[i] == val && rep < 6) {
          ++rep;
          ++i;
        }

        while (rep >= 3) {
          clEncoder.EncodeSymbol(writer, 16);
          writer.WriteBits((uint)(Math.Min(rep, 6) - 3), 2);
          rep -= Math.Min(rep, 6);
        }

        while (rep > 0) {
          clEncoder.EncodeSymbol(writer, val);
          --rep;
        }
      }
    }
  }

  private static int[] BuildCodeLengths(int[] freq, int numSymbols, int maxBits) {
    var lengths = new int[numSymbols];
    var symbols = new List<(int sym, int freq)>();

    for (var i = 0; i < numSymbols; ++i)
      if (freq[i] > 0)
        symbols.Add((i, freq[i]));

    if (symbols.Count == 0) return lengths;
    if (symbols.Count == 1) {
      lengths[symbols[0].sym] = 1;
      return lengths;
    }

    // Build Huffman tree via priority queue
    var pq = new PriorityQueue<int, long>();
    var nodes = new List<(long freq, int sym, int left, int right)>();

    for (var i = 0; i < symbols.Count; ++i) {
      nodes.Add((symbols[i].freq, symbols[i].sym, -1, -1));
      pq.Enqueue(i, symbols[i].freq);
    }

    while (pq.Count > 1) {
      pq.TryDequeue(out var a, out var fa);
      pq.TryDequeue(out var b, out var fb);
      var newIdx = nodes.Count;
      nodes.Add((fa + fb, -1, a, b));
      pq.Enqueue(newIdx, fa + fb);
    }

    pq.TryDequeue(out var root, out _);

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

    // Clamp to maxBits and fix Kraft inequality
    ClampAndFix(lengths, numSymbols, maxBits);

    return lengths;
  }

  private static void ClampAndFix(int[] lengths, int numSymbols, int maxBits) {
    for (var i = 0; i < numSymbols; ++i)
      if (lengths[i] > maxBits)
        lengths[i] = maxBits;

    var kraftMax = 1L << maxBits;
    long kraftSum = 0;
    for (var i = 0; i < numSymbols; ++i)
      if (lengths[i] > 0)
        kraftSum += kraftMax >> lengths[i];

    while (kraftSum > kraftMax) {
      for (var i = numSymbols - 1; i >= 0; --i) {
        if (lengths[i] > 0 && lengths[i] < maxBits) {
          kraftSum -= kraftMax >> lengths[i];
          ++lengths[i];
          kraftSum += kraftMax >> lengths[i];
          if (kraftSum <= kraftMax) break;
        }
      }
    }
  }

  private static uint[] BuildCanonicalCodes(int[] lengths, int numSymbols) {
    var maxLen = 0;
    foreach (var l in lengths)
      if (l > maxLen) maxLen = l;
    if (maxLen == 0) return new uint[numSymbols];

    var blCount = new int[maxLen + 1];
    foreach (var l in lengths)
      if (l > 0) ++blCount[l];

    var nextCode = new uint[maxLen + 1];
    uint code = 0;
    for (var b = 1; b <= maxLen; ++b) {
      code = (code + (uint)blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    // Build canonical codes (MSB-first — no bit reversal needed)
    var codes = new uint[numSymbols];
    for (var i = 0; i < numSymbols; ++i) {
      if (lengths[i] <= 0) continue;
      codes[i] = nextCode[lengths[i]]++;
    }

    return codes;
  }
}
