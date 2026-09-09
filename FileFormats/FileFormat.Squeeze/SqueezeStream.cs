using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Squeeze;

/// <summary>
/// Provides static methods for reading and writing the CP/M Squeeze (.sqz / .??q) file format.
/// </summary>
/// <remarks>
/// Richard Greenlaw's Squeeze format first applies a 0x90 run-length transform and then Huffman-codes
/// that transformed byte stream. Standalone SQ files contain, in order:
/// <list type="bullet">
///   <item><description>2-byte magic (0x76, 0xFF = 0xFF76 LE).</description></item>
///   <item><description>2-byte checksum (sum of the original, expanded bytes modulo 65536, LE).</description></item>
///   <item><description>Null-terminated ASCII original filename.</description></item>
///   <item><description>2-byte Huffman node count (LE).</description></item>
///   <item><description>Node array: each node is two signed 16-bit LE values (left, right).
///   Non-negative values are child node indices; negative values encode leaves as -(symbol + 1).</description></item>
///   <item><description>Huffman-coded bitstream (LSB-first bit order) terminated by EOF symbol (256).</description></item>
/// </list>
/// A zero-node tree represents an empty stream.
/// </remarks>
public static class SqueezeStream {

  private const int HistoricalMinimumRunLength = 3;
  private const int DisabledMinimumRunLength = 256;

  /// <summary>
  /// Decompresses a Squeeze-format stream from <paramref name="input"/> and writes the result to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing Squeeze-compressed data.</param>
  /// <param name="output">The stream to which the decompressed data is written.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes are invalid, the tree/RLE stream is malformed, or the checksum does not match.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> word = stackalloc byte[2];
    input.ReadExactly(word);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(word);
    if (magic != SqueezeConstants.Magic)
      throw new InvalidDataException($"Invalid Squeeze magic: 0x{magic:X4}, expected 0x{SqueezeConstants.Magic:X4}.");

    input.ReadExactly(word);
    var expectedChecksum = BinaryPrimitives.ReadUInt16LittleEndian(word);

    _ = ReadNullTerminatedString(input);

    input.ReadExactly(word);
    var nodeCount = BinaryPrimitives.ReadUInt16LittleEndian(word);
    if (nodeCount > SqueezeConstants.MaxNodes)
      throw new InvalidDataException($"Squeeze node count {nodeCount} exceeds maximum {SqueezeConstants.MaxNodes}.");

    if (nodeCount == 0) {
      if (expectedChecksum != 0)
        throw new InvalidDataException($"Squeeze checksum mismatch: computed 0x0000, expected 0x{expectedChecksum:X4}.");
      return;
    }

    var left = new short[nodeCount];
    var right = new short[nodeCount];
    Span<byte> nodeBytes = stackalloc byte[4];
    for (var i = 0; i < nodeCount; ++i) {
      input.ReadExactly(nodeBytes);
      left[i] = BinaryPrimitives.ReadInt16LittleEndian(nodeBytes);
      right[i] = BinaryPrimitives.ReadInt16LittleEndian(nodeBytes[2..]);
    }

    using var result = new MemoryStream();
    var currentByte = 0;
    var bitsLeft = 0;
    var sawRleDelimiter = false;
    var haveLastByte = false;
    byte lastByte = 0;
    ushort checksum = 0;

