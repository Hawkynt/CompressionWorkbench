using System.Globalization;
using System.Text;

namespace FileFormat.Btoa;

/// <summary>
/// The btoa 4.0 text encoding (Paul Rutter, Joe Orost): the input is taken four bytes at
/// a time as a big-endian 32-bit word and written as five base-85 digits from <c>'!'</c>
/// upward, a zero word as the single letter <c>z</c>, between an <c>xbtoa Begin</c> line
/// and an <c>xbtoa End</c> trailer that carries the byte count and three running
/// checksums.
/// </summary>
/// <remarks>
/// <para>
/// The trailer is <c>xbtoa End N &lt;count&gt; &lt;count-hex&gt; E &lt;xor&gt; S &lt;sum&gt; R &lt;rot&gt;</c>;
/// the three checksums are 32-bit and run over every encoded byte, including the zero
/// bytes that pad the last word: <c>E ^= b</c>, <c>S += b + 1</c>, and <c>R</c> rotates
/// left by one bit before <c>b</c> is added. Encoded text is broken after every 78
/// characters, a group may straddle the break, and a blank line separates the data from
/// the trailer.
/// </para>
/// <para>
/// The behaviour is that of btoa/atob 4.0 built for 32-bit <c>long</c>, as used on the
/// systems the format came from; on a 64-bit <c>long</c> the original program does not
/// produce this format.
/// </para>
/// </remarks>
public static class BtoaCodec {
  /// <summary>The line that opens the encoded data.</summary>
  public const string BeginLine = "xbtoa Begin";

  /// <summary>Characters per encoded line.</summary>
  public const int LineLength = 78;

  /// <summary>Encodes <paramref name="data"/> into btoa 4.0 text.</summary>
  public static void Encode(ReadOnlySpan<byte> data, Stream output) {
    var text = new StringBuilder(data.Length * 5 / 4 + data.Length / 60 + 96);
    text.Append(BeginLine).Append('\n');

    var checksums = new Checksums();
    var column = 0;
    Span<char> digits = stackalloc char[5];
    for (var i = 0; i < data.Length; i += 4) {
      uint word = 0;
      for (var k = 0; k < 4; ++k) {
        var b = i + k < data.Length ? data[i + k] : (byte)0;
        checksums.Add(b);
        word = (word << 8) | b;
      }

      if (word == 0) {
        Emit(text, 'z', ref column);
        continue;
      }

      for (var k = 4; k >= 0; --k) {
        digits[k] = (char)('!' + word % 85);
        word /= 85;
      }
      foreach (var c in digits)
        Emit(text, c, ref column);
    }

    text.Append('\n');
    text.Append(CultureInfo.InvariantCulture,
      $"xbtoa End N {data.Length} {data.Length:x} E {checksums.Xor:x} S {checksums.Sum:x} R {checksums.Rotate:x}\n");
    var bytes = Encoding.ASCII.GetBytes(text.ToString());
    output.Write(bytes);
  }

