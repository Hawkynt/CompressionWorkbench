using System.Buffers.Binary;

namespace FileFormat.Zstd;

/// <summary>
/// Decodes Zstandard Huffman-compressed literals exactly as specified by RFC 8878,
/// section 4.2.1 (Huffman_Tree_Description) and the Huffman bitstream layout.
/// This is the conformant counterpart used to read literal sections produced by
/// the reference <c>zstd</c> implementation: weights are decoded either directly
/// (4 bits each) or from an FSE-compressed stream, the implicit last weight is
/// completed to the next power of two, and the data bitstream(s) are read backward
/// from a final sentinel bit, MSB first.
/// </summary>
internal static class ZstdHuffmanLiterals {
  /// <summary>Maximum number of bits a Huffman code may use (table log).</summary>
  private const int MaxTableLog = 11;

  /// <summary>Maximum accuracy log for the FSE table that encodes weights.</summary>
  private const int MaxWeightAccuracyLog = 6;

  /// <summary>
  /// Decodes Huffman literals carrying their own tree description, returning the decoded
  /// bytes and the table built from that tree (for subsequent Treeless reuse).
  /// </summary>
  /// <param name="huffmanSection">
  /// The literal payload: the Huffman tree description followed by the (1 or 4) data streams.
  /// </param>
  /// <param name="regenSize">The number of literal bytes to regenerate.</param>
  /// <param name="fourStreams">Whether the data is split into four streams with a jump table.</param>
  /// <returns>The decoded literal bytes and the reusable Huffman table.</returns>
  public static (byte[] Literals, HuffTable Table) Decode(ReadOnlySpan<byte> huffmanSection, int regenSize, bool fourStreams) {
    var weights = ReadWeights(huffmanSection, out var treeBytes, out var maxBits);
    var (numBits, maxSymbol) = WeightsToNumBits(weights, maxBits);
    var decodeTable = BuildDecodeTable(numBits, maxSymbol, maxBits);
    var table = new HuffTable(decodeTable, maxBits);

    var streams = huffmanSection[treeBytes..];
    var literals = DecodeStreams(streams, table, regenSize, fourStreams);
    return (literals, table);
  }

  /// <summary>
  /// Decodes Treeless Huffman literals using a previously parsed Huffman table.
  /// </summary>
  /// <param name="streams">The data streams (no tree description).</param>
  /// <param name="regenSize">The number of literal bytes to regenerate.</param>
  /// <param name="fourStreams">Whether the data is split into four streams with a jump table.</param>
  /// <param name="table">The Huffman table parsed from a previous block.</param>
  /// <returns>The decoded literal bytes.</returns>
  public static byte[] DecodeTreeless(ReadOnlySpan<byte> streams, int regenSize, bool fourStreams, HuffTable table) =>
    DecodeStreams(streams, table, regenSize, fourStreams);

  private static byte[] DecodeStreams(ReadOnlySpan<byte> streams, HuffTable huffTable, int regenSize, bool fourStreams) {
    var table = huffTable.Table;
    var maxBits = huffTable.MaxBits;
    var output = new byte[regenSize];

    if (!fourStreams) {
      DecodeStream(streams, table, maxBits, output.AsSpan(0, regenSize));
      return output;
    }

    if (streams.Length < 6)
      throw new InvalidDataException("Truncated Huffman jump table.");

    var size1 = BinaryPrimitives.ReadUInt16LittleEndian(streams);
    var size2 = BinaryPrimitives.ReadUInt16LittleEndian(streams[2..]);
    var size3 = BinaryPrimitives.ReadUInt16LittleEndian(streams[4..]);
    var streamData = streams[6..];
    var size4 = streamData.Length - size1 - size2 - size3;
    if (size4 < 0)
      throw new InvalidDataException("Invalid Huffman stream sizes.");

    // The regenerated output is split into 4 equal segments; the last carries the remainder.
    var segment = (regenSize + 3) / 4;
    var seg4 = regenSize - segment * 3;
    if (seg4 < 0)
      throw new InvalidDataException("Invalid Huffman regenerated size for four streams.");

    var off = 0;
    DecodeStream(streamData.Slice(off, size1), table, maxBits, output.AsSpan(0, segment)); off += size1;
    DecodeStream(streamData.Slice(off, size2), table, maxBits, output.AsSpan(segment, segment)); off += size2;
    DecodeStream(streamData.Slice(off, size3), table, maxBits, output.AsSpan(segment * 2, segment)); off += size3;
    DecodeStream(streamData.Slice(off, size4), table, maxBits, output.AsSpan(segment * 3, seg4));

    return output;
  }