    while (true) {
      var symbol = DecodeSymbol(input, left, right, ref currentByte, ref bitsLeft);
      if (symbol == SqueezeConstants.EofMarker) {
        if (sawRleDelimiter)
          throw new InvalidDataException("Squeeze stream ends in an incomplete RLE escape.");
        break;
      }
      if (symbol is < 0 or > byte.MaxValue)
        throw new InvalidDataException($"Squeeze tree contains invalid symbol {symbol}.");

      var value = (byte)symbol;
      if (sawRleDelimiter) {
        if (value == 0) {
          WriteExpandedByte(result, SqueezeConstants.RleDelimiter, ref checksum);
          lastByte = SqueezeConstants.RleDelimiter;
          haveLastByte = true;
        } else {
          if (!haveLastByte)
            throw new InvalidDataException("Squeeze RLE count appears before a literal byte.");

          // The first copy was emitted before the delimiter, so a count N contributes N-1 more copies.
          for (var i = 1; i < value; ++i)
            WriteExpandedByte(result, lastByte, ref checksum);
        }

        sawRleDelimiter = false;
        continue;
      }

      if (value == SqueezeConstants.RleDelimiter) {
        sawRleDelimiter = true;
        continue;
      }

      WriteExpandedByte(result, value, ref checksum);
      lastByte = value;
      haveLastByte = true;
    }

    if (checksum != expectedChecksum)
      throw new InvalidDataException($"Squeeze checksum mismatch: computed 0x{checksum:X4}, expected 0x{expectedChecksum:X4}.");

