using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Elias Gamma coding as a benchmarkable building block.
/// Encodes positive integer N as floor(log2(N)) zero-bits followed by the binary representation of N.
/// Byte values are mapped to positive integers as (value + 1).
/// </summary>
public sealed class EliasGammaBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_EliasGamma";
  /// <inheritdoc/>
  public string DisplayName => "Elias Gamma";
  /// <inheritdoc/>
  public string Description => "Universal code for positive integers using unary length prefix";
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
      EncodeGamma(writer, b + 1);
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
      result[i] = (byte)(DecodeGamma(src, ref bitIndex) - 1);

    return result;
  }

  private static void EncodeGamma(BitWriter writer, int value) {
    // Find floor(log2(value)).
    var n = 0;
    var v = value;
    while (v > 1) {
      n++;
      v >>= 1;
    }

    // Write n zero-bits.
    for (var i = 0; i < n; i++)
      writer.WriteBit(0);

    // Write (n+1)-bit binary representation of value, MSB first.
    for (var i = n; i >= 0; i--)
      writer.WriteBit((value >> i) & 1);
  }

  private static int DecodeGamma(byte[] data, ref int bitIndex) {
    // Count leading zero-bits.
    var n = 0;
    while (ReadBit(data, ref bitIndex) == 0)
      n++;

    // The leading 1-bit was already consumed. Read remaining n bits.
    var value = 1;
    for (var i = 0; i < n; i++)
      value = (value << 1) | ReadBit(data, ref bitIndex);

    return value;
  }

  private static int ReadBit(byte[] data, ref int bitIndex) {
    if (bitIndex / 8 >= data.Length)
      throw new InvalidDataException("Unexpected end of Elias Gamma bitstream.");
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
