using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzs;

/// <summary>
/// Exposes Stac LZS (RFC 1967/1974) as a benchmarkable building block.
/// An LZSS variant using 7-bit offsets (1-127) and 11-bit offsets (128-2047),
/// with 2-bit or 8-bit match lengths. Used in Cisco IOS and Stac hardware compression.
/// </summary>
public sealed class LzsBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzs";
  /// <inheritdoc/>
  public string DisplayName => "LZS";
  /// <inheritdoc/>
  public string Description => "Stac LZS (RFC 1967), 7/11-bit offset LZSS variant for networking";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MaxOffset = 2047;
  private const int MinMatch = 2;
  private const int MaxShortLength = 4;  // Lengths 2-4 use 2-bit codes (00,01,10). 11 = extended.
  private const int WindowSize = 2048;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write 4-byte LE uncompressed size.
    Span<byte> headerBuf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(headerBuf, data.Length);
    ms.Write(headerBuf);

    if (data.Length == 0)
      return ms.ToArray();

    var writer = new BitWriter(ms);
    var hashHead = new int[1 << 14];
    var hashPrev = new int[data.Length];
    Array.Fill(hashHead, -1);

    var pos = 0;
    while (pos < data.Length) {
      // Find best match using hash chain.
      var bestLen = 0;
      var bestOff = 0;

      if (pos >= MinMatch && pos + MinMatch <= data.Length) {
        var hash = HashAt(data, pos);
        var chainLen = 0;
        var idx = hashHead[hash];
        var minPos = Math.Max(0, pos - MaxOffset);

        while (idx >= minPos && chainLen < 128) {
          var len = 0;
          var maxLen = Math.Min(data.Length - pos, 255 + MaxShortLength);
          while (len < maxLen && data[idx + len] == data[pos + len])
            len++;

          if (len >= MinMatch && len > bestLen) {
            bestLen = len;
            bestOff = pos - idx;
          }

          chainLen++;
          idx = hashPrev[idx];
        }
      }

      // Update hash chain for current position.
      if (pos + 2 <= data.Length) {
        var h = HashAt(data, pos);
        hashPrev[pos] = hashHead[h];
        hashHead[h] = pos;
      }

      if (bestLen >= MinMatch) {
        // Write match: 1-bit flag, offset, length.
        writer.WriteBit(1);

        // Offset encoding.
        if (bestOff <= 127) {
          // 7-bit short offset: 1 + 7 bits.
          writer.WriteBit(1);
          WriteBits(writer, bestOff, 7);
        } else {
          // 11-bit long offset: 0 + 11 bits.
          writer.WriteBit(0);
          WriteBits(writer, bestOff, 11);
        }

        // Length encoding.
        if (bestLen <= MaxShortLength) {
          // Lengths 2-5: 2-bit code (00=2, 01=3, 10=4, 11=5).
          WriteBits(writer, bestLen - 2, 2);
        } else {
          // Length 5+: write 11 as 2-bit code, then (length-5) in nibbles.
          // Nibble 15 means "add 15 and read another nibble".
          WriteBits(writer, 3, 2); // 11 = extended length
          var remaining = bestLen - 5;
          while (remaining >= 15) {
            WriteBits(writer, 15, 4);
            remaining -= 15;
          }
          WriteBits(writer, remaining, 4);
        }

        // Update hash chain for skipped positions.
        for (var j = 1; j < bestLen && pos + j + 2 <= data.Length; j++) {
          var h = HashAt(data, pos + j);
          hashPrev[pos + j] = hashHead[h];
          hashHead[h] = pos + j;
        }

        pos += bestLen;
      } else {
        // Write literal: 0-bit flag + 8-bit byte.
        writer.WriteBit(0);
        WriteBits(writer, data[pos], 8);
        pos++;
      }
    }

    // End marker: match flag + zero offset.
    writer.WriteBit(1);
    writer.WriteBit(1);
    WriteBits(writer, 0, 7);

    writer.Flush();
    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var src = data[4..].ToArray();
    var bitIndex = 0;
    var result = new byte[originalSize];
    var pos = 0;

    while (pos < originalSize) {
      var flag = ReadBit(src, ref bitIndex);

      if (flag == 0) {
        // Literal byte.
        result[pos++] = (byte)ReadBits(src, ref bitIndex, 8);
      } else {
        // Match: read offset.
        int offset;
        if (ReadBit(src, ref bitIndex) == 1) {
          // 7-bit short offset.
          offset = ReadBits(src, ref bitIndex, 7);
        } else {
          // 11-bit long offset.
          offset = ReadBits(src, ref bitIndex, 11);
        }

        if (offset == 0)
          break; // End marker.

        // Read length.
        var lenCode = ReadBits(src, ref bitIndex, 2);
        int length;
        if (lenCode < 3) {
          length = lenCode + 2;
        } else {
          // Extended length.
          length = 5;
          int nibble;
          do {
            nibble = ReadBits(src, ref bitIndex, 4);
            length += nibble;
          } while (nibble == 15);
        }

        // Copy match (byte by byte for overlapping support).
        var srcPos = pos - offset;
        for (var j = 0; j < length && pos < originalSize; j++)
          result[pos++] = result[srcPos + j];
      }
    }

    return result;
  }

  private static int HashAt(ReadOnlySpan<byte> data, int pos)
    => ((data[pos] << 6) ^ data[pos + 1]) & 0x3FFF;

  private static void WriteBits(BitWriter writer, int value, int count) {
    for (var i = count - 1; i >= 0; i--)
      writer.WriteBit((value >> i) & 1);
  }

  private static int ReadBit(byte[] data, ref int bitIndex) {
    if (bitIndex / 8 >= data.Length)
      throw new InvalidDataException("Unexpected end of LZS bitstream.");
    var bit = (data[bitIndex / 8] >> (7 - (bitIndex % 8))) & 1;
    bitIndex++;
    return bit;
  }

  private static int ReadBits(byte[] data, ref int bitIndex, int count) {
    var value = 0;
    for (var i = 0; i < count; i++)
      value = (value << 1) | ReadBit(data, ref bitIndex);
    return value;
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
