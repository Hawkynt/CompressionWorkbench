using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes order-0 arithmetic coding as a benchmarkable building block.
/// </summary>
public sealed class ArithmeticBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Arithmetic";
  /// <inheritdoc/>
  public string DisplayName => "Arithmetic Coding";
  /// <inheritdoc/>
  public string Description => "Order-0 arithmetic coding with frequency table";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  private const uint Half = 0x80000000u;
  private const uint Quarter = 0x40000000u;
  private const uint ThreeQuarters = 0xC0000000u;
  private const int NumSymbols = 257; // 256 byte values + 1 EOF
  private const int EofSymbol = 256;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write header: 4-byte LE original size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    // Count frequencies.
    var freq = new uint[256];
    for (var i = 0; i < data.Length; i++)
      freq[data[i]]++;

    // Cap frequencies at 65535.
    var freqTable = new ushort[256];
    for (var i = 0; i < 256; i++)
      freqTable[i] = (ushort)Math.Min(freq[i], 65535);

    // Write frequency table: 256 x 2-byte LE.
    Span<byte> freqBytes = stackalloc byte[512];
    for (var i = 0; i < 256; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(freqBytes.Slice(i * 2, 2), freqTable[i]);
    ms.Write(freqBytes);

    if (data.Length == 0) return ms.ToArray();

    // Build cumulative frequency table.
    var cumFreq = BuildCumulativeFrequencies(freqTable);

    // Encode.
    var writer = new BitWriter();
    uint low = 0;
    uint high = 0xFFFFFFFFu;
    var pending = 0;

    for (var i = 0; i < data.Length; i++)
      EncodeSymbol(ref low, ref high, ref pending, writer, cumFreq, data[i]);

    // Encode EOF.
    EncodeSymbol(ref low, ref high, ref pending, writer, cumFreq, EofSymbol);

    // Flush: output enough bits to identify the final range.
    pending++;
    if (low < Quarter) {
      writer.WriteBit(0);
      WritePendingBits(writer, ref pending, 1);
    } else {
      writer.WriteBit(1);
      WritePendingBits(writer, ref pending, 0);
    }

    ms.Write(writer.ToArray());
    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var offset = 0;

    // Read 4-byte LE original size.
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    offset += 4;

    if (originalSize == 0)
      return [];

    // Read frequency table.
    var freqTable = new ushort[256];
    for (var i = 0; i < 256; i++) {
      freqTable[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
      offset += 2;
    }

    // Build cumulative frequency table.
    var cumFreq = BuildCumulativeFrequencies(freqTable);
    var totalFreq = cumFreq[NumSymbols];

    // Read compressed bitstream.
    var compressedData = data[offset..].ToArray();
    var reader = new BitReader(compressedData);

    // Initialize code value from first 32 bits.
    uint code = 0;
    for (var i = 0; i < 32; i++)
      code = (code << 1) | (uint)reader.ReadBit();

    uint low = 0;
    uint high = 0xFFFFFFFFu;
    var result = new byte[originalSize];
    var pos = 0;

    while (pos < originalSize) {
      var range = (ulong)(high - low) + 1;

      // Find symbol using cumulative frequencies.
      var symbol = FindSymbol(cumFreq, code, low, high, totalFreq);

      if (symbol == EofSymbol)
        break;

      if (symbol < 256)
        result[pos++] = (byte)symbol;

      // Narrow range.
      var symLow = cumFreq[symbol];
      var symHigh = cumFreq[symbol + 1];
      high = low + (uint)((range * symHigh) / totalFreq - 1);
      low = low + (uint)((range * symLow) / totalFreq);

      // Renormalize.
      while (true) {
        if (high < Half) {
          // Both in lower half.
        } else if (low >= Half) {
          low -= Half;
          high -= Half;
          code -= Half;
        } else if (low >= Quarter && high < ThreeQuarters) {
          low -= Quarter;
          high -= Quarter;
          code -= Quarter;
        } else {
          break;
        }

        low <<= 1;
        high = (high << 1) | 1;
        code = (code << 1) | (uint)reader.ReadBit();
      }
    }

    return result[..pos];
  }

  private static uint[] BuildCumulativeFrequencies(ushort[] freqTable) {
    var cumFreq = new uint[NumSymbols + 1];
    cumFreq[0] = 0;
    for (var i = 0; i < 256; i++)
      cumFreq[i + 1] = cumFreq[i] + freqTable[i];
    // EOF symbol gets frequency 1.
    cumFreq[NumSymbols] = cumFreq[256] + 1;
    return cumFreq;
  }

  private static int FindSymbol(uint[] cumFreq, uint code, uint low, uint high, uint totalFreq) {
    var range = (ulong)(high - low) + 1;
    var target = (uint)((((ulong)(code - low) + 1) * totalFreq - 1) / range);

    for (var s = 0; s < NumSymbols; s++) {
      if (cumFreq[s + 1] > target)
        return s;
    }
    return EofSymbol;
  }

  private static void EncodeSymbol(ref uint low, ref uint high, ref int pending,
    BitWriter writer, uint[] cumFreq, int symbol) {
    var range = (ulong)(high - low) + 1;
    var totalFreq = cumFreq[NumSymbols];
    var symLow = cumFreq[symbol];
    var symHigh = cumFreq[symbol + 1];

    high = low + (uint)((range * symHigh) / totalFreq - 1);
    low = low + (uint)((range * symLow) / totalFreq);

    while (true) {
      if (high < Half) {
        writer.WriteBit(0);
        WritePendingBits(writer, ref pending, 1);
      } else if (low >= Half) {
        writer.WriteBit(1);
        WritePendingBits(writer, ref pending, 0);
        low -= Half;
        high -= Half;
      } else if (low >= Quarter && high < ThreeQuarters) {
        pending++;
        low -= Quarter;
        high -= Quarter;
      } else {
        break;
      }

      low <<= 1;
      high = (high << 1) | 1;
    }
  }

  private static void WritePendingBits(BitWriter writer, ref int pending, int bit) {
    while (pending > 0) {
      writer.WriteBit(bit);
      pending--;
    }
  }

  private sealed class BitWriter {
    private readonly MemoryStream _buffer = new();
    private int _currentByte;
    private int _bitsUsed;

    public void WriteBit(int bit) {
      _currentByte = (_currentByte << 1) | (bit & 1);
      if (++_bitsUsed == 8) {
        _buffer.WriteByte((byte)_currentByte);
        _currentByte = 0;
        _bitsUsed = 0;
      }
    }

    public byte[] ToArray() {
      if (_bitsUsed > 0) {
        _currentByte <<= (8 - _bitsUsed);
        _buffer.WriteByte((byte)_currentByte);
      }
      return _buffer.ToArray();
    }
  }

  private sealed class BitReader(byte[] data) {
    private int _bytePos;
    private int _bitPos = 8;

    public int ReadBit() {
      if (_bitPos >= 8) {
        if (_bytePos >= data.Length)
          return 0;
        _bitPos = 0;
        _bytePos++;
      }
      var bit = (data[_bytePos - 1] >> (7 - _bitPos)) & 1;
      _bitPos++;
      return bit;
    }
  }
}
