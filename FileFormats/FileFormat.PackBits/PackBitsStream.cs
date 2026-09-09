namespace FileFormat.PackBits;

internal enum PackBitsEncodingStrategy {
  Greedy,
  Optimal,
}

/// <summary>
/// Provides PackBits compression and decompression (Apple MacPaint standard)
/// with a framed container header.
/// </summary>
public static class PackBitsStream {

  /// <summary>Magic bytes: PKBT (0x504B4254).</summary>
  static readonly byte[] Magic = "PKBT"u8.ToArray();

  /// <summary>
  /// Compresses <paramref name="input"/> to <paramref name="output"/> using PackBits encoding.
  /// </summary>
  /// <param name="input">The uncompressed source stream.</param>
  /// <param name="output">The destination stream for compressed data.</param>
  public static void Compress(Stream input, Stream output) => Compress(input, output, PackBitsEncodingStrategy.Greedy);

  internal static void Compress(Stream input, Stream output, PackBitsEncodingStrategy strategy) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read all input bytes so we know the uncompressed size.
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    // Write header: magic + LE uncompressed size.
    output.Write(Magic);
    output.Write(BitConverter.GetBytes((uint)data.Length));

    if (strategy == PackBitsEncodingStrategy.Optimal)
      EncodeOptimal(data, output);
    else
      EncodeGreedy(data, output);
  }

  private static void EncodeGreedy(byte[] data, Stream output) {
    var i = 0;
    var literal = new List<byte>(128);

    while (i < data.Length) {
      // Count the run of identical bytes starting at i.
      var runByte = data[i];
      var runLen = 1;
      while (i + runLen < data.Length && data[i + runLen] == runByte && runLen < 128)
        runLen++;

      if (runLen >= 3) {
        // Flush any pending literal run first.
        FlushLiteral(output, literal);
        // Emit repeat packet: header = -(runLen - 1), then the byte.
        output.WriteByte((byte)(unchecked((sbyte)(-(runLen - 1)))));
        output.WriteByte(runByte);
        i += runLen;
      } else if (runLen == 2 && literal.Count == 0) {
        // A run of 2 at the start of a potential literal — emit as repeat.
        output.WriteByte(unchecked((byte)(sbyte)(-1)));
        output.WriteByte(runByte);
        i += 2;
      } else {
        // Accumulate into literal buffer.
        literal.Add(data[i]);
        i++;
        if (literal.Count == 128)
          FlushLiteral(output, literal);
      }
    }

    // Flush remaining literals.
    FlushLiteral(output, literal);
  }

  /// <summary>
  /// Finds an exact minimum-size packetization. For a suffix beginning at i, a
  /// literal packet ending at j costs 1 + (j-i) + cost[j], while a repeat packet
  /// costs 2 + cost[j]. Both candidate ranges are at most 128 bytes wide.
  /// Monotonic deques maintain the minima of those sliding ranges, reducing the
  /// dynamic program from O(n * 128) to O(n) time while storing one decision byte
  /// per input byte instead of a full cost table.
  /// </summary>
  private static void EncodeOptimal(byte[] data, Stream output) {
    if (data.Length == 0)
      return;

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

    for (var i = 0; i < data.Length;) {
      var decision = decisions[i];
      if ((decision & 0x80) == 0) {
        var length = (decision & 0x7F) + 1;
        output.WriteByte((byte)(length - 1));
        output.Write(data.AsSpan(i, length));
        i += length;
      } else {
        var length = (decision & 0x7F) + 2;
        output.WriteByte(unchecked((byte)(sbyte)(1 - length)));
        output.WriteByte(data[i]);
        i += length;
      }
    }
  }

  /// <summary>
  /// Decompresses <paramref name="input"/> to <paramref name="output"/> using PackBits decoding.
  /// </summary>
  /// <param name="input">The compressed source stream.</param>
  /// <param name="output">The destination stream for decompressed data.</param>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Validate magic.
    Span<byte> header = stackalloc byte[8];
    if (input.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
      throw new InvalidDataException("Stream too short for PackBits header.");

    if (header[0] != Magic[0] || header[1] != Magic[1] ||
        header[2] != Magic[2] || header[3] != Magic[3])
      throw new InvalidDataException("Invalid PackBits magic.");

    var uncompressedSize = BitConverter.ToUInt32(header[4..]);
    uint written = 0;

    while (written < uncompressedSize) {
      var nRaw = input.ReadByte();
      if (nRaw < 0)
        throw new InvalidDataException("Unexpected end of PackBits stream.");

      var n = (sbyte)(byte)nRaw;

      if (n == -128) {
        // No-op, skip.
        continue;
      }

      if (n >= 0) {
        // Literal run: copy n+1 bytes.
        var count = n + 1;
        for (var j = 0; j < count; j++) {
          var b = input.ReadByte();
          if (b < 0)
            throw new InvalidDataException("Unexpected end of PackBits stream during literal run.");
          output.WriteByte((byte)b);
          written++;
        }
      } else {
        // Repeat run: read one byte, repeat 1-n times.
        var count = 1 - n;
        var b = input.ReadByte();
        if (b < 0)
          throw new InvalidDataException("Unexpected end of PackBits stream during repeat run.");
        for (var j = 0; j < count; j++) {
          output.WriteByte((byte)b);
          written++;
        }
      }
    }
  }

  static void FlushLiteral(Stream output, List<byte> literal) {
    if (literal.Count == 0)
      return;

    // Header byte = count - 1.
    output.WriteByte((byte)(literal.Count - 1));
    foreach (var b in literal)
      output.WriteByte(b);

    literal.Clear();
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
