#pragma warning disable CS1591
namespace FileFormat.Avro;

/// <summary>
/// Avro zig-zag variable-length long encoder/decoder.
/// Uses the canonical formula: encode = (n &lt;&lt; 1) ^ (n &gt;&gt; 63), decode = (n &gt;&gt; 1) ^ -(n &amp; 1).
/// </summary>
public static class AvroVarLong {

  public static long ReadLong(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    ulong result = 0;
    var shift = 0;
    while (true) {
      var b = stream.ReadByte();
      if (b < 0) throw new EndOfStreamException("Unexpected EOF reading Avro varint.");
      result |= (ulong)(b & 0x7F) << shift;
      if ((b & 0x80) == 0) break;
      shift += 7;
      if (shift > 63) throw new InvalidDataException("Avro varint exceeds 64 bits.");
    }
    return (long)(result >> 1) ^ -(long)(result & 1);
  }

  public static void WriteLong(Stream stream, long value) {
    ArgumentNullException.ThrowIfNull(stream);
    var encoded = (ulong)((value << 1) ^ (value >> 63));
    while ((encoded & ~0x7FUL) != 0) {
      stream.WriteByte((byte)((encoded & 0x7F) | 0x80));
      encoded >>= 7;
    }
    stream.WriteByte((byte)encoded);
  }
}