  /// <summary>
  /// Reads the Huffman tree description and returns one weight per symbol.
  /// </summary>
  private static int[] ReadWeights(ReadOnlySpan<byte> section, out int bytesRead, out int maxBits) {
    if (section.Length < 1)
      throw new InvalidDataException("Truncated Huffman tree description.");

    int headerByte = section[0];
    int[] weights;
    int numWeights;

    if (headerByte >= 128) {
      // Direct representation: (headerByte - 127) explicit 4-bit weights.
      numWeights = headerByte - 127;
      var packed = (numWeights + 1) / 2;
      if (1 + packed > section.Length)
        throw new InvalidDataException("Truncated direct Huffman weights.");

      weights = new int[numWeights + 1];
      for (var i = 0; i < numWeights; ++i) {
        int b = section[1 + (i >> 1)];
        weights[i] = (i & 1) == 0 ? (b >> 4) : (b & 0x0F);
      }

      bytesRead = 1 + packed;
    } else {
      // FSE-compressed weights: headerByte is the compressed byte length.
      var compressedSize = headerByte;
      if (1 + compressedSize > section.Length)
        throw new InvalidDataException("Truncated FSE Huffman weights.");

      var fse = section.Slice(1, compressedSize);
      weights = DecodeFseWeights(fse);
      numWeights = weights.Length - 1; // one slot reserved for the implicit last weight
      bytesRead = 1 + compressedSize;
    }

    // Complete the implicit final weight so the total reaches the next power of two.
    CompleteWeights(weights, numWeights, out maxBits);
    return weights;
  }

  /// <summary>
  /// Computes the implicit last weight and the Huffman table log (Max_Number_of_Bits).
  /// </summary>
  private static void CompleteWeights(int[] weights, int numWeights, out int maxBits) {
    long total = 0;
    for (var i = 0; i < numWeights; ++i) {
      var w = weights[i];
      if (w > 0)
        total += 1L << (w - 1);
    }

    if (total == 0)
      throw new InvalidDataException("Huffman weights sum to zero.");

    // Max_Number_of_Bits = position of the bit above the current total.
    var bits = 1;
    while ((1L << bits) < total)
      ++bits;
    maxBits = bits;
    if (maxBits > MaxTableLog)
      throw new InvalidDataException($"Huffman table log {maxBits} exceeds {MaxTableLog}.");

    var left = (1L << maxBits) - total;
    if (left <= 0 || (left & (left - 1)) != 0)
      throw new InvalidDataException("Invalid Huffman weight distribution.");

    // left == 2^(lastWeight-1)  =>  lastWeight = log2(left) + 1
    var lastWeight = 1;
    while ((1L << (lastWeight - 1)) < left)
      ++lastWeight;
    weights[numWeights] = lastWeight;
  }

  /// <summary>
  /// Converts weights to per-symbol code bit lengths.
  /// </summary>
  private static (int[] NumBits, int MaxSymbol) WeightsToNumBits(int[] weights, int maxBits) {
    var numBits = new int[weights.Length];
    var maxSymbol = 0;
    for (var s = 0; s < weights.Length; ++s) {
      var w = weights[s];
      if (w <= 0)
        continue;
      numBits[s] = maxBits + 1 - w;
      maxSymbol = s;
    }

    return (numBits, maxSymbol);
  }

  /// <summary>
  /// Builds a flat lookup table mapping <paramref name="maxBits"/>-bit prefixes to (symbol, length).
  /// Codes are assigned in canonical order: longest codes (smallest weight) first, lowest symbol first.
  /// The reference layout fills the table sequentially with each symbol repeated 2^(maxBits-len) times.
  /// </summary>
  private static DecodeTable BuildDecodeTable(int[] numBits, int maxSymbol, int maxBits) {
    var size = 1 << maxBits;
    var symbols = new byte[size];
    var lengths = new byte[size];

    // Rank order: by descending number of bits (i.e. ascending weight), then ascending symbol.
    var pos = 0;
    for (var len = maxBits; len >= 1; --len) {
      var span = 1 << (maxBits - len);
      for (var s = 0; s <= maxSymbol; ++s) {
        if (numBits[s] != len)
          continue;
        for (var i = 0; i < span; ++i) {
          symbols[pos] = (byte)s;
          lengths[pos] = (byte)len;
          ++pos;
        }
      }
    }

    if (pos != size)
      throw new InvalidDataException("Huffman table is not fully populated.");

    return new DecodeTable(symbols, lengths);
  }

