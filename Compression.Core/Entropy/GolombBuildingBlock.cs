using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Golomb/Rice coding as a benchmarkable building block.
/// </summary>
public sealed class GolombBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Golomb";
  /// <inheritdoc/>
  public string DisplayName => "Golomb/Rice";
  /// <inheritdoc/>
  public string Description => "Optimal coding for geometric distributions, Rice when M is power-of-2";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Auto-select M based on data mean: M = max(1, round(mean * ln(2))).
    var m = 1;
    if (data.Length > 0) {
      var sum = 0.0;
      foreach (var b in data)
        sum += b;
      var mean = sum / data.Length;
      m = Math.Max(1, (int)Math.Round(mean * Math.Log(2)));
      if (m > 255) m = 255;
    }

    // Write header: 1-byte M + 4-byte LE size.
    ms.WriteByte((byte)m);
    Span<byte> sizeHeader = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(sizeHeader, data.Length);
    ms.Write(sizeHeader);

    if (data.Length == 0)
      return ms.ToArray();

    // Encode data.
    var writer = new BitWriter(ms);
    foreach (var b in data)
      EncodeGolomb(writer, b, m);
    writer.Flush();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var offset = 0;

    // Read header: 1-byte M.
    var m = (int)data[offset++];

    // Read 4-byte LE original size.
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    offset += 4;

    if (originalSize == 0)
      return [];

    // Read remaining bytes as bitstream.
    var bitData = data[offset..].ToArray();
    var result = new byte[originalSize];
    var bitIndex = 0;

    for (var i = 0; i < originalSize; i++)
      result[i] = (byte)DecodeGolomb(bitData, ref bitIndex, m);

    return result;
  }

  private static void EncodeGolomb(BitWriter writer, int value, int m) {
    var q = value / m;
    var r = value % m;

    // Unary: q one-bits followed by a zero-bit.
    for (var i = 0; i < q; i++)
      writer.WriteBit(1);
    writer.WriteBit(0);

    // Truncated binary encoding of remainder.
    if (m == 1)
      return;

    var k = FloorLog2(m);
    var c = (1 << (k + 1)) - m;

    if (r < c) {
      WriteBitsHighFirst(writer, r, k);
    } else {
      WriteBitsHighFirst(writer, r + c, k + 1);
    }
  }

  private static int DecodeGolomb(byte[] data, ref int bitIndex, int m) {
    // Read unary quotient: count 1-bits until a 0-bit.
    var q = 0;
    while (ReadBit(data, ref bitIndex) == 1)
      q++;

    // Read truncated binary remainder.
    int r;
    if (m == 1) {
      r = 0;
    } else {
      var k = FloorLog2(m);
      var c = (1 << (k + 1)) - m;

      r = ReadBitsHighFirst(data, ref bitIndex, k);
      if (r >= c) {
        r = (r << 1) | ReadBit(data, ref bitIndex);
        r -= c;
      }
    }

    return q * m + r;
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

  private static void WriteBitsHighFirst(BitWriter writer, int value, int count) {
    for (var i = count - 1; i >= 0; i--)
      writer.WriteBit((value >> i) & 1);
  }

  private static int ReadBitsHighFirst(byte[] data, ref int bitIndex, int count) {
    var value = 0;
    for (var i = 0; i < count; i++)
      value = (value << 1) | ReadBit(data, ref bitIndex);
    return value;
  }

  private static int ReadBit(byte[] data, ref int bitIndex) {
    if (bitIndex / 8 >= data.Length)
      throw new InvalidDataException("Unexpected end of Golomb bitstream.");
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
