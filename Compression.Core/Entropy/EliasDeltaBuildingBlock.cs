using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Elias Delta coding as a benchmarkable building block.
/// Encodes positive integer N by Gamma-coding the length of N, then appending the lower bits.
/// Byte values are mapped to positive integers as (value + 1).
/// </summary>
public sealed class EliasDeltaBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_EliasDelta";
  /// <inheritdoc/>
  public string DisplayName => "Elias Delta";
  /// <inheritdoc/>
  public string Description => "Universal code for positive integers, Gamma-codes the bit length";
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
      EncodeDelta(writer, b + 1);
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
      result[i] = (byte)(DecodeDelta(src, ref bitIndex) - 1);

    return result;
  }

  private static void EncodeDelta(BitWriter writer, int value) {
    // N = floor(log2(value)).
    var n = 0;
    var v = value;
    while (v > 1) {
      n++;
      v >>= 1;
    }

    // Gamma-encode (N + 1): write floor(log2(N+1)) zero-bits, then binary of (N+1).
    var lenBits = n + 1;
    var lenLen = 0;
    var tmp = lenBits;
    while (tmp > 1) {
      lenLen++;
      tmp >>= 1;
    }

    for (var i = 0; i < lenLen; i++)
      writer.WriteBit(0);
    for (var i = lenLen; i >= 0; i--)
      writer.WriteBit((lenBits >> i) & 1);

    // Write lower N bits of value (without leading 1).
    for (var i = n - 1; i >= 0; i--)
      writer.WriteBit((value >> i) & 1);
  }

  private static int DecodeDelta(byte[] data, ref int bitIndex) {
    // Read Gamma-coded length: count leading zeros, then read that many more bits.
    var lenLen = 0;
    while (ReadBit(data, ref bitIndex) == 0)
      lenLen++;

    // Leading 1-bit consumed. Read remaining lenLen bits to form (N + 1).
    var lenBits = 1;
    for (var i = 0; i < lenLen; i++)
      lenBits = (lenBits << 1) | ReadBit(data, ref bitIndex);

    var n = lenBits - 1;

    // Read N lower bits of value (implicit leading 1).
    var value = 1;
    for (var i = 0; i < n; i++)
      value = (value << 1) | ReadBit(data, ref bitIndex);

    return value;
  }

  private static int ReadBit(byte[] data, ref int bitIndex) {
    if (bitIndex / 8 >= data.Length)
      throw new InvalidDataException("Unexpected end of Elias Delta bitstream.");
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
