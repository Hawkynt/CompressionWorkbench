namespace FileFormat.UuEncoding;

/// <summary>
/// XXencode: the uuencode envelope (<c>begin mode name</c> … <c>end</c>) with a
/// 64-character alphabet that avoids the punctuation EBCDIC gateways mangled.
/// </summary>
/// <remarks>
/// Each body line carries up to 45 bytes: one length character, then four
/// characters per three input bytes, all drawn from
/// <c>+-0-9A-Za-z</c>. The empty line that closes the body is the length
/// character for zero, <c>+</c>. The alphabet is the one the original
/// <c>xxencode</c> (Phil Howard, 1987) and uudeview's <c>uuenview -x</c> emit.
/// </remarks>
public static class XxEncoder {

  /// <summary>The XXencode alphabet; index is the 6-bit value.</summary>
  public const string Alphabet = "+-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

  /// <summary>Bytes per full body line, the same 45 uuencode uses.</summary>
  private const int BytesPerLine = 45;

  /// <summary>Default mode announced in the header, octal 644.</summary>
  private const int DefaultMode = 0x1A4;

  /// <summary>Permission bits of a mode word, octal 777.</summary>
  private const int PermissionMask = 0x1FF;

  private static readonly sbyte[] _reverse = BuildReverse();

  private static sbyte[] BuildReverse() {
    var table = new sbyte[128];
    Array.Fill(table, (sbyte)-1);
    for (var i = 0; i < Alphabet.Length; ++i)
      table[Alphabet[i]] = (sbyte)i;
    return table;
  }

  /// <summary>Encodes <paramref name="input"/> as XXencoded text.</summary>
  /// <param name="input">Binary data to encode.</param>
  /// <param name="output">Destination for the encoded text.</param>
  /// <param name="filename">Name announced in the <c>begin</c> header.</param>
  /// <param name="mode">Permission word, written to the header as octal (default 420 = octal 644).</param>
  public static void Encode(Stream input, Stream output, string filename, int mode = DefaultMode) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(filename);

    using var writer = new StreamWriter(output, leaveOpen: true) { NewLine = "\n" };
    writer.WriteLine($"begin {Convert.ToString(mode & PermissionMask, 8)} {filename}");

    Span<byte> data = stackalloc byte[BytesPerLine];
    Span<char> line = stackalloc char[1 + BytesPerLine / 3 * 4];
    while (true) {
      var count = ReadUpTo(input, data);
      if (count == 0)
        break;

      var n = 0;
      line[n++] = Alphabet[count];
      for (var i = 0; i < count; i += 3) {
        var b0 = data[i];
        var b1 = i + 1 < count ? data[i + 1] : 0;
        var b2 = i + 2 < count ? data[i + 2] : 0;
        line[n++] = Alphabet[b0 >> 2];
        line[n++] = Alphabet[((b0 << 4) | (b1 >> 4)) & 0x3F];
        line[n++] = Alphabet[((b1 << 2) | (b2 >> 6)) & 0x3F];
        line[n++] = Alphabet[b2 & 0x3F];
      }
      writer.WriteLine(line[..n]);
    }

    writer.WriteLine(Alphabet[0]);
    writer.WriteLine("end");
    writer.Flush();
  }

  /// <summary>
  /// Decodes XXencoded text. Lines before <c>begin</c> (mail headers, prose) are
  /// skipped; the body ends at the zero-length line, at <c>end</c>, or at end of input.
  /// </summary>
  /// <exception cref="InvalidDataException">No <c>begin</c> line, or a body character outside the alphabet.</exception>
  public static (string FileName, int Mode, byte[] Data) Decode(Stream input) {
    ArgumentNullException.ThrowIfNull(input);

    using var reader = new StreamReader(input, leaveOpen: true);
    string? line;
    var filename = "unknown";
    var mode = DefaultMode;
    var found = false;
    while ((line = reader.ReadLine()) != null) {
      if (!line.StartsWith("begin ", StringComparison.Ordinal))
        continue;
      var parts = line.Split(' ', 3);
      if (parts.Length >= 3) {
        mode = ParseMode(parts[1]);
        filename = parts[2];
      }
      found = true;
      break;
    }
    if (!found)
      throw new InvalidDataException("XXencode: no 'begin' line found.");

    using var output = new MemoryStream();
    Span<byte> decoded = stackalloc byte[3];
    while ((line = reader.ReadLine()) != null) {
      line = line.TrimEnd('\r', ' ', '\t');
      if (line.Length == 0)
        continue;
      if (line == "end")
        break;

      var count = Value(line[0]);
      if (count == 0)
        break;

      var pos = 1;
      for (var i = 0; i < count; i += 3) {
        var c0 = pos < line.Length ? Value(line[pos++]) : 0;
        var c1 = pos < line.Length ? Value(line[pos++]) : 0;
        var c2 = pos < line.Length ? Value(line[pos++]) : 0;
        var c3 = pos < line.Length ? Value(line[pos++]) : 0;
        decoded[0] = (byte)((c0 << 2) | (c1 >> 4));
        decoded[1] = (byte)((c1 << 4) | (c2 >> 2));
        decoded[2] = (byte)((c2 << 6) | c3);
        output.Write(decoded[..Math.Min(3, count - i)]);
      }
    }
    return (filename, mode, output.ToArray());
  }

  private static int Value(char c) {
    var v = c < 128 ? _reverse[c] : -1;
    return v >= 0 ? v : throw new InvalidDataException($"XXencode: character '{c}' (U+{(int)c:X4}) is outside the alphabet.");
  }

  private static int ReadUpTo(Stream input, Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = input.Read(buffer[total..]);
      if (read == 0)
        break;
      total += read;
    }
    return total;
  }

  private static int ParseMode(string text) {
    try { return Convert.ToInt32(text, 8); }
    catch (FormatException) { return DefaultMode; }
    catch (OverflowException) { return DefaultMode; }
  }
}
