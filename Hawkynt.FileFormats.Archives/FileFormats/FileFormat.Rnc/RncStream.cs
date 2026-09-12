using System.Numerics;

namespace FileFormat.Rnc;

/// <summary>Parser strategy used by the RNC Method 1 encoder.</summary>
public enum RncParseStrategy {
  /// <summary>Emit the longest match found at the current position immediately.</summary>
  Greedy,
  /// <summary>Prefer a literal when the next position has a longer match, matching ProPack's one-byte look-ahead.</summary>
  Lazy,
}

/// <summary>Encoder controls for RNC ProPack Method 1.</summary>
public sealed record RncCompressionOptions {
  /// <summary>Sliding dictionary size in bytes. Method 1 permits up to 32768 bytes.</summary>
  public int DictionarySize { get; init; } = 0x8000;
  /// <summary>Maximum uncompressed bytes represented by one Huffman-table block.</summary>
  public int BlockSize { get; init; } = 0x3000;
  /// <summary>Maximum hash-chain candidates examined at each input position.</summary>
  public int SearchDepth { get; init; } = 256;
  /// <summary>LZ parsing strategy.</summary>
  public RncParseStrategy ParseStrategy { get; init; } = RncParseStrategy.Lazy;

  internal void Validate() {
    if (this.DictionarySize is < 0x400 or > 0x8000)
      throw new ArgumentOutOfRangeException(nameof(this.DictionarySize), "RNC Method 1 dictionary size must be between 1024 and 32768 bytes.");
    if (this.BlockSize is < 1 or > 0x7FFF)
      throw new ArgumentOutOfRangeException(nameof(this.BlockSize), "RNC Method 1 block size must be between 1 and 32767 bytes.");
    if (this.SearchDepth < 1)
      throw new ArgumentOutOfRangeException(nameof(this.SearchDepth), "Search depth must be positive.");
  }
}

/// <summary>
/// Compressor and decompressor for Rob Northen Computing's RNC ProPack stream format.
/// Method 1 is the Huffman-coded LZ77 variant used by many Amiga, DOS and console games.
/// </summary>
public static class RncStream {
  private const int HeaderSize = 18;
  private const int HuffmanSymbols = 16;
  private const int MaxMatchLength = 0x1000;

