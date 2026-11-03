namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Encodes data with ZIP Implode (method 6), in the stream layout PKZIP 1.x reads: optional literal
/// tree, length tree and distance tree as run-length coded code lengths, then flag bit, literal or
/// (low distance bits, distance code, length code, extra length byte) per item.
/// </summary>
/// <remarks>
/// The layout and the inverted canonical codes follow hwimplode in hwzip 2.4's implode.c (Hans
/// Wennborg, public domain), see THIRD-PARTY-NOTICE.hwzip.txt. Implode needs a full tree over every
/// symbol, so unused symbols get a frequency of one and code lengths are limited to 16 bits by the
/// JPEG Annex K.3 adjustment, which keeps the tree full.
/// </remarks>
public static class ImplodeEncoder {
  private const int MaxCodeLength = 16;
  private const int HashBits = 14;
  private const int MaxChain = 128;

  /// <summary>
  /// Compresses data using the ZIP Implode algorithm.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="useLiteralTree">Code literals with a Shannon-Fano tree (general-purpose bit 2).</param>
  /// <param name="use8kDictionary">Use the 8 KB window (general-purpose bit 1) instead of 4 KB.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data, bool useLiteralTree = true, bool use8kDictionary = true) {
    var minLength = useLiteralTree ? 3 : 2;
    var maxLength = minLength + 63 + 255;
    var maxDistance = use8kDictionary ? 8192 : 4096;
    var lowDistanceBits = use8kDictionary ? 7 : 6;

    var tokens = Tokenize(data, minLength, maxLength, maxDistance);
    var literalFreq = new long[256];
    var lengthFreq = new long[64];
    var distanceFreq = new long[64];
    foreach (var (distance, value) in tokens)
      if (distance == 0)
        ++literalFreq[value];
      else {
        ++lengthFreq[Math.Min(value - minLength, 63)];
        ++distanceFreq[(distance - 1) >> lowDistanceBits];
      }

    var writer = new ZipLegacyBitWriter(data.Length / 2 + 64);
    var literalCode = useLiteralTree ? WriteTree(writer, literalFreq) : null;
    var lengthCode = WriteTree(writer, lengthFreq);
    var distanceCode = WriteTree(writer, distanceFreq);

    foreach (var (distance, value) in tokens) {
      if (distance == 0) {
        writer.Write(1, 1);
        if (literalCode != null)
          literalCode.Write(writer, value);
        else
          writer.Write((ulong)value, 8);
        continue;
      }

      writer.Write(0, 1);
      writer.Write((ulong)((distance - 1) & ((1 << lowDistanceBits) - 1)), lowDistanceBits);
      distanceCode.Write(writer, (distance - 1) >> lowDistanceBits);
      var lengthSymbol = Math.Min(value - minLength, 63);
      lengthCode.Write(writer, lengthSymbol);
      if (lengthSymbol == 63)
        writer.Write((ulong)(value - minLength - 63), 8);
    }