  /// <summary>
  /// Decodes btoa 4.0 text. Anything before the <c>xbtoa Begin</c> line (a mail header,
  /// say) is skipped; a character outside the alphabet, a misplaced <c>z</c>, a group that
  /// does not fit 32 bits, a malformed trailer or a checksum that does not match throws
  /// <see cref="InvalidDataException"/> and nothing is returned.
  /// </summary>
  public static byte[] Decode(ReadOnlySpan<byte> text) {
    var position = FindBegin(text);
    if (position < 0)
      throw new InvalidDataException("btoa: no \"xbtoa Begin\" line.");

    using var decoded = new MemoryStream();
    var checksums = new Checksums();
    ulong word = 0;
    var count = 0;
    for (; ; ++position) {
      if (position >= text.Length)
        throw new InvalidDataException("btoa: the data ends without an \"xbtoa End\" trailer.");

      var c = text[position];
      if (c is (byte)'\n' or (byte)'\r')
        continue;
      if (c == (byte)'x')
        break;

      if (c == (byte)'z') {
        if (count != 0)
          throw new InvalidDataException($"btoa: 'z' inside a group at offset {position}.");
        for (var k = 0; k < 4; ++k) {
          checksums.Add(0);
          decoded.WriteByte(0);
        }
        continue;
      }

      if (c < (byte)'!' || c >= (byte)'!' + 85)
        throw new InvalidDataException($"btoa: byte 0x{c:X2} at offset {position} is not part of the alphabet.");

      word = word * 85 + (uint)(c - '!');
      if (++count < 5)
        continue;
      if (word > uint.MaxValue)
        throw new InvalidDataException($"btoa: the group ending at offset {position} exceeds 32 bits.");

      for (var shift = 24; shift >= 0; shift -= 8) {
        var b = (byte)(word >> shift);
        checksums.Add(b);
        decoded.WriteByte(b);
      }
      word = 0;
      count = 0;
    }

    if (count != 0)
      throw new InvalidDataException("btoa: the data ends inside a group.");

    var trailer = ReadTrailer(text[position..]);
    if (trailer.Count != trailer.CountHex)
      throw new InvalidDataException("btoa: the trailer's two byte counts disagree.");
    if (trailer.Count > decoded.Length || decoded.Length - trailer.Count > 3)
      throw new InvalidDataException($"btoa: the trailer records {trailer.Count} bytes but the data holds {decoded.Length}.");
    if (trailer.Xor != checksums.Xor || trailer.Sum != checksums.Sum || trailer.Rotate != checksums.Rotate)
      throw new InvalidDataException("btoa: checksum mismatch.");

    return decoded.GetBuffer().AsSpan(0, (int)trailer.Count).ToArray();
  }

  /// <summary>Offset just past the <c>xbtoa Begin</c> line, or -1.</summary>
  private static int FindBegin(ReadOnlySpan<byte> text) {
    var begin = "xbtoa Begin"u8;
    for (var lineStart = 0; lineStart < text.Length;) {
      var end = text[lineStart..].IndexOf((byte)'\n');
      var line = end < 0 ? text[lineStart..] : text.Slice(lineStart, end);
      if (line.Length > 0 && line[^1] == (byte)'\r')
        line = line[..^1];
      if (line.SequenceEqual(begin))
        return end < 0 ? text.Length : lineStart + end + 1;
      if (end < 0)
        break;
      lineStart += end + 1;
    }

    return -1;
  }

  private readonly record struct Trailer(long Count, long CountHex, uint Xor, uint Sum, uint Rotate);

  /// <summary>Parses <c>xbtoa End N n nhex E eor S sum R rot</c> starting at the <c>x</c>.</summary>
  private static Trailer ReadTrailer(ReadOnlySpan<byte> text) {
    var end = text.IndexOf((byte)'\n');
    var line = Encoding.ASCII.GetString(end < 0 ? text : text[..end]).TrimEnd('\r');
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 11 || parts[0] != "xbtoa" || parts[1] != "End" || parts[2] != "N" ||
        parts[5] != "E" || parts[7] != "S" || parts[9] != "R" ||
        !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
        !long.TryParse(parts[4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var countHex) ||
        !uint.TryParse(parts[6], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var xor) ||
        !uint.TryParse(parts[8], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var sum) ||
        !uint.TryParse(parts[10], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rotate))
      throw new InvalidDataException($"btoa: malformed trailer \"{line}\".");

    return new(count, countHex, xor, sum, rotate);
  }

  private static void Emit(StringBuilder text, char c, ref int column) {
    text.Append(c);
    if (++column != LineLength)
      return;
    text.Append('\n');
    column = 0;
  }

  /// <summary>btoa's three running checksums, all modulo 2^32.</summary>
  private struct Checksums {
    public uint Xor;
    public uint Sum;
    public uint Rotate;

    public void Add(byte b) {
      this.Xor ^= b;
      this.Sum += b + 1u;
      this.Rotate = ((this.Rotate & 0x80000000u) != 0 ? (this.Rotate << 1) + 1 : this.Rotate << 1) + b;
    }
  }
}
