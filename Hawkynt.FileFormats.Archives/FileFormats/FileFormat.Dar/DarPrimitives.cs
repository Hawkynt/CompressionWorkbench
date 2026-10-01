using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FileFormat.Dar;

/// <summary>
/// The small encodings every DAR structure is built from. Layouts are those of
/// <c>docs/DAR-ON-DISK.md</c>, measured on archives written by dar 2.7.13 and 2.8.6.
/// </summary>
internal static class DarPrimitives {

  /// <summary>The five bytes that open every escape mark in a tape-marked archive (section 6).</summary>
  public static ReadOnlySpan<byte> EscapePrefix => [0xAD, 0xFD, 0xEA, 0x77, 0x21];

  /// <summary>The five bytes that open a hole record inside sparse-encoded file data (section 8).</summary>
  public static ReadOnlySpan<byte> SparsePrefix => [0xAE, 0xFD, 0xEA, 0x77, 0x21];

  /// <summary>Escape-mark type: the preceding five bytes are data, not a mark.</summary>
  public const byte NotAMark = (byte)'X';

  /// <summary>Hole-record type inside sparse-encoded data; an infinint zero count follows.</summary>
  public const byte HoleMark = (byte)'F';

  /// <summary>Reads one infinint (section 2.1) from <paramref name="s"/> at <paramref name="pos"/>.</summary>
  /// <exception cref="InvalidDataException">Truncated, malformed, or wider than 64 bits.</exception>
  public static ulong ReadInfinint(ReadOnlySpan<byte> s, ref int pos) {
    if (!DarSliceHeader.TryReadInfinint(s, ref pos, out var value))
      throw new InvalidDataException($"Malformed or truncated DAR infinint at offset {pos}.");
    return value;
  }

  /// <summary>Writes <paramref name="value"/> as an infinint, in the shortest form dar itself writes.</summary>
  public static void WriteInfinint(Stream output, ulong value) {
    var bytes = value == 0 ? 1 : (64 - BitOperations.LeadingZeroCount(value) + 7) / 8;
    var groups = (bytes + 3) / 4;
    // groups g: (g-1)/8 zero bytes, then one byte with bit (7 - (g-1)%8) set.
    for (var i = 0; i < (groups - 1) / 8; ++i)
      output.WriteByte(0);
    output.WriteByte((byte)(0x80 >> ((groups - 1) % 8)));
    Span<byte> buffer = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
    output.Write(buffer[(8 - groups * 4)..]);
  }

  /// <summary>Reads a NUL-terminated UTF-8 string.</summary>
  public static string ReadString(ReadOnlySpan<byte> s, ref int pos) {
    var end = s[pos..].IndexOf((byte)0);
    if (end < 0)
      throw new InvalidDataException($"Unterminated DAR string at offset {pos}.");
    var text = Encoding.UTF8.GetString(s.Slice(pos, end));
    pos += end + 1;
    return text;
  }

  /// <summary>Writes a NUL-terminated UTF-8 string.</summary>
  public static void WriteString(Stream output, string text) {
    output.Write(Encoding.UTF8.GetBytes(text));
    output.WriteByte(0);
  }

  /// <summary>
  /// dar's "CRC" (section 2.3): every byte XOR-ed into a <paramref name="width"/>-byte ring,
  /// cycling from the first slot.
  /// </summary>
  public static byte[] Checksum(ReadOnlySpan<byte> data, int width) {
    var ring = new byte[width];
    for (var i = 0; i < data.Length; ++i)
      ring[i % width] ^= data[i];
    return ring;
  }

  /// <summary>The checksum width dar picks for file data of <paramref name="size"/> bytes: 1 when
  /// empty, else 4 bytes per started GiB.</summary>
  public static int DataChecksumWidth(ulong size) => size == 0 ? 1 : checked((int)(4 * ((size + (1UL << 30) - 1) >> 30)));

  /// <summary>Reads a stored checksum: an infinint width, then that many bytes.</summary>
  public static byte[] ReadChecksum(ReadOnlySpan<byte> s, ref int pos) {
    var width = ReadInfinint(s, ref pos);
    if (width == 0 || width > (ulong)(s.Length - pos))
      throw new InvalidDataException($"DAR checksum of width {width} at offset {pos} is empty or truncated.");
    var value = s.Slice(pos, (int)width).ToArray();
    pos += (int)width;
    return value;
  }

  /// <summary>Writes a checksum as an infinint width followed by the ring.</summary>
  public static void WriteChecksum(Stream output, ReadOnlySpan<byte> ring) {
    WriteInfinint(output, (ulong)ring.Length);
    output.Write(ring);
  }

  /// <summary>
  /// Writes a terminator (section 5): the infinint <paramref name="position"/>, zero-padded to
  /// whole 4-byte blocks, then the block count in unary read backward — a byte whose top bits
  /// count the blocks modulo 8 (or a zero byte), then one 0xFF per further eight blocks.
  /// </summary>
  public static void WriteTerminator(Stream output, ulong position) {
    using var value = new MemoryStream();
    WriteInfinint(value, position);
    var length = (int)value.Length;
    var blocks = (length + 3) / 4;
    output.Write(value.GetBuffer().AsSpan(0, length));
    for (var i = length; i < blocks * 4; ++i)
      output.WriteByte(0);
    var low = blocks % 8;
    output.WriteByte(low == 0 ? (byte)0 : (byte)(0xFF << (8 - low)));
    for (var i = 0; i < blocks / 8; ++i)
      output.WriteByte(0xFF);
  }