    return writer.ToArray();
  }

  /// <summary>Greedy hash-chain LZ77: (0, literal) or (distance, length) items.</summary>
  private static List<(int Distance, int Value)> Tokenize(ReadOnlySpan<byte> data, int minLength, int maxLength, int maxDistance) {
    var tokens = new List<(int, int)>(data.Length / 2 + 1);
    var head = new int[1 << HashBits];
    Array.Fill(head, -1);
    var prev = new int[data.Length];
    var pos = 0;
    while (pos < data.Length) {
      var bestLength = 0;
      var bestDistance = 0;
      if (pos + 3 <= data.Length) {
        var room = Math.Min(maxLength, data.Length - pos);
        var steps = 0;
        for (var candidate = head[Hash(data, pos)]; candidate >= 0 && pos - candidate <= maxDistance && steps < MaxChain; candidate = prev[candidate], ++steps) {
          var length = 0;
          while (length < room && data[candidate + length] == data[pos + length])
            ++length;

          if (length > bestLength) {
            bestLength = length;
            bestDistance = pos - candidate;
            if (length == room)
              break;
          }
        }
      }

      var advance = bestLength >= minLength ? bestLength : 1;
      tokens.Add(bestLength >= minLength ? (bestDistance, bestLength) : (0, data[pos]));
      for (var end = pos + advance; pos < end; ++pos)
        if (pos + 3 <= data.Length) {
          var h = Hash(data, pos);
          prev[pos] = head[h];
          head[h] = pos;
        }
    }

    return tokens;
  }

  private static int Hash(ReadOnlySpan<byte> data, int pos)
    => (int)(((uint)(data[pos] << 16 | data[pos + 1] << 8 | data[pos + 2]) * 2654435761u) >> (32 - HashBits));

  private static TreeCode WriteTree(ZipLegacyBitWriter writer, long[] frequencies) {
    var lengths = BuildLengths(frequencies);

    var runs = new List<(int Length, int Count)>();
    foreach (var length in lengths)
      if (runs.Count > 0 && runs[^1].Length == length && runs[^1].Count < 16)
        runs[^1] = (length, runs[^1].Count + 1);
      else
        runs.Add((length, 1));

    writer.Write((ulong)(runs.Count - 1), 8);
    foreach (var (length, count) in runs) {
      writer.Write((ulong)(length - 1), 4);
      writer.Write((ulong)(count - 1), 4);
    }

    return new(lengths);
  }

  /// <summary>Code lengths of a full tree over every symbol, at most 16 bits each.</summary>
  internal static int[] BuildLengths(long[] frequencies) {
    var n = frequencies.Length;
    var weights = new long[n];
    for (var i = 0; i < n; ++i)
      weights[i] = frequencies[i] == 0 ? 1 : frequencies[i] * 4096; // keep unused symbols far below used ones

    // Plain Huffman over all symbols.
    var lengths = new int[n];
    var queue = new PriorityQueue<int, (long Weight, int Order)>();
    var parent = new int[2 * n];
    for (var i = 0; i < n; ++i)
      queue.Enqueue(i, (weights[i], i));

    var next = n;
    var nodeWeight = new long[2 * n];
    Array.Copy(weights, nodeWeight, n);
    while (queue.Count > 1) {
      queue.TryDequeue(out var a, out var pa);
      queue.TryDequeue(out var b, out var pb);
      nodeWeight[next] = pa.Weight + pb.Weight;
      parent[a] = next;
      parent[b] = next;
      queue.Enqueue(next, (nodeWeight[next], next));
      ++next;
    }

    var root = next - 1;
    var depth = new int[2 * n];
    for (var node = root - 1; node >= 0; --node)
      depth[node] = depth[parent[node]] + 1;

    var count = new int[Math.Max(MaxCodeLength, n) + 2];
    for (var i = 0; i < n; ++i)
      ++count[depth[i]];

    // JPEG Annex K.3: move symbols off lengths above 16 without breaking the full tree.
    for (var length = count.Length - 1; length > MaxCodeLength; --length)
      while (count[length] > 0) {
        var j = length - 2;
        while (count[j] == 0)
          --j;

        count[length] -= 2;
        ++count[length - 1];
        count[j + 1] += 2;
        --count[j];
      }

    // Hand the lengths back out, shortest to the most frequent.
    var order = Enumerable.Range(0, n).OrderByDescending(i => weights[i]).ThenBy(i => i).ToArray();
    var at = 0;
    for (var length = 1; length <= MaxCodeLength; ++length)
      for (var k = 0; k < count[length]; ++k)
        lengths[order[at++]] = length;

    return lengths;
  }

  /// <summary>The inverted canonical code for a set of lengths, written first bit first.</summary>
  private sealed class TreeCode {
    private readonly int[] _lengths;
    private readonly ulong[] _written;

    public TreeCode(int[] lengths) {
      this._lengths = lengths;
      this._written = new ulong[lengths.Length];
      var count = new int[MaxCodeLength + 1];
      foreach (var length in lengths)
        ++count[length];

      var nextCode = new int[MaxCodeLength + 1];
      var code = 0;
      for (var length = 1; length <= MaxCodeLength; ++length) {
        code = (code + count[length - 1]) << 1;
        nextCode[length] = code;
      }

      for (var symbol = 0; symbol < lengths.Length; ++symbol) {
        var length = lengths[symbol];
        var canonical = nextCode[length]++;
        // The stream takes the code's top bit first, inverted; an LSB-first writer needs it reversed.
        ulong reversed = 0;
        for (var b = 0; b < length; ++b)
          reversed |= (ulong)(((canonical >> (length - 1 - b)) & 1) ^ 1) << b;

        this._written[symbol] = reversed;
      }
    }

    public void Write(ZipLegacyBitWriter writer, int symbol) => writer.Write(this._written[symbol], this._lengths[symbol]);
  }
}