    result.Position = 0;
    result.CopyTo(output);
  }

  /// <summary>
  /// Compresses data from <paramref name="input"/> and writes a Squeeze-format stream to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing uncompressed data.</param>
  /// <param name="output">The stream to which the Squeeze-compressed data is written.</param>
  /// <param name="originalFilename">The original filename to embed in the header. Defaults to an empty string.</param>
  public static void Compress(Stream input, Stream output, string originalFilename = "")
    => Compress(input, output, HistoricalMinimumRunLength, originalFilename);

  /// <summary>
  /// Encodes SQ while choosing the smallest run length that is represented by an RLE token.
  /// A value of 256 suppresses repeat tokens while still escaping literal 0x90 bytes.
  /// </summary>
  internal static void Compress(Stream input, Stream output, int minimumRunLength, string originalFilename = "") {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(originalFilename);

    if (minimumRunLength is < HistoricalMinimumRunLength or > DisabledMinimumRunLength)
      throw new ArgumentOutOfRangeException(nameof(minimumRunLength), minimumRunLength,
        $"Squeeze RLE minimum run length must be in {HistoricalMinimumRunLength}..{DisabledMinimumRunLength}.");
    if (originalFilename.Contains('\0'))
      throw new ArgumentException("Squeeze filenames cannot contain an embedded NUL.", nameof(originalFilename));

    var data = ReadAllBytes(input);
    ushort checksum = 0;
    foreach (var value in data)
      checksum += value;

    var rle = EncodeRle(data, minimumRunLength);

    Span<byte> word = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(word, SqueezeConstants.Magic);
    output.Write(word);
    BinaryPrimitives.WriteUInt16LittleEndian(word, checksum);
    output.Write(word);

    output.Write(Encoding.ASCII.GetBytes(originalFilename));
    output.WriteByte(0);

    if (rle.Length == 0) {
      BinaryPrimitives.WriteUInt16LittleEndian(word, 0);
      output.Write(word);
      return;
    }

    var freq = new long[257];
    foreach (var value in rle)
      ++freq[value];
    freq[SqueezeConstants.EofMarker] = 1;

    BuildTree(freq, out var left, out var right, out var codes, out var codeLens);
    var nodeCount = left.Length;
    if (nodeCount > SqueezeConstants.MaxNodes)
      throw new InvalidDataException($"Squeeze Huffman tree has {nodeCount} nodes; maximum is {SqueezeConstants.MaxNodes}.");

    BinaryPrimitives.WriteUInt16LittleEndian(word, checked((ushort)nodeCount));
    output.Write(word);

    Span<byte> nodeBytes = stackalloc byte[4];
    for (var i = 0; i < nodeCount; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(nodeBytes, left[i]);
      BinaryPrimitives.WriteInt16LittleEndian(nodeBytes[2..], right[i]);
      output.Write(nodeBytes);
    }

    ulong bitBuffer = 0;
    var bitCount = 0;
    foreach (var value in rle)
      WriteBits(output, codes[value], codeLens[value], ref bitBuffer, ref bitCount);
    WriteBits(output, codes[SqueezeConstants.EofMarker], codeLens[SqueezeConstants.EofMarker], ref bitBuffer, ref bitCount);

    if (bitCount > 0)
      output.WriteByte((byte)bitBuffer);
  }

  private static byte[] EncodeRle(ReadOnlySpan<byte> source, int minimumRunLength) {
    using var output = new MemoryStream(source.Length);
    var offset = 0;

    while (offset < source.Length) {
      var value = source[offset];
      if (value == SqueezeConstants.RleDelimiter) {
        output.WriteByte(SqueezeConstants.RleDelimiter);
        output.WriteByte(0);
        ++offset;
        continue;
      }

      var runLength = 1;
      while (offset + runLength < source.Length && source[offset + runLength] == value)
        ++runLength;

      var remaining = runLength;
      while (remaining > 0) {
        var count = Math.Min(remaining, byte.MaxValue);
        if (count >= minimumRunLength) {
          output.WriteByte(value);
          output.WriteByte(SqueezeConstants.RleDelimiter);
          output.WriteByte((byte)count);
        } else {
          for (var i = 0; i < count; ++i)
            output.WriteByte(value);
        }
        remaining -= count;
      }

      offset += runLength;
    }

    return output.ToArray();
  }

  private static void WriteExpandedByte(Stream output, byte value, ref ushort checksum) {
    output.WriteByte(value);
    checksum += value;
  }

  private static int DecodeSymbol(
    Stream input,
    short[] left,
    short[] right,
    ref int currentByte,
    ref int bitsLeft
  ) {
    var node = 0;
    var traversed = 0;

    while (true) {
      if ((uint)node >= (uint)left.Length)
        throw new InvalidDataException($"Squeeze tree references invalid node index {node}.");
      if (++traversed > left.Length)
        throw new InvalidDataException("Squeeze Huffman tree contains a cycle.");

      if (bitsLeft == 0) {
        currentByte = input.ReadByte();
        if (currentByte < 0)
          throw new InvalidDataException("Unexpected end of Squeeze bitstream.");
        bitsLeft = 8;
      }

      var bit = currentByte & 1;
      currentByte >>= 1;
      --bitsLeft;

      var child = bit == 0 ? left[node] : right[node];
      if (child < 0)
        return -(child + 1);
      node = child;
    }
  }

  private static void WriteBits(Stream output, uint code, int length, ref ulong bitBuffer, ref int bitCount) {
    // code is already stored LSB-first (bit zero is emitted first). The accumulator is 64-bit so
    // a long code plus the at-most-seven pending bits cannot truncate before completed bytes drain.
    bitBuffer |= (ulong)code << bitCount;
    bitCount += length;

    while (bitCount >= 8) {
      output.WriteByte((byte)bitBuffer);
      bitBuffer >>= 8;
      bitCount -= 8;
    }
  }

  /// <summary>
  /// Builds a Huffman tree from symbol frequencies and serializes it into the Squeeze node-array format.
  /// </summary>
  private static void BuildTree(long[] freq, out short[] left, out short[] right, out uint[] codes, out int[] codeLens) {
    var symbolCount = 0;
    for (var i = 0; i < freq.Length; ++i)
      if (freq[i] > 0)
        ++symbolCount;

    if (symbolCount == 0)
      throw new InvalidOperationException("No symbols to encode.");

    if (symbolCount == 1) {
      var sym = -1;
      for (var i = 0; i < freq.Length; ++i)
        if (freq[i] > 0) { sym = i; break; }

      left = [(short)(-(sym + 1))];
      right = [(short)(-(sym + 1))];
      codes = new uint[257];
      codeLens = new int[257];
      codes[sym] = 0;
      codeLens[sym] = 1;
      return;
    }

    // Squeeze writes the tree itself, so equal-frequency ordering is part of the produced bytes.
    // Use the repository's deterministic rule directly: (weight, symbol) for leaves, followed by
    // internal nodes in creation order on equal weight. The two-queue merge makes that total order explicit.
    var leafCount = symbolCount;
    var leaves = new (long Weight, int Symbol)[leafCount];
    var filled = 0;
    for (var i = 0; i < freq.Length; ++i)
      if (freq[i] > 0)
        leaves[filled++] = (freq[i], i);

    Array.Sort(leaves, static (a, b) => a.Weight != b.Weight
      ? a.Weight.CompareTo(b.Weight)
      : a.Symbol.CompareTo(b.Symbol));

    var internalCount = leafCount - 1;
    var internalWeight = new long[internalCount];
    var nodeLeft = new short[internalCount];
    var nodeRight = new short[internalCount];

    var leafHead = 0;
    var internalHead = 0;
    var created = 0;

    (short Value, long Weight) TakeSmallest() {
      var takeLeaf = leafHead < leafCount
                     && (internalHead >= created || leaves[leafHead].Weight <= internalWeight[internalHead]);
      if (takeLeaf) {
        var leaf = leaves[leafHead++];
        return ((short)(-(leaf.Symbol + 1)), leaf.Weight);
      }

      var index = internalHead++;
      return ((short)index, internalWeight[index]);
    }

    while (created < internalCount) {
      var (leftValue, leftWeight) = TakeSmallest();
      var (rightValue, rightWeight) = TakeSmallest();
      nodeLeft[created] = leftValue;
      nodeRight[created] = rightValue;
      internalWeight[created] = leftWeight + rightWeight;
      ++created;
    }

    var rootId = internalCount - 1;
    left = new short[internalCount];
    right = new short[internalCount];

    var remap = new int[internalCount];
    remap[rootId] = 0;
    var next = 1;
    for (var i = 0; i < internalCount; ++i) {
      if (i == rootId)
        continue;
      remap[i] = next++;
    }

    for (var i = 0; i < internalCount; ++i) {
      var newIndex = remap[i];
      left[newIndex] = RemapChild(nodeLeft[i], remap);
      right[newIndex] = RemapChild(nodeRight[i], remap);
    }

    codes = new uint[257];
    codeLens = new int[257];
    GenerateCodes(left, right, 0, 0, 0, codes, codeLens);
  }

  private static short RemapChild(short child, int[] remap)
    => child >= 0 ? (short)remap[child] : child;

  private static void GenerateCodes(
    short[] left,
    short[] right,
    int node,
    uint code,
    int depth,
    uint[] codes,
    int[] codeLens
  ) {
    var leftChild = left[node];
    var rightChild = right[node];

    if (leftChild < 0) {
      var symbol = -(leftChild + 1);
      codes[symbol] = code;
      codeLens[symbol] = Math.Max(depth + 1, 1);
    } else {
      GenerateCodes(left, right, leftChild, code, depth + 1, codes, codeLens);
    }

    if (rightChild < 0) {
      var symbol = -(rightChild + 1);
      codes[symbol] = code | (1u << depth);
      codeLens[symbol] = Math.Max(depth + 1, 1);
    } else {
      GenerateCodes(left, right, rightChild, code | (1u << depth), depth + 1, codes, codeLens);
    }
  }

  private static string ReadNullTerminatedString(Stream stream) {
    var bytes = new List<byte>();
    while (true) {
      var value = stream.ReadByte();
      if (value < 0)
        throw new InvalidDataException("Squeeze header ends before the filename terminator.");
      if (value == 0)
        return Encoding.ASCII.GetString(bytes.ToArray());
      bytes.Add((byte)value);
    }
  }

  private static byte[] ReadAllBytes(Stream stream) {
    if (stream is MemoryStream memoryStream && memoryStream.Position == 0)
      return memoryStream.ToArray();

    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }
}