  /// <summary>Decompresses an RNC stream.</summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    output.Write(DecompressCore(ReadAllBytes(input)));
  }

  /// <summary>Compresses using the ProPack-compatible Method 1 defaults.</summary>
  public static void Compress(Stream input, Stream output)
    => Compress(input, output, new RncCompressionOptions());

  /// <summary>Compresses using the supplied Method 1 encoder settings.</summary>
  public static void Compress(Stream input, Stream output, RncCompressionOptions options) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(options);
    options.Validate();
    output.Write(CompressCore(ReadAllBytes(input), options));
  }

  /// <summary>Computes the RNC CRC-16 (CRC-16/ARC, polynomial 0xA001, initial value 0).</summary>
  public static ushort Crc16(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (var value in data) {
      crc ^= value;
      for (var bit = 0; bit < 8; ++bit)
        crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
    }
    return crc;
  }

  private static byte[] DecompressCore(ReadOnlySpan<byte> data) {
    if (data.Length < HeaderSize)
      throw new InvalidDataException("Input is shorter than the RNC header.");
    if (data[0] != 'R' || data[1] != 'N' || data[2] != 'C')
      throw new InvalidDataException("Invalid RNC magic bytes.");

    var method = data[3];
    if (method == 0)
      return DecompressStored(data);
    if (method != 1)
      throw new NotSupportedException($"RNC method {method} is not supported. Only Method 1 is implemented.");

    var uncompressedSize = ReadBE32(data, 4);
    var compressedSize = ReadBE32(data, 8);
    if (uncompressedSize > int.MaxValue || compressedSize > int.MaxValue)
      throw new InvalidDataException("RNC stream is too large for the managed in-memory decoder.");
    if (compressedSize > (uint)(data.Length - HeaderSize))
      throw new InvalidDataException("RNC payload is truncated.");

    var expectedUncompressedCrc = ReadBE16(data, 12);
    var expectedCompressedCrc = ReadBE16(data, 14);
    var compressed = data.Slice(HeaderSize, (int)compressedSize);
    var actualCompressedCrc = Crc16(compressed);
    if (actualCompressedCrc != expectedCompressedCrc)
      throw new InvalidDataException($"Compressed CRC mismatch: expected 0x{expectedCompressedCrc:X4}, got 0x{actualCompressedCrc:X4}.");

    var result = new byte[(int)uncompressedSize];
    if (result.Length == 0) {
      if (expectedUncompressedCrc != 0)
        throw new InvalidDataException("Uncompressed CRC mismatch for empty RNC payload.");
      return result;
    }

    var reader = new Method1BitReader(compressed);
    _ = reader.ReadBits(1); // lock flag
    _ = reader.ReadBits(1); // encryption-key-present flag; encrypted streams are not supported by this descriptor

    var outputPosition = 0;
    while (outputPosition < result.Length) {
      var rawTable = ReadHuffmanTable(ref reader);
      var distanceTable = ReadHuffmanTable(ref reader);
      var lengthTable = ReadHuffmanTable(ref reader);
      var tupleCount = reader.ReadBits(16);
      if (tupleCount == 0)
        throw new InvalidDataException("RNC Method 1 block contains zero tuples.");

      for (var tupleIndex = 0; tupleIndex < tupleCount; ++tupleIndex) {
        var rawCount = DecodeValue(ref reader, rawTable);
        if (rawCount > result.Length - outputPosition)
          throw new InvalidDataException("RNC literal run exceeds the declared uncompressed size.");
        reader.ReadRawBytes(result.AsSpan(outputPosition, rawCount));
        outputPosition += rawCount;

        if (tupleIndex == tupleCount - 1)
          continue;

        var distance = DecodeValue(ref reader, distanceTable) + 1;
        var length = DecodeValue(ref reader, lengthTable) + 2;
        if (distance > outputPosition)
          throw new InvalidDataException("RNC match references bytes before the start of the output.");
        if (length > result.Length - outputPosition)
          throw new InvalidDataException("RNC match exceeds the declared uncompressed size.");

        for (var i = 0; i < length; ++i) {
          result[outputPosition] = result[outputPosition - distance];
          ++outputPosition;
        }
      }
    }

    var actualUncompressedCrc = Crc16(result);
    if (actualUncompressedCrc != expectedUncompressedCrc)
      throw new InvalidDataException($"Uncompressed CRC mismatch: expected 0x{expectedUncompressedCrc:X4}, got 0x{actualUncompressedCrc:X4}.");

    return result;
  }

  private static byte[] DecompressStored(ReadOnlySpan<byte> data) {
    var size = ReadBE32(data, 4);
    if (size > int.MaxValue || size > (uint)(data.Length - HeaderSize))
      throw new InvalidDataException("Stored RNC payload is truncated or too large.");
    return data.Slice(HeaderSize, (int)size).ToArray();
  }

  private ref struct Method1BitReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _position;
    private uint _bitBuffer;
    private int _bitCount;

    public Method1BitReader(ReadOnlySpan<byte> data) {
      this._data = data;
      this._position = 0;
      this._bitBuffer = 0;
      this._bitCount = 0;
    }

    public int ReadBits(int count) {
      if ((uint)count > 16)
        throw new ArgumentOutOfRangeException(nameof(count));

      var result = 0u;
      var resultShift = 0;
      while (count > 0) {
        if (this._bitCount == 0)
          this.PrimeWord();

        var take = Math.Min(count, this._bitCount);
        var mask = (1u << take) - 1;
        result |= (this._bitBuffer & mask) << resultShift;
        this._bitBuffer >>= take;
        this._bitCount -= take;
        count -= take;
        resultShift += take;
      }
      return (int)result;
    }

    public void ReadRawBytes(Span<byte> destination) {
      if (destination.Length > this._data.Length - this._position)
        throw new InvalidDataException("Unexpected end of RNC literal data.");
      this._data.Slice(this._position, destination.Length).CopyTo(destination);
      this._position += destination.Length;
    }

    private void PrimeWord() {
      if (this._position >= this._data.Length)
        throw new InvalidDataException("Unexpected end of RNC bitstream.");

      var low = this._data[this._position++];
      var high = this._position < this._data.Length ? this._data[this._position++] : (byte)0;
      this._bitBuffer = (uint)(low | (high << 8));
      this._bitCount = 16;
    }
  }

  private readonly struct HuffmanTable(int[] lengths, int[] codes) {
    public int[] Lengths { get; } = lengths;
    public int[] Codes { get; } = codes;
  }

  private static HuffmanTable ReadHuffmanTable(ref Method1BitReader reader) {
    var count = reader.ReadBits(5);
    if (count > HuffmanSymbols)
      throw new InvalidDataException($"RNC Huffman table declares {count} symbols; Method 1 permits at most {HuffmanSymbols}.");

    var lengths = new int[HuffmanSymbols];
    for (var i = 0; i < count; ++i)
      lengths[i] = reader.ReadBits(4);
    return BuildCanonicalTable(lengths);
  }

  private static int DecodeValue(ref Method1BitReader reader, HuffmanTable table) {
    var code = 0;
    for (var length = 1; length <= 15; ++length) {
      code |= reader.ReadBits(1) << (length - 1);
      for (var symbol = 0; symbol < HuffmanSymbols; ++symbol) {
        if (table.Lengths[symbol] != length || table.Codes[symbol] != code)
          continue;
        if (symbol < 2)
          return symbol;
        return (1 << (symbol - 1)) | reader.ReadBits(symbol - 1);
      }
    }
    throw new InvalidDataException("Invalid RNC Huffman code.");
  }

  private static HuffmanTable BuildCanonicalTable(int[] lengths) {
    var counts = new int[16];
    foreach (var length in lengths) {
      if ((uint)length > 15)
        throw new InvalidDataException("RNC Huffman code length exceeds 15 bits.");
      if (length > 0)
        ++counts[length];
    }

    var nextCode = new int[16];
    var code = 0;
    for (var bits = 1; bits <= 15; ++bits) {
      code = (code + counts[bits - 1]) << 1;
      if (code + counts[bits] > 1 << bits)
        throw new InvalidDataException("RNC Huffman table is oversubscribed.");
      nextCode[bits] = code;
    }

    var codes = new int[HuffmanSymbols];
    for (var symbol = 0; symbol < HuffmanSymbols; ++symbol) {
      var length = lengths[symbol];
      if (length == 0)
        continue;
      codes[symbol] = ReverseBits(nextCode[length]++, length);
    }
    return new HuffmanTable(lengths, codes);
  }

  private static byte[] CompressCore(ReadOnlySpan<byte> input, RncCompressionOptions options) {
    var writer = new Method1BitWriter();
    writer.WriteBits(0, 1); // no lock
    writer.WriteBits(0, 1); // no encryption key

    var chunks = input.IsEmpty ? 0 : EncodeChunks(input, options, writer);
    var payload = writer.Finish();
    var uncompressedCrc = Crc16(input);
    var compressedCrc = Crc16(payload);
    return BuildHeader(
      method: 1,
      uncompressedSize: checked((uint)input.Length),
      compressedSize: checked((uint)payload.Length),
      uncompressedCrc,
      compressedCrc,
      leeway: 0,
      chunkCount: (byte)Math.Min(chunks, byte.MaxValue),
      payload);
  }

  private static int EncodeChunks(ReadOnlySpan<byte> input, RncCompressionOptions options, Method1BitWriter writer) {
    var previous = BuildHashChains(input);
    var chunkCount = 0;

    for (var blockStart = 0; blockStart < input.Length; blockStart += options.BlockSize) {
      var blockEnd = Math.Min(input.Length, blockStart + options.BlockSize);
      var tuples = TokenizeBlock(input, previous, blockStart, blockEnd, options);
      EncodeBlock(input, tuples, writer);
      ++chunkCount;
    }

    return chunkCount;
  }

  private readonly record struct Match(int Distance, int Length);
  private readonly record struct Tuple(int RawStart, int RawLength, int Distance = 0, int Length = 0) {
    public bool HasMatch => this.Length >= 2;
  }

  private static int[] BuildHashChains(ReadOnlySpan<byte> input) {
    var heads = new int[1 << 16];
    Array.Fill(heads, -1);
    var previous = new int[input.Length];
    Array.Fill(previous, -1);

    for (var position = 0; position + 1 < input.Length; ++position) {
      var hash = (input[position] << 8) | input[position + 1];
      previous[position] = heads[hash];
      heads[hash] = position;
    }
    return previous;
  }

  private static List<Tuple> TokenizeBlock(
      ReadOnlySpan<byte> input,
      int[] previous,
      int blockStart,
      int blockEnd,
      RncCompressionOptions options) {
    var tuples = new List<Tuple>();
    var position = blockStart;
    var rawStart = blockStart;

    while (position + 1 < blockEnd) {
      var match = FindMatch(input, previous, position, blockEnd, options);
      if (match.Length >= 2 && options.ParseStrategy == RncParseStrategy.Lazy && position + 2 < blockEnd) {
        var next = FindMatch(input, previous, position + 1, blockEnd, options);
        if (next.Length > match.Length) {
          ++position;
          continue;
        }
      }

      if (match.Length < 2) {
        ++position;
        continue;
      }

      tuples.Add(new Tuple(rawStart, position - rawStart, match.Distance, match.Length));
      position += match.Length;
      rawStart = position;
    }

    tuples.Add(new Tuple(rawStart, blockEnd - rawStart));
    return tuples;
  }

  private static Match FindMatch(
      ReadOnlySpan<byte> input,
      int[] previous,
      int position,
      int blockEnd,
      RncCompressionOptions options) {
    if (position + 1 >= blockEnd)
      return default;

    var candidate = previous[position];
    var minimumPosition = Math.Max(0, position - options.DictionarySize);
    var maximumLength = Math.Min(MaxMatchLength, blockEnd - position);
    var bestDistance = 0;
    var bestLength = 0;
    var steps = 0;

    while (candidate >= minimumPosition && candidate >= 0 && steps < options.SearchDepth) {
      var length = 2;
      while (length < maximumLength && input[candidate + length] == input[position + length])
        ++length;

      var distance = position - candidate;
      if (length > bestLength || length == bestLength && length >= 2 && (bestDistance == 0 || distance < bestDistance)) {
        bestLength = length;
        bestDistance = distance;
        if (length == maximumLength)
          break;
      }

      candidate = previous[candidate];
      ++steps;
    }

    // ProPack deliberately rejects two-byte matches when their offset needs more than one byte.
    return bestLength == 2 && bestDistance > 0x100 ? default : new Match(bestDistance, bestLength);
  }

  private static void EncodeBlock(ReadOnlySpan<byte> input, List<Tuple> tuples, Method1BitWriter writer) {
    Span<long> rawFrequencies = stackalloc long[HuffmanSymbols];
    Span<long> distanceFrequencies = stackalloc long[HuffmanSymbols];
    Span<long> lengthFrequencies = stackalloc long[HuffmanSymbols];

    foreach (var tuple in tuples) {
      ++rawFrequencies[Category(tuple.RawLength)];
      if (!tuple.HasMatch)
        continue;
      ++distanceFrequencies[Category(tuple.Distance - 1)];
      ++lengthFrequencies[Category(tuple.Length - 2)];
    }

    var rawTable = BuildHuffmanTable(rawFrequencies);
    var distanceTable = BuildHuffmanTable(distanceFrequencies);
    var lengthTable = BuildHuffmanTable(lengthFrequencies);

    WriteHuffmanTable(writer, rawTable);
    WriteHuffmanTable(writer, distanceTable);
    WriteHuffmanTable(writer, lengthTable);
    writer.WriteBits(tuples.Count, 16);

    for (var i = 0; i < tuples.Count; ++i) {
      var tuple = tuples[i];
      WriteValue(writer, rawTable, tuple.RawLength);
      writer.WriteRawBytes(input.Slice(tuple.RawStart, tuple.RawLength));
      if (i == tuples.Count - 1)
        continue;
      WriteValue(writer, distanceTable, tuple.Distance - 1);
      WriteValue(writer, lengthTable, tuple.Length - 2);
    }
  }

  private sealed class HuffmanNode {
    public int Symbol { get; }
    public HuffmanNode? Left { get; }
    public HuffmanNode? Right { get; }

    public HuffmanNode(int symbol) => this.Symbol = symbol;
    public HuffmanNode(HuffmanNode left, HuffmanNode right) {
      this.Symbol = -1;
      this.Left = left;
      this.Right = right;
    }
  }

  private static HuffmanTable BuildHuffmanTable(ReadOnlySpan<long> frequencies) {
    var lengths = new int[HuffmanSymbols];
    var queue = new PriorityQueue<HuffmanNode, (long Weight, int Order)>();
    var order = 0;
    for (var symbol = 0; symbol < HuffmanSymbols; ++symbol) {
      if (frequencies[symbol] > 0)
        queue.Enqueue(new HuffmanNode(symbol), (frequencies[symbol], order++));
    }

    if (queue.Count == 0)
      return BuildCanonicalTable(lengths);
    if (queue.Count == 1) {
      queue.TryPeek(out var only, out _);
      lengths[only!.Symbol] = 1;
      return BuildCanonicalTable(lengths);
    }

    while (queue.Count > 1) {
      queue.TryDequeue(out var left, out var leftPriority);
      queue.TryDequeue(out var right, out var rightPriority);
      queue.Enqueue(new HuffmanNode(left!, right!), (leftPriority.Weight + rightPriority.Weight, order++));
    }

    queue.TryDequeue(out var root, out _);
    AssignDepths(root!, 0, lengths);
    return BuildCanonicalTable(lengths);
  }

  private static void AssignDepths(HuffmanNode node, int depth, int[] lengths) {
    if (node.Symbol >= 0) {
      lengths[node.Symbol] = Math.Max(1, depth);
      return;
    }
    AssignDepths(node.Left!, depth + 1, lengths);
    AssignDepths(node.Right!, depth + 1, lengths);
  }

  private static void WriteHuffmanTable(Method1BitWriter writer, HuffmanTable table) {
    var count = HuffmanSymbols;
    while (count > 0 && table.Lengths[count - 1] == 0)
      --count;
    writer.WriteBits(count, 5);
    for (var i = 0; i < count; ++i)
      writer.WriteBits(table.Lengths[i], 4);
  }

  private static void WriteValue(Method1BitWriter writer, HuffmanTable table, int value) {
    var category = Category(value);
    var codeLength = table.Lengths[category];
    if (codeLength == 0)
      throw new InvalidOperationException($"RNC Huffman table does not contain category {category}.");

    writer.WriteBits(table.Codes[category], codeLength);
    if (category > 1)
      writer.WriteBits(value - (1 << (category - 1)), category - 1);
  }

  private static int Category(int value) {
    if ((uint)value > 0x7FFF)
      throw new InvalidOperationException($"RNC Method 1 value {value} exceeds the 15-bit category range.");
    return value <= 1 ? value : BitOperations.Log2((uint)value) + 1;
  }

  private sealed class Method1BitWriter {
    private readonly List<byte> _output = [];
    private readonly List<byte> _pendingRaw = [];
    private ushort _token;
    private int _bitCount;

    public void WriteBits(int value, int count) {
      if ((uint)count > 16)
        throw new ArgumentOutOfRangeException(nameof(count));

      var bits = (uint)value;
      while (count-- > 0) {
        this._token >>= 1;
        if ((bits & 1) != 0)
          this._token |= 0x8000;
        bits >>= 1;
        if (++this._bitCount == 16)
          this.FlushWord();
      }
    }

    public void WriteRawBytes(ReadOnlySpan<byte> bytes) {
      if (bytes.IsEmpty)
        return;
      if (this._bitCount == 0) {
        foreach (var value in bytes)
          this._output.Add(value);
      } else {
        foreach (var value in bytes)
          this._pendingRaw.Add(value);
      }
    }

    public byte[] Finish() {
      if (this._bitCount != 0 || this._pendingRaw.Count != 0) {
        this._token >>= 16 - this._bitCount;
        this._output.Add((byte)this._token);
        if (this._bitCount > 8 || this._pendingRaw.Count != 0)
          this._output.Add((byte)(this._token >> 8));
        this.FlushRaw();
        this._token = 0;
        this._bitCount = 0;
      }
      return [.. this._output];
    }

    private void FlushWord() {
      this._output.Add((byte)this._token);
      this._output.Add((byte)(this._token >> 8));
      this.FlushRaw();
      this._token = 0;
      this._bitCount = 0;
    }

    private void FlushRaw() {
      this._output.AddRange(this._pendingRaw);
      this._pendingRaw.Clear();
    }
  }

  private static int ReverseBits(int value, int count) {
    var result = 0;
    while (count-- > 0) {
      result = (result << 1) | (value & 1);
      value >>= 1;
    }
    return result;
  }

  private static byte[] BuildHeader(
      byte method,
      uint uncompressedSize,
      uint compressedSize,
      ushort uncompressedCrc,
      ushort compressedCrc,
      byte leeway,
      byte chunkCount,
      ReadOnlySpan<byte> payload) {
    var result = new byte[HeaderSize + payload.Length];
    result[0] = (byte)'R';
    result[1] = (byte)'N';
    result[2] = (byte)'C';
    result[3] = method;
    WriteBE32(result, 4, uncompressedSize);
    WriteBE32(result, 8, compressedSize);
    WriteBE16(result, 12, uncompressedCrc);
    WriteBE16(result, 14, compressedCrc);
    result[16] = leeway;
    result[17] = chunkCount;
    payload.CopyTo(result.AsSpan(HeaderSize));
    return result;
  }

  private static uint ReadBE32(ReadOnlySpan<byte> data, int offset)
    => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

  private static ushort ReadBE16(ReadOnlySpan<byte> data, int offset)
    => (ushort)((data[offset] << 8) | data[offset + 1]);

  private static void WriteBE32(Span<byte> data, int offset, uint value) {
    data[offset] = (byte)(value >> 24);
    data[offset + 1] = (byte)(value >> 16);
    data[offset + 2] = (byte)(value >> 8);
    data[offset + 3] = (byte)value;
  }

  private static void WriteBE16(Span<byte> data, int offset, ushort value) {
    data[offset] = (byte)(value >> 8);
    data[offset + 1] = (byte)value;
  }

  private static byte[] ReadAllBytes(Stream stream) {
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
  }
}
