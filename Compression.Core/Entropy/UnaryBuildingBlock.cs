using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Unary coding as a benchmarkable building block.
/// Each byte value N is encoded as N one-bits followed by a zero-bit.
/// </summary>
public sealed class UnaryBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Unary";
  /// <inheritdoc/>
  public string DisplayName => "Unary Coding";
  /// <inheritdoc/>
  public string Description => "Simplest universal code, encodes N as N ones followed by a zero";
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
    foreach (var b in data) {
      // N one-bits.
      for (var i = 0; i < b; i++)
        writer.WriteBit(1);
      // Terminating zero-bit.
      writer.WriteBit(0);
    }
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

    for (var i = 0; i < originalSize; i++) {
      var count = 0;
      while (ReadBit(src, ref bitIndex) == 1)
        count++;
      result[i] = (byte)count;
    }

    return result;
  }

  private static int ReadBit(byte[] data, ref int bitIndex) {
    if (bitIndex / 8 >= data.Length)
      throw new InvalidDataException("Unexpected end of Unary bitstream.");
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
