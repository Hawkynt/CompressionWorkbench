#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Nrbf;

internal sealed partial class NrbfReader {
  private StructuredNode ReadPrimitive(byte type) => type switch {
    1 => StructuredNode.Text(StructuredNodeKind.Boolean, ReadByte() == 0 ? "false" : "true", "boolean"),
    2 => Number(ReadByte(), "byte"),
    3 => StructuredNode.Text(StructuredNodeKind.String, new string((char)ReadUInt16(), 1), "char"),
    5 => StructuredNode.Text(StructuredNodeKind.Number, ReadString(), "decimal"),
    6 => StructuredNode.Text(StructuredNodeKind.Number, BitConverter.Int64BitsToDouble(ReadInt64()).ToString("R", CultureInfo.InvariantCulture), "double"),
    7 => Number(ReadInt16(), "int16"),
    8 => Number(ReadInt32(), "int32"),
    9 => Number(ReadInt64(), "int64"),
    10 => Number(unchecked((sbyte)ReadByte()), "sbyte"),
    11 => StructuredNode.Text(StructuredNodeKind.Number, BitConverter.Int32BitsToSingle(ReadInt32()).ToString("R", CultureInfo.InvariantCulture), "single"),
    12 => StructuredNode.Text(StructuredNodeKind.Number, ReadInt64().ToString(CultureInfo.InvariantCulture), "timespan-ticks"),
    13 => StructuredNode.Text(StructuredNodeKind.Number, $"0x{ReadUInt64():X16}", "datetime-data"),
    14 => Number(ReadUInt16(), "uint16"),
    15 => Number(ReadUInt32(), "uint32"),
    16 => Number(ReadUInt64(), "uint64"),
    17 => StructuredNode.Null("null"),
    18 => StructuredNode.Text(StructuredNodeKind.String, ReadString(), "string"),
    _ => throw new InvalidDataException($"Unknown NRBF PrimitiveTypeEnumeration value {type}."),
  };

  private static string PrimitiveName(byte type) => type switch {
    1 => "boolean", 2 => "byte", 3 => "char", 5 => "decimal", 6 => "double",
    7 => "int16", 8 => "int32", 9 => "int64", 10 => "sbyte", 11 => "single",
    12 => "timespan", 13 => "datetime", 14 => "uint16", 15 => "uint32", 16 => "uint64",
    17 => "null", 18 => "string", _ => $"primitive-{type}",
  };

  private static StructuredNode Number<T>(T value, string type) where T : IFormattable
    => StructuredNode.Text(StructuredNodeKind.Number, value.ToString(null, CultureInfo.InvariantCulture), type);

  private string ReadString() {
    var length = Read7BitEncodedInt();
    var bytes = ReadBytes(length);
    try { return new UTF8Encoding(false, true).GetString(bytes); }
    catch (DecoderFallbackException ex) { throw new InvalidDataException("NRBF LengthPrefixedString contains invalid UTF-8.", ex); }
  }

  private int Read7BitEncodedInt() {
    uint result = 0;
    for (var shift = 0; shift < 35; shift += 7) {
      var value = ReadByte();
      if (shift == 28 && (value & 0xf8) != 0) throw new InvalidDataException("NRBF string length overflows Int32.");
      result |= (uint)(value & 0x7f) << shift;
      if ((value & 0x80) == 0) return (int)result;
    }
    throw new InvalidDataException("Malformed NRBF 7-bit encoded string length.");
  }

  private int ReadCount(int max = 1_000_000) {
    var value = ReadInt32();
    if (value < 0 || value > max) throw new InvalidDataException($"NRBF count {value} is outside the supported safety range 0..{max}.");
    return value;
  }

  private byte PeekByte() {
    if ((uint)this._position >= (uint)this._data.Length) throw new EndOfStreamException("Truncated NRBF stream.");
    return this._data[this._position];
  }

  private byte ReadByte() {
    var value = PeekByte();
    ++this._position;
    return value;
  }

  private ReadOnlySpan<byte> ReadBytes(int length) {
    if (length < 0 || length > this._data.Length - this._position) throw new EndOfStreamException("Truncated NRBF payload.");
    var result = this._data.AsSpan(this._position, length);
    this._position += length;
    return result;
  }

  private short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(ReadBytes(2));
  private ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));
  private int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));
  private uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));
  private long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(8));
  private ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));
}
