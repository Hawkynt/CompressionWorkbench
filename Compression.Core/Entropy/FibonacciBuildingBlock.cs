using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Fibonacci universal coding as a benchmarkable building block.
/// </summary>
public sealed class FibonacciBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Fibonacci";
  /// <inheritdoc/>
  public string DisplayName => "Fibonacci Coding";
  /// <inheritdoc/>
  public string Description => "Universal code using Zeckendorf representation with '11' terminators";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  // Pre-compute Fibonacci numbers. F(2)=1, F(3)=2, F(4)=3, F(5)=5, ...
  private static readonly int[] Fibs;
  // Pre-computed codewords for values 0-255: each entry is (bits, bitCount).
  private static readonly (uint Bits, int Count)[] CodeTable;

  static FibonacciBuildingBlock() {
    var fibs = new List<int> { 1, 2 };
    while (fibs[^1] < 300) {
      fibs.Add(fibs[^1] + fibs[^2]);
    }
    Fibs = [.. fibs];

    CodeTable = new (uint Bits, int Count)[256];
    for (var i = 0; i < 256; i++) {
      CodeTable[i] = EncodeFib(i + 1);
    }
  }

  private static (uint Bits, int Count) EncodeFib(int value) {
    var bits = 0u;
    var remaining = value;
    var highBit = 0;

    for (var i = Fibs.Length - 1; i >= 0; i--) {
      if (Fibs[i] <= remaining) {
        remaining -= Fibs[i];
        bits |= 1u << i;
        if (i > highBit) highBit = i;
      }
    }

    // The codeword is the bits plus a terminating 1-bit at position highBit+1.
    bits |= 1u << (highBit + 1);
    return (bits, highBit + 2);
  }

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write uncompressed size (4 bytes, LE).
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    // Write Fibonacci-coded bitstream.
    var writer = new BitWriter();
    for (var i = 0; i < data.Length; i++) {
      var (bits, count) = CodeTable[data[i]];
      // Write LSB first (bit 0 = F2 position).
      for (var b = 0; b < count; b++) {
        writer.WriteBit((int)((bits >> b) & 1));
      }
    }
    ms.Write(writer.ToArray());

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var offset = 0;

    // Read uncompressed size.
    var uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    offset += 4;

    if (uncompressedSize == 0)
      return [];

    var src = data[offset..].ToArray();
    var result = new List<byte>(uncompressedSize);
    var reader = new BitReader(src);
    var prevBit = 0;

    // Accumulate Fibonacci sum for current symbol.
    var sum = 0;
    var fibIndex = 0;

    while (result.Count < uncompressedSize) {
      var bit = reader.ReadBit();

      if (bit == 1 && prevBit == 1) {
        // "11" terminator found -- emit symbol.
        result.Add((byte)(sum - 1));
        sum = 0;
        fibIndex = 0;
        prevBit = 0;
      } else {
        if (bit == 1) {
          sum += Fibs[fibIndex];
        }
        fibIndex++;
        prevBit = bit;
      }
    }

    return [.. result];
  }

  private sealed class BitWriter {
    private readonly List<byte> _buffer = [];
    private int _currentByte;
    private int _bitsUsed;

    public void WriteBit(int bit) {
      _currentByte = (_currentByte << 1) | (bit & 1);
      if (++_bitsUsed == 8) {
        _buffer.Add((byte)_currentByte);
        _currentByte = 0;
        _bitsUsed = 0;
      }
    }

    public byte[] ToArray() {
      if (_bitsUsed > 0) {
        _currentByte <<= (8 - _bitsUsed);
        _buffer.Add((byte)_currentByte);
      }
      return [.. _buffer];
    }
  }

  private sealed class BitReader(byte[] data) {
    private int _bytePos;
    private int _bitPos = 8;

    public int ReadBit() {
      if (_bitPos >= 8) {
        if (_bytePos >= data.Length)
          throw new InvalidDataException("Unexpected end of Fibonacci compressed data.");
        _bitPos = 0;
        _bytePos++;
      }
      var bit = (data[_bytePos - 1] >> (7 - _bitPos)) & 1;
      _bitPos++;
      return bit;
    }
  }
}
