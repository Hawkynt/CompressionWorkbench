namespace Compression.Core.Transforms;

/// <summary>
/// Apple PackBits run-length encoding, as specified in Apple Technical Note TN1023
/// and adopted by the TIFF 6.0 specification (section 2, "PackBits" compression).
/// The compressed stream is a sequence of control bytes, each interpreted as a
/// signed 8-bit count:
/// <list type="bullet">
/// <item>0..127: copy the following (count + 1) bytes literally.</item>
/// <item>-1..-127: repeat the single following byte (1 - count) times.</item>
/// <item>-128: no-op, reserved (skipped).</item>
/// </list>
/// The format is self-terminating — no length header is required since the decoder
/// simply consumes control bytes until the input is exhausted.
/// </summary>
public static class PackBitsEncoding {
  private const int MaxRunLength = 128;
  private const int MaxLiteralLength = 128;
  private const int MinRunLength = 3;

  /// <summary>
  /// Encodes data using Apple PackBits run-length encoding.
  /// </summary>
  /// <param name="data">The input data to encode.</param>
  /// <returns>The PackBits-encoded data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var output = new List<byte>();
    var i = 0;

    while (i < data.Length) {
      var runLength = RunLengthAt(data, i);

      if (runLength >= MinRunLength) {
        output.Add((byte)(257 - runLength));
        output.Add(data[i]);
        i += runLength;
        continue;
      }

      // Accumulate a literal sequence, stopping just before any run of at least
      // MinRunLength identical bytes so it can be encoded as a run instead.
      var literalStart = i;
      var literalLength = 0;
      while (i < data.Length && literalLength < MaxLiteralLength) {
        if (RunLengthAt(data, i) >= MinRunLength)
          break;
        literalLength++;
        i++;
      }

      output.Add((byte)(literalLength - 1));
      for (var j = 0; j < literalLength; j++)
        output.Add(data[literalStart + j]);
    }

    return [.. output];
  }

  /// <summary>
  /// Decodes PackBits-encoded data back to the original bytes.
  /// </summary>
  /// <param name="data">The PackBits-encoded data.</param>
  /// <returns>The decoded data.</returns>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var output = new List<byte>();
    var i = 0;

    while (i < data.Length) {
      sbyte control = (sbyte)data[i++];

      switch (control) {
        case >= 0:
          var literalLength = control + 1;
          for (var j = 0; j < literalLength && i < data.Length; j++)
            output.Add(data[i++]);
          break;

        case > -128:
          if (i >= data.Length)
            break;
          var runLength = 1 - control;
          var value = data[i++];
          for (var j = 0; j < runLength; j++)
            output.Add(value);
          break;

        default:
          // -128: reserved no-op, consumes only the control byte.
          break;
      }
    }

    return [.. output];
  }

  /// <summary>Length of the run of identical bytes starting at <paramref name="pos"/>, capped at <see cref="MaxRunLength"/>.</summary>
  private static int RunLengthAt(ReadOnlySpan<byte> data, int pos) {
    var run = 1;
    while (pos + run < data.Length && data[pos + run] == data[pos] && run < MaxRunLength)
      run++;
    return run;
  }

  /// <summary>
  /// Finds an exact minimum-size packetization. For a suffix beginning at i, a
  /// literal packet ending at j costs 1 + (j-i) + cost[j], while a repeat packet
  /// costs 2 + cost[j]. Both candidate ranges are at most 128 bytes wide.
  /// Monotonic deques maintain the minima of those sliding ranges, reducing the
  /// dynamic program from O(n * 128) to O(n) time while storing one decision byte
  /// per input byte instead of a full cost table.
  /// </summary>
  /// <param name="data">The input data to encode.</param>
  /// <returns>The smallest PackBits encoding of <paramref name="data"/>.</returns>
  public static byte[] EncodeOptimal(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var decisions = new byte[data.Length];
    var literals = new MinCostWindow();
    var repeats = new MinCostWindow();

    // cost[i+1] and cost[i+2] while walking the input backwards.
    long nextCost = 0;
    long nextNextCost = 0;

    for (var i = data.Length - 1; i >= 0; --i) {
      var maxEnd = Math.Min(data.Length, i + 128);

      // Literal packets may end anywhere in [i+1, i+128]. Minimise
      // cost[j] + j, because the remaining terms (1-i) are constant for i.
      literals.Add(i + 1, nextCost + i + 1L);
      literals.RemoveIndicesGreaterThan(maxEnd);
      var literal = literals.Minimum;
      var literalLength = literal.Index - i;
      var bestCost = 1L - i + literal.Cost;
      var decision = (byte)(literalLength - 1);

      if (i + 1 < data.Length && data[i] == data[i + 1]) {
        // Within one equal-byte run, legal repeat packets may end anywhere in
        // [i+2, i+128]. A repeat always costs two encoded bytes regardless of length.
        repeats.Add(i + 2, nextNextCost);
        repeats.RemoveIndicesGreaterThan(maxEnd);
        var repeat = repeats.Minimum;
        var repeatCost = 2L + repeat.Cost;

        if (repeatCost < bestCost) {
          bestCost = repeatCost;
          decision = (byte)(0x80 | (repeat.Index - i - 2));
        }
      } else {
        repeats.Clear();
      }

      decisions[i] = decision;
      nextNextCost = nextCost;
      nextCost = bestCost;
    }

    var output = new List<byte>(data.Length + data.Length / 128 + 1);
    for (var i = 0; i < data.Length;) {
      var decision = decisions[i];
      if ((decision & 0x80) == 0) {
        var length = (decision & 0x7F) + 1;
        output.Add((byte)(length - 1));
        output.AddRange(data.Slice(i, length));
        i += length;
      } else {
        var length = (decision & 0x7F) + 2;
        output.Add(unchecked((byte)(sbyte)(1 - length)));
        output.Add(data[i]);
        i += length;
      }
    }

    return [.. output];
  }

  private readonly record struct CostCandidate(int Index, long Cost);

  /// <summary>
  /// Monotonic queue for a backwards-moving index window. Indices are inserted in
  /// descending order, so expired high indices leave from the front while dominated
  /// costs leave from the back. Equal costs are retained so the older (longer packet)
  /// candidate wins deterministically.
  /// </summary>
  private sealed class MinCostWindow {
    private readonly CostCandidate[] _items = new CostCandidate[129];
    private int _head;
    private int _count;

    public CostCandidate Minimum => this._items[this._head];

    public void Add(int index, long cost) {
      while (this._count > 0) {
        var tail = (this._head + this._count - 1) % this._items.Length;
        if (this._items[tail].Cost <= cost)
          break;

        --this._count;
      }

      var target = (this._head + this._count) % this._items.Length;
      this._items[target] = new(index, cost);
      ++this._count;
    }

    public void RemoveIndicesGreaterThan(int maxIndex) {
      while (this._count > 0 && this._items[this._head].Index > maxIndex) {
        this._head = (this._head + 1) % this._items.Length;
        --this._count;
      }
    }

    public void Clear() {
      this._head = 0;
      this._count = 0;
    }
  }
}
