#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Parquet;

/// <summary>
/// Minimal Thrift compact protocol decoder/encoder for walking the Parquet FileMetaData footer.
/// Implements only the subset needed: zig-zag varints, binary/string, field headers, list headers,
/// and recursive skip for unknown values. Not a full Thrift implementation.
/// </summary>
public static class ThriftCompact {

  /// <summary>Reads a zig-zag varint as a 64-bit signed long.</summary>
  public static long ReadVarLong(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    ulong result = 0;
    var shift = 0;
    while (true) {
      var b = stream.ReadByte();
      if (b < 0) throw new EndOfStreamException("Unexpected EOF reading Thrift varint.");
      result |= (ulong)(b & 0x7F) << shift;
      if ((b & 0x80) == 0) break;
      shift += 7;
      if (shift > 63) throw new InvalidDataException("Thrift varint exceeds 64 bits.");
    }
    return (long)(result >> 1) ^ -(long)(result & 1);
  }

  /// <summary>Reads a zig-zag varint and casts to int. Throws if outside int range.</summary>
  public static int ReadVarInt(Stream stream) {
    var v = ReadVarLong(stream);
    if (v < int.MinValue || v > int.MaxValue) throw new InvalidDataException("Thrift varint out of int range.");
    return (int)v;
  }

  /// <summary>Writes a zig-zag varint long.</summary>
  public static void WriteVarLong(Stream stream, long value) {
    ArgumentNullException.ThrowIfNull(stream);
    var encoded = (ulong)((value << 1) ^ (value >> 63));
    while ((encoded & ~0x7FUL) != 0) {
      stream.WriteByte((byte)((encoded & 0x7F) | 0x80));
      encoded >>= 7;
    }
    stream.WriteByte((byte)encoded);
  }

  /// <summary>Writes a zig-zag varint int.</summary>
  public static void WriteVarInt(Stream stream, int value) => WriteVarLong(stream, value);

  /// <summary>Reads a length-prefixed binary blob and decodes as UTF-8.</summary>
  public static string ReadBinary(Stream stream) {
    var bytes = ReadBinaryBytes(stream);
    return Encoding.UTF8.GetString(bytes);
  }

  /// <summary>Reads a length-prefixed binary blob as raw bytes.</summary>
  public static byte[] ReadBinaryBytes(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var len = ReadVarLong(stream);
    if (len < 0) throw new InvalidDataException("Negative binary length in Thrift stream.");
    if (len == 0) return [];
    var buf = new byte[len];
    var read = 0;
    while (read < len) {
      var n = stream.Read(buf, read, (int)(len - read));
      if (n <= 0) throw new EndOfStreamException("Unexpected EOF reading Thrift binary payload.");
      read += n;
    }
    return buf;
  }

  /// <summary>Writes a length-prefixed binary blob from UTF-8 string.</summary>
  public static void WriteBinary(Stream stream, string value) {
    ArgumentNullException.ThrowIfNull(value);
    WriteBinaryBytes(stream, Encoding.UTF8.GetBytes(value));
  }

