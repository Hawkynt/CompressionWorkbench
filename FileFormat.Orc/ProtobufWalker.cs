#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Orc;

/// <summary>
/// Minimal Protobuf wire-format decoder/encoder used to walk the ORC PostScript and Footer.
/// Implements only the subset required: unsigned varints, length-delimited bytes/strings, tag
/// (field number + wire type) headers, and a tolerant <see cref="Skip"/> helper that walks
/// past values of any wire type. Not a full Protobuf implementation; no message reflection.
/// </summary>
public static class ProtobufWalker {

  /// <summary>Reads an unsigned Protobuf varint as a 64-bit value (NOT zig-zag).</summary>
  public static long ReadVarLong(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    ulong result = 0;
    var shift = 0;
    while (true) {
      var b = stream.ReadByte();
      if (b < 0) throw new EndOfStreamException("Unexpected EOF reading Protobuf varint.");
      result |= (ulong)(b & 0x7F) << shift;
      if ((b & 0x80) == 0) break;
      shift += 7;
      if (shift > 63) throw new InvalidDataException("Protobuf varint exceeds 64 bits.");
    }
    return (long)result;
  }

  /// <summary>Reads an unsigned Protobuf varint and casts to int. Throws if outside int range.</summary>
  public static int ReadVarInt(Stream stream) {
    var v = ReadVarLong(stream);
    if (v < 0 || v > int.MaxValue) throw new InvalidDataException("Protobuf varint out of int range.");
    return (int)v;
  }

  /// <summary>Writes an unsigned Protobuf varint (NOT zig-zag).</summary>
  public static void WriteVarLong(Stream stream, long value) {
    ArgumentNullException.ThrowIfNull(stream);
    var encoded = (ulong)value;
    while ((encoded & ~0x7FUL) != 0) {
      stream.WriteByte((byte)((encoded & 0x7F) | 0x80));
      encoded >>= 7;
    }
    stream.WriteByte((byte)encoded);
  }

  /// <summary>Writes an unsigned Protobuf varint int.</summary>
  public static void WriteVarInt(Stream stream, int value) => WriteVarLong(stream, value);

  /// <summary>Reads a Protobuf tag and decomposes it into (field number, wire type).</summary>
  public static (int FieldNumber, int WireType) ReadTag(Stream stream) {
    var raw = ReadVarLong(stream);
    var wire = (int)(raw & 0x7);
    var field = (int)((ulong)raw >> 3);
    return (field, wire);
  }

  /// <summary>Encodes (field number, wire type) into a tag varint.</summary>
  public static void WriteTag(Stream stream, int fieldNumber, int wireType) {
    if (fieldNumber < 1) throw new ArgumentOutOfRangeException(nameof(fieldNumber));
    if ((wireType & ~0x7) != 0) throw new ArgumentOutOfRangeException(nameof(wireType));
    var tag = ((long)fieldNumber << 3) | (long)(uint)wireType;
    WriteVarLong(stream, tag);
  }

  /// <summary>Reads a length-delimited byte array (length varint followed by raw bytes).</summary>
  public static byte[] ReadLengthDelimited(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var len = ReadVarLong(stream);
    if (len < 0) throw new InvalidDataException("Negative length in Protobuf length-delimited field.");
    if (len == 0) return [];
    var buf = new byte[len];
    var read = 0;
    while (read < len) {
      var n = stream.Read(buf, read, (int)(len - read));
      if (n <= 0) throw new EndOfStreamException("Unexpected EOF reading Protobuf length-delimited payload.");
      read += n;
    }
    return buf;
  }

  /// <summary>Reads a length-delimited UTF-8 string.</summary>
  public static string ReadString(Stream stream) => Encoding.UTF8.GetString(ReadLengthDelimited(stream));

  /// <summary>Writes a length-delimited byte array.</summary>
  public static void WriteLengthDelimited(Stream stream, byte[] data) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(data);
    WriteVarLong(stream, data.Length);
    stream.Write(data, 0, data.Length);
  }

  /// <summary>Writes a length-delimited UTF-8 string.</summary>
  public static void WriteString(Stream stream, string value) {
    ArgumentNullException.ThrowIfNull(value);
    WriteLengthDelimited(stream, Encoding.UTF8.GetBytes(value));
  }

  /// <summary>
  /// Skips a single Protobuf field value of the given wire type. Tolerates unknown wire types
  /// by raising <see cref="InvalidDataException"/>; callers using this for "skip unknown field"
  /// behavior should catch and stop walking gracefully.
  /// </summary>
  public static void Skip(Stream stream, int wireType) {
    ArgumentNullException.ThrowIfNull(stream);
    switch (wireType) {
      case OrcConstants.WireVarint:
        _ = ReadVarLong(stream);
        return;
      case OrcConstants.Wire64Bit: {
        Span<byte> tmp = stackalloc byte[8];
        var read = 0;
        while (read < 8) {
          var n = stream.Read(tmp[read..]);
          if (n <= 0) throw new EndOfStreamException("EOF skipping Protobuf 64-bit value.");
          read += n;
        }
        return;
      }
      case OrcConstants.WireLengthDelimited:
        _ = ReadLengthDelimited(stream);
        return;
      case OrcConstants.Wire32Bit: {
        Span<byte> tmp = stackalloc byte[4];
        var read = 0;
        while (read < 4) {
          var n = stream.Read(tmp[read..]);
          if (n <= 0) throw new EndOfStreamException("EOF skipping Protobuf 32-bit value.");
          read += n;
        }
        return;
      }
      default:
        throw new InvalidDataException($"Unknown Protobuf wire type {wireType}.");
    }
  }
}
