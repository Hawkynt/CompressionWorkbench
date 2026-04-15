using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Ibm842;

/// <summary>
/// Exposes IBM 842 hardware compression as a benchmarkable building block.
/// A fixed-dictionary LZ scheme that encodes data using templates of 2, 4, and 8-byte
/// patterns. Each group of 8 bytes is encoded as a template index followed by a mix of
/// literals and references to a recent-history dictionary.
/// </summary>
public sealed class Ibm842BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_842";
  /// <inheritdoc/>
  public string DisplayName => "842";
  /// <inheritdoc/>
  public string Description => "IBM 842 hardware compression with 2/4/8-byte template matching";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  // Dictionary sizes for each entry width.
  private const int Dict2Size = 256;
  private const int Dict4Size = 256;
  private const int Dict8Size = 256;

  // Template opcodes (5-bit).
  private const byte OpD8 = 0x00;         // 8-byte dict ref
  private const byte OpD4D4 = 0x01;       // two 4-byte dict refs
  private const byte OpD4D2D2 = 0x02;     // 4-byte + two 2-byte dict refs
  private const byte OpD2D2D4 = 0x03;     // two 2-byte + 4-byte dict refs
  private const byte OpD2D2D2D2 = 0x04;   // four 2-byte dict refs
  private const byte OpD4L4 = 0x05;       // 4-byte dict ref + 4 literal bytes
  private const byte OpL4D4 = 0x06;       // 4 literal bytes + 4-byte dict ref
  private const byte OpL8 = 0x07;         // 8 literal bytes
  private const byte OpEnd = 0x1F;        // end of stream

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write 4-byte LE uncompressed size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    // Dictionaries: circular buffers mapping content → index, with reverse maps
    // to invalidate stale entries when indices wrap around.
    var dict2 = new Dictionary<ushort, byte>();
    var dict4 = new Dictionary<uint, byte>();
    var dict8 = new Dictionary<ulong, byte>();
    var rev2 = new ushort?[Dict2Size];
    var rev4 = new uint?[Dict4Size];
    var rev8 = new ulong?[Dict8Size];
    var dict2Next = (byte)0;
    var dict4Next = (byte)0;
    var dict8Next = (byte)0;

    var writer = new BitWriter(ms);
    var pos = 0;

    while (pos < data.Length) {
      var remaining = data.Length - pos;

      if (remaining >= 8) {
        // Try 8-byte group encoding.
        var chunk = data.Slice(pos, 8);
        var v8 = BinaryPrimitives.ReadUInt64BigEndian(chunk);
        var v4a = BinaryPrimitives.ReadUInt32BigEndian(chunk);
        var v4b = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
        var v2a = BinaryPrimitives.ReadUInt16BigEndian(chunk);
        var v2b = BinaryPrimitives.ReadUInt16BigEndian(chunk[2..]);
        var v2c = BinaryPrimitives.ReadUInt16BigEndian(chunk[4..]);
        var v2d = BinaryPrimitives.ReadUInt16BigEndian(chunk[6..]);

        if (dict8.TryGetValue(v8, out var idx8)) {
          WriteBits(writer, OpD8, 5);
          writer.WriteByte(idx8);
        } else if (dict4.TryGetValue(v4a, out var i4a) && dict4.TryGetValue(v4b, out var i4b)) {
          WriteBits(writer, OpD4D4, 5);
          writer.WriteByte(i4a);
          writer.WriteByte(i4b);
        } else if (dict4.TryGetValue(v4a, out var i4a2) && dict2.TryGetValue(v2c, out var i2c) && dict2.TryGetValue(v2d, out var i2d)) {
          WriteBits(writer, OpD4D2D2, 5);
          writer.WriteByte(i4a2);
          writer.WriteByte(i2c);
          writer.WriteByte(i2d);
        } else if (dict2.TryGetValue(v2a, out var i2a) && dict2.TryGetValue(v2b, out var i2b2) && dict4.TryGetValue(v4b, out var i4b2)) {
          WriteBits(writer, OpD2D2D4, 5);
          writer.WriteByte(i2a);
          writer.WriteByte(i2b2);
          writer.WriteByte(i4b2);
        } else if (dict2.TryGetValue(v2a, out var i2a2) && dict2.TryGetValue(v2b, out var i2b3) && dict2.TryGetValue(v2c, out var i2c2) && dict2.TryGetValue(v2d, out var i2d2)) {
          WriteBits(writer, OpD2D2D2D2, 5);
          writer.WriteByte(i2a2);
          writer.WriteByte(i2b3);
          writer.WriteByte(i2c2);
          writer.WriteByte(i2d2);
        } else if (dict4.TryGetValue(v4a, out var i4a3)) {
          WriteBits(writer, OpD4L4, 5);
          writer.WriteByte(i4a3);
          for (var j = 4; j < 8; j++)
            writer.WriteByte(chunk[j]);
        } else if (dict4.TryGetValue(v4b, out var i4b3)) {
          WriteBits(writer, OpL4D4, 5);
          for (var j = 0; j < 4; j++)
            writer.WriteByte(chunk[j]);
          writer.WriteByte(i4b3);
        } else {
          WriteBits(writer, OpL8, 5);
          for (var j = 0; j < 8; j++)
            writer.WriteByte(chunk[j]);
        }

        // Update dictionaries (invalidate stale entries on wrap).
        if (rev8[dict8Next].HasValue) dict8.Remove(rev8[dict8Next]!.Value);
        rev8[dict8Next] = v8; dict8[v8] = dict8Next++;

        if (rev4[dict4Next].HasValue) dict4.Remove(rev4[dict4Next]!.Value);
        rev4[dict4Next] = v4a; dict4[v4a] = dict4Next; dict4Next++;
        if (rev4[dict4Next].HasValue) dict4.Remove(rev4[dict4Next]!.Value);
        rev4[dict4Next] = v4b; dict4[v4b] = dict4Next; dict4Next++;

        if (rev2[dict2Next].HasValue) dict2.Remove(rev2[dict2Next]!.Value);
        rev2[dict2Next] = v2a; dict2[v2a] = dict2Next; dict2Next++;
        if (rev2[dict2Next].HasValue) dict2.Remove(rev2[dict2Next]!.Value);
        rev2[dict2Next] = v2b; dict2[v2b] = dict2Next; dict2Next++;
        if (rev2[dict2Next].HasValue) dict2.Remove(rev2[dict2Next]!.Value);
        rev2[dict2Next] = v2c; dict2[v2c] = dict2Next; dict2Next++;
        if (rev2[dict2Next].HasValue) dict2.Remove(rev2[dict2Next]!.Value);
        rev2[dict2Next] = v2d; dict2[v2d] = dict2Next; dict2Next++;

        pos += 8;
      } else {
        // Remaining bytes < 8: emit as literal with padding.
        WriteBits(writer, OpL8, 5);
        for (var j = 0; j < 8; j++)
          writer.WriteByte(j < remaining ? data[pos + j] : (byte)0);
        pos += remaining;
      }
    }

    // End marker.
    WriteBits(writer, OpEnd, 5);
    writer.Flush();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var src = data[4..].ToArray();
    var reader = new BitReader(src);
    var result = new List<byte>(originalSize);

    // Reverse dictionaries: index → content.
    var dict2 = new ushort[Dict2Size];
    var dict4 = new uint[Dict4Size];
    var dict8 = new ulong[Dict8Size];
    var dict2Next = (byte)0;
    var dict4Next = (byte)0;
    var dict8Next = (byte)0;

    Span<byte> chunk = stackalloc byte[8];
    while (result.Count < originalSize) {
      var op = reader.ReadBits(5);

      if (op == OpEnd)
        break;

      chunk.Clear();

      switch (op) {
        case OpD8: {
          var idx = reader.ReadByte();
          BinaryPrimitives.WriteUInt64BigEndian(chunk, dict8[idx]);
          break;
        }
        case OpD4D4: {
          var i4a = reader.ReadByte();
          var i4b = reader.ReadByte();
          BinaryPrimitives.WriteUInt32BigEndian(chunk, dict4[i4a]);
          BinaryPrimitives.WriteUInt32BigEndian(chunk[4..], dict4[i4b]);
          break;
        }
        case OpD4D2D2: {
          var i4 = reader.ReadByte();
          var i2c = reader.ReadByte();
          var i2d = reader.ReadByte();
          BinaryPrimitives.WriteUInt32BigEndian(chunk, dict4[i4]);
          BinaryPrimitives.WriteUInt16BigEndian(chunk[4..], dict2[i2c]);
          BinaryPrimitives.WriteUInt16BigEndian(chunk[6..], dict2[i2d]);
          break;
        }
        case OpD2D2D4: {
          var i2a = reader.ReadByte();
          var i2b = reader.ReadByte();
          var i4 = reader.ReadByte();
          BinaryPrimitives.WriteUInt16BigEndian(chunk, dict2[i2a]);
          BinaryPrimitives.WriteUInt16BigEndian(chunk[2..], dict2[i2b]);
          BinaryPrimitives.WriteUInt32BigEndian(chunk[4..], dict4[i4]);
          break;
        }
        case OpD2D2D2D2: {
          var i2a = reader.ReadByte();
          var i2b = reader.ReadByte();
          var i2c = reader.ReadByte();
          var i2d = reader.ReadByte();
          BinaryPrimitives.WriteUInt16BigEndian(chunk, dict2[i2a]);
          BinaryPrimitives.WriteUInt16BigEndian(chunk[2..], dict2[i2b]);
          BinaryPrimitives.WriteUInt16BigEndian(chunk[4..], dict2[i2c]);
          BinaryPrimitives.WriteUInt16BigEndian(chunk[6..], dict2[i2d]);
          break;
        }
        case OpD4L4: {
          var i4 = reader.ReadByte();
          BinaryPrimitives.WriteUInt32BigEndian(chunk, dict4[i4]);
          for (var j = 4; j < 8; j++)
            chunk[j] = reader.ReadByte();
          break;
        }
        case OpL4D4: {
          for (var j = 0; j < 4; j++)
            chunk[j] = reader.ReadByte();
          var i4 = reader.ReadByte();
          BinaryPrimitives.WriteUInt32BigEndian(chunk[4..], dict4[i4]);
          break;
        }
        case OpL8: {
          for (var j = 0; j < 8; j++)
            chunk[j] = reader.ReadByte();
          break;
        }
        default:
          throw new InvalidDataException($"Unknown 842 opcode: 0x{op:X2}");
      }

      // Update dictionaries from decoded chunk.
      var v8 = BinaryPrimitives.ReadUInt64BigEndian(chunk);
      var v4a = BinaryPrimitives.ReadUInt32BigEndian(chunk);
      var v4b = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
      var v2a = BinaryPrimitives.ReadUInt16BigEndian(chunk);
      var v2b = BinaryPrimitives.ReadUInt16BigEndian(chunk[2..]);
      var v2c = BinaryPrimitives.ReadUInt16BigEndian(chunk[4..]);
      var v2d = BinaryPrimitives.ReadUInt16BigEndian(chunk[6..]);

      dict8[dict8Next++] = v8;
      dict4[dict4Next++] = v4a;
      dict4[dict4Next++] = v4b;
      dict2[dict2Next++] = v2a;
      dict2[dict2Next++] = v2b;
      dict2[dict2Next++] = v2c;
      dict2[dict2Next++] = v2d;

      // Append to result (only up to originalSize).
      var toAdd = Math.Min(8, originalSize - result.Count);
      for (var j = 0; j < toAdd; j++)
        result.Add(chunk[j]);
    }

    return [.. result];
  }

  private static void WriteBits(BitWriter writer, int value, int count) {
    for (var i = count - 1; i >= 0; i--)
      writer.WriteBit((value >> i) & 1);
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

    public void WriteByte(byte value) {
      // Fast path when aligned.
      if (_bitCount == 0) {
        output.WriteByte(value);
        return;
      }
      for (var i = 7; i >= 0; i--)
        WriteBit((value >> i) & 1);
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

  private sealed class BitReader(byte[] data) {
    private int _bitIndex;

    public int ReadBits(int count) {
      var value = 0;
      for (var i = 0; i < count; i++)
        value = (value << 1) | ReadBit();
      return value;
    }

    public byte ReadByte() => (byte)ReadBits(8);

    private int ReadBit() {
      if (_bitIndex / 8 >= data.Length)
        throw new InvalidDataException("Unexpected end of 842 bitstream.");
      var bit = (data[_bitIndex / 8] >> (7 - (_bitIndex % 8))) & 1;
      _bitIndex++;
      return bit;
    }
  }
}