  /// <summary>Writes a length-prefixed binary blob.</summary>
  public static void WriteBinaryBytes(Stream stream, byte[] bytes) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(bytes);
    WriteVarLong(stream, bytes.Length);
    stream.Write(bytes, 0, bytes.Length);
  }

  /// <summary>
  /// Reads one field header byte. Returns the field type (low 4 bits). On stop (0) returns 0
  /// without touching <paramref name="prevFieldId"/>. Otherwise, when the high-nibble delta is
  /// non-zero, advances <paramref name="prevFieldId"/> by that delta. When the delta nibble is 0,
  /// reads a zig-zag varint i16 holding the absolute field id.
  /// </summary>
  public static byte ReadFieldHeader(Stream stream, ref int prevFieldId) {
    ArgumentNullException.ThrowIfNull(stream);
    var b = stream.ReadByte();
    if (b < 0) throw new EndOfStreamException("Unexpected EOF reading Thrift field header.");
    if (b == 0) return ParquetConstants.TypeStop;

    var type = (byte)(b & 0x0F);
    var delta = (b >> 4) & 0x0F;
    if (delta != 0)
      prevFieldId += delta;
    else
      prevFieldId = (int)ReadVarLong(stream);

    return type;
  }

  /// <summary>
  /// Writes a field header. If the delta from <paramref name="prevFieldId"/> to
  /// <paramref name="fieldId"/> fits in 1..15, packs it in the high nibble; otherwise emits a
  /// zero-delta header followed by a zig-zag varint with the absolute id.
  /// </summary>
  public static void WriteFieldHeader(Stream stream, byte type, int fieldId, ref int prevFieldId) {
    ArgumentNullException.ThrowIfNull(stream);
    var delta = fieldId - prevFieldId;
    if (delta is >= 1 and <= 15) {
      stream.WriteByte((byte)((delta << 4) | (type & 0x0F)));
    } else {
      stream.WriteByte((byte)(type & 0x0F));
      WriteVarLong(stream, fieldId);
    }
    prevFieldId = fieldId;
  }

  /// <summary>Writes the struct-stop byte.</summary>
  public static void WriteStop(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.WriteByte(0);
  }

  /// <summary>
  /// Reads a list/set header. Returns (size, elementType). Size is in the high nibble (0..14)
  /// or, when the high nibble is 15, in a following zig-zag varint i32.
  /// </summary>
  public static (int Size, byte ElementType) ReadListHeader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var b = stream.ReadByte();
    if (b < 0) throw new EndOfStreamException("Unexpected EOF reading Thrift list header.");
    var elementType = (byte)(b & 0x0F);
    var sizeNibble = (b >> 4) & 0x0F;
    var size = sizeNibble == 0x0F ? ReadVarInt(stream) : sizeNibble;
    if (size < 0) throw new InvalidDataException("Negative Thrift list size.");
    return (size, elementType);
  }

  /// <summary>Writes a list/set header.</summary>
  public static void WriteListHeader(Stream stream, int size, byte elementType) {
    ArgumentNullException.ThrowIfNull(stream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
    if (size <= 14) {
      stream.WriteByte((byte)((size << 4) | (elementType & 0x0F)));
    } else {
      stream.WriteByte((byte)(0xF0 | (elementType & 0x0F)));
      WriteVarInt(stream, size);
    }
  }

  /// <summary>
  /// Skips the value of a field with the given compact-protocol type code. For container types
  /// (struct, list, set, map) this walks recursively until the contained values are consumed.
  /// </summary>
  public static void Skip(Stream stream, byte type) {
    ArgumentNullException.ThrowIfNull(stream);
    switch (type) {
      case ParquetConstants.TypeStop:
      case ParquetConstants.TypeBoolTrue:
      case ParquetConstants.TypeBoolFalse:
        return;
      case ParquetConstants.TypeByte: {
        var b = stream.ReadByte();
        if (b < 0) throw new EndOfStreamException("EOF skipping Thrift byte.");
        return;
      }
      case ParquetConstants.TypeI16:
      case ParquetConstants.TypeI32:
      case ParquetConstants.TypeI64:
        _ = ReadVarLong(stream);
        return;
      case ParquetConstants.TypeDouble: {
        Span<byte> tmp = stackalloc byte[8];
        var read = 0;
        while (read < 8) {
          var n = stream.Read(tmp[read..]);
          if (n <= 0) throw new EndOfStreamException("EOF skipping Thrift double.");
          read += n;
        }
        return;
      }
      case ParquetConstants.TypeBinary:
        _ = ReadBinaryBytes(stream);
        return;
      case ParquetConstants.TypeList:
      case ParquetConstants.TypeSet: {
        var (size, elemType) = ReadListHeader(stream);
        for (var i = 0; i < size; i++) Skip(stream, elemType);
        return;
      }
      case ParquetConstants.TypeMap: {
        var size = ReadVarInt(stream);
        if (size < 0) throw new InvalidDataException("Negative Thrift map size.");
        if (size == 0) return;
        var kv = stream.ReadByte();
        if (kv < 0) throw new EndOfStreamException("EOF skipping Thrift map kv types.");
        var keyType = (byte)((kv >> 4) & 0x0F);
        var valueType = (byte)(kv & 0x0F);
        for (var i = 0; i < size; i++) {
          Skip(stream, keyType);
          Skip(stream, valueType);
        }
        return;
      }
      case ParquetConstants.TypeStruct:
        SkipStruct(stream);
        return;
      default:
        throw new InvalidDataException($"Unknown Thrift compact type code 0x{type:X2}.");
    }
  }

  /// <summary>Reads field headers until the struct-stop byte, skipping each field's value.</summary>
  public static void SkipStruct(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var prevId = 0;
    while (true) {
      var t = ReadFieldHeader(stream, ref prevId);
      if (t == ParquetConstants.TypeStop) return;
      Skip(stream, t);
    }
  }
}
