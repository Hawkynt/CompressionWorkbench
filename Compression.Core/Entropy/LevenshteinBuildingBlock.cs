using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Levenshtein coding as a benchmarkable building block.
/// A self-delimiting universal code: encodes positive integer N by recursively
/// prefixing the bit-length until it reaches 0, with a count prefix.
/// Byte values are mapped to positive integers as (value + 1).
/// </summary>
public sealed class LevenshteinBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Levenshtein";
  /// <inheritdoc/>
  public string DisplayName => "Levenshtein Coding";
  /// <inheritdoc/>
  public string Description => "Self-delimiting universal code with recursive length prefixing";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write 4-byte LE uncompressed size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var writer = new BitWriter(ms);
    foreach (var b in data)
      EncodeLevenshtein(writer, b + 1);
    writer.Flush();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var src = data[4..].ToArray();
    var result = new byte[originalSize];
    var bitIndex = 0;

    for (var i = 0; i < originalSize; i++)
      result[i] = (byte)(DecodeLevenshtein(src, ref bitIndex) - 1);

    return result;
  }

  private static void EncodeLevenshtein(BitWriter writer, int value) {
    if (value == 0) {
      writer.WriteBit(0);
      return;
    }

    // Build the chain of lengths: value → floor(log2(value)) → ... → 0.
    var chain = new List<int>();
    var v = value;
    while (v > 0) {
      chain.Add(v);
      v = FloorLog2(v);
    }

    // Write step count C in unary: C one-bits followed by a zero-bit.
    // C = chain.Count (number of values stored, not including the terminating 0).
    var c = chain.Count;
    for (var i = 0; i < c; i++)
      writer.WriteBit(1);
    writer.WriteBit(0);

    // Write each chain entry from the smallest (innermost) to the largest,
    // omitting the leading 1-bit of each (it's implicit).
    for (var i = chain.Count - 1; i >= 0; i--) {
      var n = i < chain.Count - 1 ? chain[i + 1] : 0;
      var entry = chain[i];
      // Write lower n bits of entry (MSB first, skipping leading 1).
      for (var b = n - 1; b >= 0; b--)
        writer.WriteBit((entry >> b) & 1);
    }
  }

  private static int DecodeLevenshtein(byte[] data, ref int bitIndex) {
    // Read unary step count: count 1-bits until 0.
    var c = 0;
    while (ReadBit(data, ref bitIndex) == 1)
      c++;

    if (c == 0)
      return 0;

    // Decode the chain bottom-up.
    var n = 0;
    for (var i = 0; i < c; i++) {
      // Read n bits and prepend implicit leading 1.
      var value = 1;
      for (var b = 0; b < n; b++)
        value = (value << 1) | ReadBit(data, ref bitIndex);
      n = value;
    }

    return n;
  }

  private static int FloorLog2(int value) {
    var result = 0;
    var v = value;
    while (v > 1) {
      result++;
      v >>= 1;
    }
    return result;
  }

  private static int ReadBit(byte[] data, ref int bitIndex) {
    if (bitIndex / 8 >= data.Length)
      throw new InvalidDataException("Unexpected end of Levenshtein bitstream.");
    var bit = (data[bitIndex / 8] >> (7 - (bitIndex % 8))) & 1;
    bitIndex++;
    return bit;
  }

  private sealed class BitWriter(Stream output) {
    private byte _buffer;
    private int _bitCount;

    public void WriteBit(int bit) {
      _buffer = (byte)((_buffer << 1) | (bit & 1));
      _bitCount++;
      if (_bitCount == 8) {
        output.WriteByte(_buffer);
        _buffer = 0;
        _bitCount = 0;
      }
    }

    public void Flush() {
      if (_bitCount > 0) {
        _buffer <<= (8 - _bitCount);
        output.WriteByte(_buffer);
        _buffer = 0;
        _bitCount = 0;
      }
    }
  }
}