  /// <summary>
  /// Decodes a single Huffman stream (backward, MSB-first, sentinel-terminated) into <paramref name="output"/>.
  /// </summary>
  private static void DecodeStream(ReadOnlySpan<byte> stream, DecodeTable table, int maxBits, Span<byte> output) {
    if (output.Length == 0)
      return;
    if (stream.Length == 0)
      throw new InvalidDataException("Empty Huffman stream.");

    var reader = new ReverseBitReader(stream);
    for (var i = 0; i < output.Length; ++i) {
      var prefix = reader.Peek(maxBits);
      var sym = table.Symbols[prefix];
      var len = table.Lengths[prefix];
      output[i] = sym;
      reader.Skip(len);
    }
  }

  /// <summary>
  /// Decodes the FSE-compressed weight stream into raw weights (with one extra trailing slot
  /// reserved for the implicit final weight).
  /// </summary>
  private static int[] DecodeFseWeights(ReadOnlySpan<byte> fse) {
    var br = new ForwardBitReader(fse);
    var accuracyLog = br.ReadBits(4) + 5;
    if (accuracyLog > MaxWeightAccuracyLog)
      throw new InvalidDataException($"Huffman weight FSE accuracy log {accuracyLog} too large.");

    var tableSize = 1 << accuracyLog;
    var counts = new short[256];
    var maxSym = -1;
    var remaining = tableSize + 1;
    var symbol = 0;

    while (remaining > 1 && symbol < 256) {
      var maxValue = remaining; // values 0..remaining, inclusive
      var nbBits = BitWidth(maxValue);
      var threshold = (1 << nbBits) - 1 - maxValue; // low-bit "small value" region

      var value = br.ReadBits(nbBits - 1);
      if (value < threshold) {
        // value fits in nbBits-1 bits
      } else {
        var extra = br.ReadBits(1);
        value += extra << (nbBits - 1);
        if (value >= (1 << (nbBits - 1)))
          value -= threshold;
      }

      var proba = value - 1; // -1 means "less than 1"
      counts[symbol] = (short)proba;
      if (proba != 0)
        maxSym = symbol;
      remaining -= proba < 0 ? 1 : proba;

      if (proba == 0) {
        // Repeat flag(s): runs of 2-bit values, each adds that many zero-probability symbols.
        while (true) {
          var repeat = br.ReadBits(2);
          symbol += repeat;
          if (repeat != 3)
            break;
        }
      }

      ++symbol;
    }

    if (maxSym < 0)
      throw new InvalidDataException("FSE Huffman weight table has no symbols.");

    var table = FseWeightTable.Build(counts, maxSym, accuracyLog);

    // The weight bitstream follows the FSE table description, read backward with two
    // interleaved FSE states (the reference uses two states for the weights stream).
    var bitstreamStart = br.BytesConsumed;
    var weightStream = fse[bitstreamStart..];
    return DecodeWeightBitstream(weightStream, table, accuracyLog);
  }

  /// <summary>
  /// Decodes the backward FSE weight bitstream using two interleaved states.
  /// </summary>
  private static int[] DecodeWeightBitstream(ReadOnlySpan<byte> stream, FseWeightTable table, int accuracyLog) {
    if (stream.Length == 0)
      throw new InvalidDataException("Empty Huffman weight bitstream.");

    var reader = new ReverseBitReader(stream);
    var state1 = reader.ReadBits(accuracyLog);
    var state2 = reader.ReadBits(accuracyLog);

    var weights = new List<int>();
    while (true) {
      weights.Add(table.Symbol[state1]);
      if (reader.Finished) {
        weights.Add(table.Symbol[state2]);
        break;
      }
      state1 = table.NewStateBase[state1] + reader.ReadBits(table.NumBits[state1]);

      weights.Add(table.Symbol[state2]);
      if (reader.Finished) {
        weights.Add(table.Symbol[state1]);
        break;
      }
      state2 = table.NewStateBase[state2] + reader.ReadBits(table.NumBits[state2]);
    }

    // Reserve a trailing slot for the implicit last weight.
    var result = new int[weights.Count + 1];
    for (var i = 0; i < weights.Count; ++i)
      result[i] = weights[i];
    return result;
  }

