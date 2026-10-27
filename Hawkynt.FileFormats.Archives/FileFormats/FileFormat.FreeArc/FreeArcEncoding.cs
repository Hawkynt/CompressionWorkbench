using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FileFormat.FreeArc;

/// <summary>
/// The primitive encodings of the FreeArc container: the variable-length integer, the C string
/// and the block-type numbers. See <c>docs/FREEARC-ON-DISK.md</c>.
/// </summary>
internal static class FreeArcEncoding {
  /// <summary>"ArC\x01" read as a little-endian 32-bit value; opens the archive and every block descriptor.</summary>
  public const uint Signature = 0x01437241;

  /// <summary>The version word FreeArc 0.67 writes after the signature in the header block.</summary>
  public const uint ArchiveVersion = 0x07060000;

  /// <summary>How far from the end of the archive the footer descriptor may start.</summary>
  public const int FooterScanWindow = 4096;

  public const int BlockHeader = 1;
  public const int BlockData = 2;
  public const int BlockDirectory = 3;
  public const int BlockFooter = 4;
  public const int BlockRecovery = 5;

  /// <summary>Reads one variable-length integer: the count of low one-bits in the first byte tells how many more bytes follow.</summary>
  public static ulong ReadVarInt(ReadOnlySpan<byte> buffer, ref int position) {
    if (position >= buffer.Length)
      throw new InvalidDataException("FreeArc: integer runs past the end of the block.");

    var first = buffer[position];
    var extra = BitOperations.TrailingZeroCount(~(uint)first);
    if (extra >= 8) {
      // A first byte of all ones announces a plain 64-bit value in the next eight bytes.
      if (position + 9 > buffer.Length)
        throw new InvalidDataException("FreeArc: integer runs past the end of the block.");
      var wide = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(position + 1, 8));
      position += 9;
      return wide;
    }

    var length = extra + 1;
    if (position + length > buffer.Length)
      throw new InvalidDataException("FreeArc: integer runs past the end of the block.");

    ulong value = 0;
    for (var i = length - 1; i >= 0; --i)
      value = (value << 8) | buffer[position + i];
    position += length;
    return value >> length;
  }

  /// <summary>Reads a variable-length integer that must fit a non-negative <see cref="int"/>.</summary>
  public static int ReadCount(ReadOnlySpan<byte> buffer, ref int position) {
    var value = ReadVarInt(buffer, ref position);
    if (value > int.MaxValue)
      throw new InvalidDataException("FreeArc: count out of range.");
    return (int)value;
  }

  /// <summary>Reads a variable-length integer that must fit a non-negative <see cref="long"/>.</summary>
  public static long ReadSize(ReadOnlySpan<byte> buffer, ref int position) {
    var value = ReadVarInt(buffer, ref position);
    if (value > long.MaxValue)
      throw new InvalidDataException("FreeArc: size out of range.");
    return (long)value;
  }

  /// <summary>Writes one variable-length integer in the shortest form.</summary>
  public static void WriteVarInt(Stream output, ulong value) {
    Span<byte> buffer = stackalloc byte[9];
    for (var length = 1; length <= 8; ++length) {
      if (value >= 1UL << (7 * length))
        continue;

      var encoded = (value << length) | ((1UL << (length - 1)) - 1);
      for (var i = 0; i < length; ++i)
        buffer[i] = (byte)(encoded >> (8 * i));
      output.Write(buffer[..length]);
      return;
    }

    buffer[0] = 0xFF;
    BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], value);
    output.Write(buffer);
  }

  /// <summary>Reads a NUL-terminated UTF-8 string.</summary>
  public static string ReadString(ReadOnlySpan<byte> buffer, ref int position) {
    var rest = buffer[Math.Min(position, buffer.Length)..];
    var end = rest.IndexOf((byte)0);
    if (end < 0)
      throw new InvalidDataException("FreeArc: unterminated string.");
    var text = Encoding.UTF8.GetString(rest[..end]);
    position += end + 1;
    return text;
  }

  /// <summary>Writes a NUL-terminated UTF-8 string.</summary>
  public static void WriteString(Stream output, string text) {
    output.Write(Encoding.UTF8.GetBytes(text));
    output.WriteByte(0);
  }

  /// <summary>Reads a little-endian 32-bit value.</summary>
  public static uint ReadUInt32(ReadOnlySpan<byte> buffer, ref int position) {
    if (position + 4 > buffer.Length)
      throw new InvalidDataException("FreeArc: field runs past the end of the block.");
    var value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(position, 4));
    position += 4;
    return value;
  }

  /// <summary>Writes a little-endian 32-bit value.</summary>
  public static void WriteUInt32(Stream output, uint value) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
    output.Write(buffer);
  }
}
