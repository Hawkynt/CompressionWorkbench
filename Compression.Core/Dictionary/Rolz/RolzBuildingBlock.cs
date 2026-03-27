using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Rolz;

/// <summary>
/// Exposes ROLZ (Reduced-Offset LZ) as a benchmarkable building block.
/// Uses context-based match tables to reduce offset encoding cost. The previous byte
/// determines which of 256 offset tables to search for matches. Each context maintains a
/// circular buffer of up to 256 recent positions.
/// Header: 4-byte LE uncompressed size, then a bitstream of literals and matches.
/// </summary>
public sealed class RolzBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_ROLZ";
  /// <inheritdoc/>
  public string DisplayName => "ROLZ";
  /// <inheritdoc/>
  public string Description => "Reduced-Offset LZ with context-based match tables";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int WindowSize = 32768;
  private const int MinMatch = 3;
  private const int MaxMatch = 255;
  private const int NumContexts = 256;
  private const int TableSize = 256;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write uncompressed size (4 bytes, LE).
    Span<byte> sizeHeader = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(sizeHeader, data.Length);
    ms.Write(sizeHeader);

    if (data.Length == 0)
      return ms.ToArray();

    var writer = new BitWriter();

    // Context tables: each context has a circular buffer of positions.
    var tables = new int[NumContexts][];
    var tablePos = new int[NumContexts];
    var tableCount = new int[NumContexts];
    for (var i = 0; i < NumContexts; i++)
      tables[i] = new int[TableSize];

    var pos = 0;
    while (pos < data.Length) {
      var ctx = pos > 0 ? data[pos - 1] : 0;
      var bestLen = 0;
      var bestIdx = 0;

      // Search context table for matches.
      var count = tableCount[ctx];
      var table = tables[ctx];
      for (var i = 0; i < count; i++) {
        var candidate = table[i];
        if (pos - candidate > WindowSize)
          continue;
        if (candidate >= pos)
          continue;

        var len = 0;
        var maxLen = Math.Min(MaxMatch, data.Length - pos);
        while (len < maxLen && data[candidate + len] == data[pos + len])
          len++;

        if (len >= MinMatch && len > bestLen) {
          bestLen = len;
          bestIdx = i;
          if (bestLen == maxLen)
            break;
        }
      }

      if (bestLen >= MinMatch) {
        // Match: bit 1, 8-bit index, 8-bit (length - MinMatch).
        writer.WriteBit(1);
        writer.WriteBits(bestIdx, 8);
        writer.WriteBits(bestLen - MinMatch, 8);
        UpdateTable(tables, tablePos, tableCount, ctx, pos);
        pos += bestLen;
      } else {
        // Literal: bit 0, 8-bit data.
        writer.WriteBit(0);
        writer.WriteBits(data[pos], 8);
        UpdateTable(tables, tablePos, tableCount, ctx, pos);
        pos++;
      }
    }

    var compressed = writer.ToArray();
    ms.Write(compressed);

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var offset = 0;

    var uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    offset += 4;

    if (uncompressedSize == 0)
      return [];

    var reader = new BitReader(data[offset..]);
    var dst = new List<byte>(uncompressedSize);

    // Context tables: must be maintained identically to compressor.
    var tables = new int[NumContexts][];
    var tablePos = new int[NumContexts];
    var tableCount = new int[NumContexts];
    for (var i = 0; i < NumContexts; i++)
      tables[i] = new int[TableSize];

    while (dst.Count < uncompressedSize) {
      var ctx = dst.Count > 0 ? dst[dst.Count - 1] : 0;

      if (reader.ReadBit() == 0) {
        // Literal.
        var b = (byte)reader.ReadBits(8);
        UpdateTable(tables, tablePos, tableCount, ctx, dst.Count);
        dst.Add(b);
      } else {
        // Match.
        var idx = reader.ReadBits(8);
        var length = reader.ReadBits(8) + MinMatch;

        var table = tables[ctx];
        if (idx >= tableCount[ctx])
          throw new InvalidDataException($"ROLZ: invalid table index {idx} for context {ctx}.");

        var matchPos = table[idx];
        UpdateTable(tables, tablePos, tableCount, ctx, dst.Count);

        for (var i = 0; i < length; i++) {
          if (dst.Count >= uncompressedSize)
            throw new InvalidDataException("ROLZ: decompressed data exceeds expected size.");
          dst.Add(dst[matchPos + i]);
        }
      }
    }

    return dst.ToArray();
  }

  private static void UpdateTable(int[][] tables, int[] tablePos, int[] tableCount, int ctx, int position) {
    var table = tables[ctx];
    var wp = tablePos[ctx];
    table[wp] = position;
    tablePos[ctx] = (wp + 1) % TableSize;
    if (tableCount[ctx] < TableSize)
      tableCount[ctx]++;
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

    public void WriteBits(int value, int count) {
      for (var i = count - 1; i >= 0; --i)
        WriteBit((value >> i) & 1);
    }

    public byte[] ToArray() {
      if (_bitsUsed > 0) {
        _currentByte <<= (8 - _bitsUsed);
        _buffer.WriteByte((byte)_currentByte);
      }
      return _buffer.ToArray();
    }
  }

  private sealed class BitReader {
    private readonly byte[] _data;
    private int _bytePos;
    private int _bitPos = 8;

    public BitReader(ReadOnlySpan<byte> data) {
      _data = data.ToArray();
    }

    public int ReadBit() {
      if (_bitPos >= 8) {
        if (_bytePos >= _data.Length)
          throw new InvalidDataException("Unexpected end of ROLZ compressed data.");
        _bitPos = 0;
        _bytePos++;
      }
      var bit = (_data[_bytePos - 1] >> (7 - _bitPos)) & 1;
      _bitPos++;
      return bit;
    }

    public int ReadBits(int count) {
      var value = 0;
      for (var i = 0; i < count; ++i)
        value = (value << 1) | ReadBit();
      return value;
    }
  }
}