  private static int BitWidth(int value) {
    var bits = 1;
    while ((1 << bits) <= value)
      ++bits;
    return bits;
  }

  internal readonly struct DecodeTable(byte[] symbols, byte[] lengths) {
    public byte[] Symbols { get; } = symbols;
    public byte[] Lengths { get; } = lengths;
  }

  /// <summary>A parsed Huffman table reusable across Treeless literal blocks.</summary>
  internal readonly struct HuffTable(DecodeTable table, int maxBits) {
    internal DecodeTable Table { get; } = table;
    internal int MaxBits { get; } = maxBits;
  }

  /// <summary>
  /// FSE table for Huffman weights, built with the same spreading rules as the sequence tables.
  /// </summary>
  private sealed class FseWeightTable {
    public int[] NumBits = [];
    public byte[] Symbol = [];
    public int[] NewStateBase = [];

    public static FseWeightTable Build(short[] normalizedCounts, int maxSymbol, int tableLog) {
      var tableSize = 1 << tableLog;
      var t = new FseWeightTable {
        NumBits = new int[tableSize],
        Symbol = new byte[tableSize],
        NewStateBase = new int[tableSize],
      };

      var highThreshold = tableSize - 1;
      var effective = new int[maxSymbol + 1];
      for (var s = 0; s <= maxSymbol; ++s)
        if (normalizedCounts[s] < 0) {
          t.Symbol[highThreshold--] = (byte)s;
          effective[s] = 1;
        } else
          effective[s] = normalizedCounts[s];

      var step = (tableSize >> 1) + (tableSize >> 3) + 3;
      var mask = tableSize - 1;
      var pos = 0;
      for (var s = 0; s <= maxSymbol; ++s) {
        int count = normalizedCounts[s];
        if (count <= 0)
          continue;
        for (var i = 0; i < count; ++i) {
          t.Symbol[pos] = (byte)s;
          do { pos = (pos + step) & mask; } while (pos > highThreshold);
        }
      }

      var next = new int[maxSymbol + 1];
      for (var s = 0; s <= maxSymbol; ++s)
        next[s] = effective[s];

      for (var state = 0; state < tableSize; ++state) {
        var sym = t.Symbol[state];
        var nextState = next[sym]++;
        var nb = tableLog - System.Numerics.BitOperations.Log2((uint)nextState);
        t.NumBits[state] = nb;
        t.NewStateBase[state] = (nextState << nb) - tableSize;
      }

      return t;
    }
  }

  /// <summary>
  /// Reads bits MSB-first from the front of a byte span (used for the FSE table description).
  /// </summary>
  private ref struct ForwardBitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bitPos = 0;

    public int BytesConsumed => (this._bitPos + 7) >> 3;

    public int ReadBits(int n) {
      var value = 0;
      for (var i = 0; i < n; ++i) {
        var byteIdx = this._bitPos >> 3;
        var bitIdx = this._bitPos & 7;
        var bit = byteIdx < this._data.Length ? (this._data[byteIdx] >> bitIdx) & 1 : 0;
        value |= bit << i;
        ++this._bitPos;
      }

      return value;
    }
  }

  /// <summary>
  /// Reads a Zstandard backward bitstream: the highest set bit of the last byte is the
  /// sentinel, data bits are below it, consumed from the most significant downward.
  /// </summary>
  private ref struct ReverseBitReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos;

    public ReverseBitReader(ReadOnlySpan<byte> data) {
      this._data = data;
      var last = data.Length - 1;
      while (last > 0 && data[last] == 0)
        --last;
      if (data[last] == 0)
        throw new InvalidDataException("No sentinel bit in Huffman stream.");
      var high = System.Numerics.BitOperations.Log2((uint)data[last]);
      this._bitPos = last * 8 + high - 1; // position just below the sentinel
    }

    public bool Finished => this._bitPos < 0;

    public int Peek(int n) {
      var value = 0;
      var p = this._bitPos;
      for (var i = 0; i < n; ++i) {
        var bit = p >= 0 ? (this._data[p >> 3] >> (p & 7)) & 1 : 0;
        value = (value << 1) | bit;
        --p;
      }

      return value;
    }

    public int ReadBits(int n) {
      var value = Peek(n);
      this._bitPos -= n;
      return value;
    }

    public void Skip(int n) => this._bitPos -= n;
  }
}
