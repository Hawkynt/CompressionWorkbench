#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.MessagePack;

internal static partial class MessagePackCodec {
  public static StructuredNode Read(Stream stream) {
    var data = StructuredArchive.ReadAll(stream);
    var reader = new Reader(data);
    var result = reader.ReadNode(0);
    if (!reader.AtEnd) throw new InvalidDataException("MessagePack document contains trailing bytes after the root value.");
    return result;
  }

  private ref struct Reader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;
    public bool AtEnd => this._position == this._data.Length;

    public StructuredNode ReadNode(int depth) {
      if (depth > 256) throw new InvalidDataException("MessagePack nesting exceeds the 256-level safety limit.");
      var code = ReadByte();
      if (code <= 0x7f) return Number(code.ToString(CultureInfo.InvariantCulture), "positive-fixint");
      if (code >= 0xe0) return Number(unchecked((sbyte)code).ToString(CultureInfo.InvariantCulture), "negative-fixint");
      if ((code & 0xf0) == 0x80) return ReadMap(code & 0x0f, depth + 1, "fixmap");
      if ((code & 0xf0) == 0x90) return ReadArray(code & 0x0f, depth + 1, "fixarray");
      if ((code & 0xe0) == 0xa0) return ReadString(code & 0x1f, "fixstr");

      return code switch {
        0xc0 => StructuredNode.Null("nil"),
        0xc1 => throw new InvalidDataException("0xC1 is not a valid MessagePack format byte."),
        0xc2 => StructuredNode.Text(StructuredNodeKind.Boolean, "false", "bool"),
        0xc3 => StructuredNode.Text(StructuredNodeKind.Boolean, "true", "bool"),
        0xc4 => ReadBinary(ReadByte(), "bin8"),
        0xc5 => ReadBinary(ReadUInt16(), "bin16"),
        0xc6 => ReadBinary(ReadLength32(), "bin32"),
        0xc7 => ReadExtension(ReadByte(), "ext8"),
        0xc8 => ReadExtension(ReadUInt16(), "ext16"),
        0xc9 => ReadExtension(ReadLength32(), "ext32"),
        0xca => Number(BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32())).ToString("R", CultureInfo.InvariantCulture), "float32"),
        0xcb => Number(BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64())).ToString("R", CultureInfo.InvariantCulture), "float64"),
        0xcc => Number(ReadByte().ToString(CultureInfo.InvariantCulture), "uint8"),
        0xcd => Number(ReadUInt16().ToString(CultureInfo.InvariantCulture), "uint16"),
        0xce => Number(ReadUInt32().ToString(CultureInfo.InvariantCulture), "uint32"),
        0xcf => Number(ReadUInt64().ToString(CultureInfo.InvariantCulture), "uint64"),
        0xd0 => Number(unchecked((sbyte)ReadByte()).ToString(CultureInfo.InvariantCulture), "int8"),
        0xd1 => Number(ReadInt16().ToString(CultureInfo.InvariantCulture), "int16"),
        0xd2 => Number(ReadInt32().ToString(CultureInfo.InvariantCulture), "int32"),
        0xd3 => Number(ReadInt64().ToString(CultureInfo.InvariantCulture), "int64"),
        0xd4 => ReadExtension(1, "fixext1"),
        0xd5 => ReadExtension(2, "fixext2"),
        0xd6 => ReadExtension(4, "fixext4"),
        0xd7 => ReadExtension(8, "fixext8"),
        0xd8 => ReadExtension(16, "fixext16"),
        0xd9 => ReadString(ReadByte(), "str8"),
        0xda => ReadString(ReadUInt16(), "str16"),
        0xdb => ReadString(ReadLength32(), "str32"),
        0xdc => ReadArray(ReadUInt16(), depth + 1, "array16"),
        0xdd => ReadArray(ReadLength32(), depth + 1, "array32"),
        0xde => ReadMap(ReadUInt16(), depth + 1, "map16"),
        0xdf => ReadMap(ReadLength32(), depth + 1, "map32"),
        _ => throw new InvalidDataException($"Unknown MessagePack format byte 0x{code:X2}."),
      };
    }

    private StructuredNode ReadArray(int count, int depth, string typeName) {
      CheckCount(count);
      var result = StructuredNode.Array(typeName);
      for (var i = 0; i < count; ++i) result.Items.Add(ReadNode(depth));
      return result;
    }

    private StructuredNode ReadMap(int count, int depth, string typeName) {
      CheckCount(count);
      var result = StructuredNode.Object(typeName);
      for (var i = 0; i < count; ++i) {
        var key = ReadNode(depth);
        var value = ReadNode(depth);
        if (key.Kind == StructuredNodeKind.String) {
          result.Add(Encoding.UTF8.GetString(key.Data), value);
          continue;
        }
        var pair = StructuredNode.Object("map-entry");
        pair.Add("$key", key).Add("$value", value);
        result.Add($"entry-{i:D6}", pair);
      }
      return result;
    }

    private StructuredNode ReadString(int length, string typeName) {
      var bytes = ReadBytes(length);
      try { return StructuredNode.Text(StructuredNodeKind.String, new UTF8Encoding(false, true).GetString(bytes), typeName); }
      catch (DecoderFallbackException ex) { throw new InvalidDataException("MessagePack string contains invalid UTF-8.", ex); }
    }

    private StructuredNode ReadBinary(int length, string typeName) => StructuredNode.Binary(ReadBytes(length), typeName);

    private StructuredNode ReadExtension(int length, string formatName) {
      var type = unchecked((sbyte)ReadByte());
      return StructuredNode.Binary(ReadBytes(length), $"{formatName}:type={type}");
    }

    private byte ReadByte() {
      if ((uint)this._position >= (uint)this._data.Length) throw new EndOfStreamException("Truncated MessagePack document.");
      return this._data[this._position++];
    }

    private ReadOnlySpan<byte> ReadBytes(int length) {
      if (length < 0 || length > this._data.Length - this._position) throw new EndOfStreamException("Truncated MessagePack payload.");
      var result = this._data.Slice(this._position, length);
      this._position += length;
      return result;
    }

    private ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2));
    private short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(ReadBytes(2));
    private uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));
    private int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));
    private ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8));
    private long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(ReadBytes(8));

    private int ReadLength32() {
      var length = ReadUInt32();
      if (length > int.MaxValue) throw new InvalidDataException("MessagePack item is too large for this process.");
      return (int)length;
    }

    private static void CheckCount(int count) {
      if ((uint)count > 1_000_000u) throw new InvalidDataException("MessagePack container exceeds the one-million-item safety limit.");
    }

    private static StructuredNode Number(string text, string typeName)
      => StructuredNode.Text(StructuredNodeKind.Number, text, typeName);
  }
}