  /// <summary>
  /// Reads the terminator that ends just before <paramref name="end"/> in <paramref name="s"/>.
  /// Returns the position it records and sets <paramref name="start"/> to where it begins.
  /// </summary>
  public static ulong ReadTerminator(Stream s, long end, out long start) {
    long blocks = 0;
    var at = end;
    int b;
    while (true) {
      if (--at < 0)
        throw new InvalidDataException("DAR terminator runs past the start of the archive.");
      s.Position = at;
      b = s.ReadByte();
      if (b != 0xFF)
        break;
      blocks += 8;
      if (blocks > 1 << 20)
        throw new InvalidDataException("DAR terminator is implausibly long.");
    }
    while (b != 0) {
      if ((b & 0x80) == 0)
        throw new InvalidDataException($"Badly formed DAR terminator byte 0x{b:X2}.");
      ++blocks;
      b = (b << 1) & 0xFF;
    }
    start = at - blocks * 4;
    if (blocks == 0 || start < 0)
      throw new InvalidDataException("Badly formed DAR terminator.");
    var buffer = new byte[blocks * 4];
    s.Position = start;
    s.ReadExactly(buffer);
    var pos = 0;
    return ReadInfinint(buffer, ref pos);
  }

  /// <summary>
  /// Copies <paramref name="s"/> from <paramref name="offset"/> removing the escape layer
  /// (section 6): a prefix followed by <see cref="NotAMark"/> stands for the prefix as data;
  /// any other mark ends the data. Stops after <paramref name="logicalLength"/> data bytes, at a
  /// real mark, or at <paramref name="limit"/>.
  /// </summary>
  public static byte[] ReadUnescaped(Stream s, long offset, long logicalLength, long limit) {
    if (logicalLength > Array.MaxLength)
      throw new NotSupportedException($"DAR member of {logicalLength} stored bytes exceeds the in-memory limit.");
    var output = new MemoryStream(logicalLength < 0 ? 4096 : (int)Math.Min(logicalLength, 1 << 20));
    var prefix = EscapePrefix;
    var window = new byte[64 * 1024 + 8];
    var carry = 0;
    var position = offset;
    s.Position = offset;
    while (logicalLength < 0 || output.Length < logicalLength) {
      var want = (int)Math.Min(window.Length - carry, limit - position);
      var read = want <= 0 ? 0 : s.Read(window, carry, want);
      position += read;
      var available = carry + read;
      var atEnd = read == 0;
      var i = 0;
      while (i < available && (logicalLength < 0 || output.Length < logicalLength)) {
        if (window[i] == prefix[0]) {
          if (available - i < 6 && !atEnd)
            break; // need more bytes to classify
          if (available - i >= 6 && window.AsSpan(i, 5).SequenceEqual(prefix)) {
            if (window[i + 5] != NotAMark)
              return output.ToArray(); // a real mark ends the data
            output.Write(prefix);
            i += 6;
            continue;
          }
        }
        output.WriteByte(window[i]);
        ++i;
      }
      carry = available - i;
      Array.Copy(window, i, window, 0, carry);
      if (atEnd)
        break;
    }
    if (logicalLength >= 0 && output.Length < logicalLength)
      throw new InvalidDataException($"DAR member data ends after {output.Length} of {logicalLength} bytes.");
    return output.ToArray();
  }

  /// <summary>
  /// Expands sparse-encoded data (section 8): a <see cref="SparsePrefix"/> followed by
  /// <see cref="HoleMark"/> and an infinint is that many zero bytes; followed by
  /// <see cref="NotAMark"/> it is the prefix itself as data.
  /// </summary>
  public static byte[] Unsparse(ReadOnlySpan<byte> data, ulong expectedSize) {
    if (expectedSize > (ulong)Array.MaxLength)
      throw new NotSupportedException($"DAR sparse member of {expectedSize} bytes exceeds the in-memory limit.");
    var output = new byte[expectedSize];
    var o = 0L;
    var prefix = SparsePrefix;
    var i = 0;
    while (i < data.Length) {
      if (data[i] == prefix[0] && data.Length - i >= 6 && data.Slice(i, 5).SequenceEqual(prefix)) {
        var type = data[i + 5];
        if (type == NotAMark) {
          Put(prefix);
          i += 6;
          continue;
        }
        if (type != HoleMark)
          throw new InvalidDataException($"Unknown DAR sparse record type 0x{type:X2}.");
        var p = i + 6;
        var zeros = ReadInfinint(data, ref p);
        if (zeros > expectedSize - (ulong)o)
          throw new InvalidDataException("DAR hole runs past the recorded file size.");
        o += (long)zeros; // the array is already zeroed
        i = p;
        continue;
      }
      Put(data.Slice(i, 1));
      ++i;
    }
    if ((ulong)o != expectedSize)
      throw new InvalidDataException($"DAR sparse data expands to {o} bytes, the catalogue says {expectedSize}.");
    return output;

    void Put(ReadOnlySpan<byte> bytes) {
      if ((ulong)(o + bytes.Length) > expectedSize)
        throw new InvalidDataException("DAR sparse data runs past the recorded file size.");
      bytes.CopyTo(output.AsSpan((int)o));
      o += bytes.Length;
    }
  }
}
