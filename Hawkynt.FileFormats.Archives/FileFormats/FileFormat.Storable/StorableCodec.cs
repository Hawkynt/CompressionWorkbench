#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Storable;

internal static class StorableCodec {
  private const byte SxObject = 0;
  private const byte SxLongScalar = 1;
  private const byte SxArray = 2;
  private const byte SxHash = 3;
  private const byte SxRef = 4;
  private const byte SxUndef = 5;
  private const byte SxByte = 8;
  private const byte SxNetInt = 9;
  private const byte SxScalar = 10;
  private const byte SxImmortalUndef = 14;
  private const byte SxImmortalYes = 15;
  private const byte SxImmortalNo = 16;
  private const byte SxUtf8String = 23;
  private const byte SxLongUtf8String = 24;

  public static StructuredNode Read(Stream stream) {
    var data = StructuredArchive.ReadAll(stream);
    var reader = new Reader(data);
    return reader.ReadRoot();
  }

  public static void Write(Stream output, StructuredNode root) {
    output.Write("pst0"u8);
    output.Write([0x05, 0x0b]);
    WriteNode(output, root, isRoot: true);
  }

  private static void WriteNode(Stream output, StructuredNode node, bool isRoot = false) {
    if (!isRoot && node.Kind is StructuredNodeKind.Object or StructuredNodeKind.Array)
      output.WriteByte(SxRef);

    switch (node.Kind) {
      case StructuredNodeKind.Object:
        output.WriteByte(SxHash);
        WriteUInt32(output, checked((uint)node.Members.Count));
        foreach (var member in node.Members) {
          WriteNode(output, member.Value);
          var key = Encoding.UTF8.GetBytes(member.Key);
          WriteUInt32(output, checked((uint)key.Length));
          output.Write(key);
        }
        break;
      case StructuredNodeKind.Array:
        output.WriteByte(SxArray);
        WriteUInt32(output, checked((uint)node.Items.Count));
        foreach (var item in node.Items) WriteNode(output, item);
        break;
      case StructuredNodeKind.Null:
        output.WriteByte(SxUndef);
        break;
      case StructuredNodeKind.Boolean:
        output.WriteByte(node.Data.AsSpan().SequenceEqual("true"u8) ? SxImmortalYes : SxImmortalNo);
        break;
      default:
        WriteScalar(output, node.Data);
        break;
    }
  }

  private static void WriteScalar(Stream output, ReadOnlySpan<byte> data) {
    if (data.Length <= byte.MaxValue) {
      output.WriteByte(SxScalar);
      output.WriteByte((byte)data.Length);
    } else {
      output.WriteByte(SxLongScalar);
      WriteUInt32(output, checked((uint)data.Length));
    }
    output.Write(data);
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    output.Write(bytes);
  }

  private sealed class Reader(byte[] data) {
    private readonly byte[] _data = data;
    private int _position;
    private int _nodes;

    public StructuredNode ReadRoot() {
      if (this._data.AsSpan().StartsWith("pst0"u8)) this._position = 4;
      if (ReadByte() != 0x05) throw new InvalidDataException("Only portable/network-order Perl Storable streams are supported.");
      _ = ReadByte();
      var result = ReadNode(0);
      if (this._position != this._data.Length) throw new InvalidDataException("Storable stream contains trailing data.");
      return result;
    }

    private StructuredNode ReadNode(int depth) {
      if (depth > 256) throw new InvalidDataException("Storable nesting exceeds the 256-level safety limit.");
      if (++this._nodes > 1_000_000) throw new InvalidDataException("Storable graph exceeds the one-million-node safety limit.");
      var type = ReadByte();
      return type switch {
        SxObject => StructuredNode.Text(StructuredNodeKind.Reference, $"@{ReadUInt32()}", "storable-reference"),
        SxLongScalar => StructuredNode.Binary(ReadBytes(ReadLength32()), "scalar"),
        SxArray => ReadArray(depth + 1),
        SxHash => ReadHash(depth + 1),
        SxRef => ReadNode(depth + 1),
        SxUndef or SxImmortalUndef => StructuredNode.Null("undef"),
        SxByte => StructuredNode.Text(StructuredNodeKind.Number, (ReadByte() - 128).ToString(CultureInfo.InvariantCulture), "integer"),
        SxNetInt => StructuredNode.Text(StructuredNodeKind.Number, ReadInt32().ToString(CultureInfo.InvariantCulture), "integer"),
        SxScalar => StructuredNode.Binary(ReadBytes(ReadByte()), "scalar"),
        SxImmortalYes => StructuredNode.Text(StructuredNodeKind.Boolean, "true", "boolean"),
        SxImmortalNo => StructuredNode.Text(StructuredNodeKind.Boolean, "false", "boolean"),
        SxUtf8String => ReadUtf8(ReadByte(), "utf8"),
        SxLongUtf8String => ReadUtf8(ReadLength32(), "utf8"),
        _ => throw new InvalidDataException($"Unsupported or executable Perl Storable type tag {type}; blessed/tied/hook/code values are intentionally not instantiated."),
      };
    }

    private StructuredNode ReadArray(int depth) {
      var count = ReadCount();
      var result = StructuredNode.Array("array");
      for (var i = 0; i < count; ++i) result.Items.Add(ReadNode(depth));
      return result;
    }

    private StructuredNode ReadHash(int depth) {
      var count = ReadCount();
      var result = StructuredNode.Object("hash");
      for (var i = 0; i < count; ++i) {
        var value = ReadNode(depth);
        var key = StructuredArchive.DecodeUtf8OrLatin1(ReadBytes(ReadLength32()));
        result.Add(key, value);
      }
      return result;
    }

    private StructuredNode ReadUtf8(int length, string type) {
      try { return StructuredNode.Text(StructuredNodeKind.String, new UTF8Encoding(false, true).GetString(ReadBytes(length)), type); }
      catch (DecoderFallbackException ex) { throw new InvalidDataException("Storable UTF-8 scalar is malformed.", ex); }
    }

    private int ReadCount() {
      var value = ReadUInt32();
      if (value > 1_000_000) throw new InvalidDataException("Storable container exceeds the one-million-item safety limit.");
      return (int)value;
    }

    private int ReadLength32() {
      var value = ReadUInt32();
      if (value > int.MaxValue) throw new InvalidDataException("Storable scalar is too large for this process.");
      return (int)value;
    }

    private byte ReadByte() {
      if ((uint)this._position >= (uint)this._data.Length) throw new EndOfStreamException("Truncated Storable stream.");
      return this._data[this._position++];
    }

    private ReadOnlySpan<byte> ReadBytes(int length) {
      if (length < 0 || length > this._data.Length - this._position) throw new EndOfStreamException("Truncated Storable payload.");
      var result = this._data.AsSpan(this._position, length);
      this._position += length;
      return result;
    }

    private uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));
    private int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));
  }
}
