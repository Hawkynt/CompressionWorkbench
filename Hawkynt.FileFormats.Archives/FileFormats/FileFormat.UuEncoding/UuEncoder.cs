namespace FileFormat.UuEncoding;

/// <summary>
/// Classic Unix-to-Unix encoding and the <c>begin-base64</c> wrapper used by
/// libarchive's b64encode filter.
/// </summary>
public static class UuEncoder {

  /// <summary>
  /// Permission bits of a mode word, octal 777. C# has no octal literal, so the
  /// mask is written in hex: a literal <c>0777</c> would be the decimal number
  /// 777 and would clear bits that belong to the permission field.
  /// </summary>
  private const int PermissionMask = 0x1FF;

  /// <summary>Default mode both wrappers announce, octal 644 — the value uuencode and libarchive use.</summary>
  private const int DefaultMode = 0x1A4;

  /// <summary>Encodes binary data into classic UUEncoded text without buffering the full input.</summary>
  public static void Encode(Stream input, Stream output, string filename, int mode = DefaultMode) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(filename);

    using var writer = new StreamWriter(output, leaveOpen: true) { NewLine = "\n" };
    writer.WriteLine($"begin {Convert.ToString(mode & PermissionMask, 8)} {filename}");

    Span<byte> data = stackalloc byte[45];
    while (true) {
      var count = ReadUpTo(input, data);
      if (count == 0)
        break;

      writer.Write((char)(count + 32));
      for (var i = 0; i < count; i += 3) {
        var b0 = data[i];
        var b1 = i + 1 < count ? data[i + 1] : (byte)0;
        var b2 = i + 2 < count ? data[i + 2] : (byte)0;
        writer.Write(UuChar((b0 >> 2) & 0x3F));
        writer.Write(UuChar(((b0 << 4) | (b1 >> 4)) & 0x3F));
        writer.Write(UuChar(((b1 << 2) | (b2 >> 6)) & 0x3F));
        writer.Write(UuChar(b2 & 0x3F));
      }
      writer.WriteLine();
    }

    writer.WriteLine("`");
    writer.WriteLine("end");
    writer.Flush();
  }

  /// <summary>
  /// Encodes binary data using the libarchive/GNU-style <c>begin-base64</c>
  /// uuencode wrapper. Input is consumed in 57-byte blocks, yielding canonical
  /// 76-character Base64 lines.
  /// </summary>
  public static void EncodeBase64(Stream input, Stream output, string filename = "-", int mode = DefaultMode) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(filename);

    using var writer = new StreamWriter(output, leaveOpen: true) { NewLine = "\n" };
    writer.WriteLine($"begin-base64 {Convert.ToString(mode & PermissionMask, 8)} {filename}");

    var data = new byte[57];
    while (true) {
      var count = ReadUpTo(input, data);
      if (count == 0)
        break;
      writer.WriteLine(Convert.ToBase64String(data, 0, count));
    }

    writer.WriteLine("====");
    writer.Flush();
  }

  /// <summary>Decodes classic uuencode or <c>begin-base64</c> text back to binary.</summary>
  public static (string FileName, int Mode, byte[] Data) Decode(Stream input) {
    ArgumentNullException.ThrowIfNull(input);

    using var reader = new StreamReader(input, leaveOpen: true);
    string? line;
    string filename = "unknown";
    int mode = DefaultMode;

    while ((line = reader.ReadLine()) != null) {
      if (line.StartsWith("begin ", StringComparison.Ordinal)) {
        var parts = line.Split(' ', 3);
        if (parts.Length >= 3) {
          mode = ParseMode(parts[1]);
          filename = parts[2];
        }
        break;
      }
      if (line.StartsWith("begin-base64 ", StringComparison.Ordinal)) {
        var parts = line.Split(' ', 3);
        if (parts.Length >= 3) {
          mode = ParseMode(parts[1]);
          filename = parts[2];
        }
        return DecodeBase64Body(reader, filename, mode);
      }
    }

    using var output = new MemoryStream();
    while ((line = reader.ReadLine()) != null) {
      if (line == "`" || line == "end" || line.Length == 0) {
        if (line == "end" || line == "`") break;
        continue;
      }
      var count = (line[0] - 32) & 0x3F;
      if (count == 0) break;
      var pos = 1;
      for (var i = 0; i < count; i += 3) {
        var c0 = pos < line.Length ? (line[pos++] - 32) & 0x3F : 0;
        var c1 = pos < line.Length ? (line[pos++] - 32) & 0x3F : 0;
        var c2 = pos < line.Length ? (line[pos++] - 32) & 0x3F : 0;
        var c3 = pos < line.Length ? (line[pos++] - 32) & 0x3F : 0;
        output.WriteByte((byte)((c0 << 2) | (c1 >> 4)));
        if (i + 1 < count) output.WriteByte((byte)((c1 << 4) | (c2 >> 2)));
        if (i + 2 < count) output.WriteByte((byte)((c2 << 6) | c3));
      }
    }
    return (filename, mode, output.ToArray());
  }

  private static (string FileName, int Mode, byte[] Data) DecodeBase64Body(StreamReader reader, string filename, int mode) {
    using var output = new MemoryStream();
    string? line;
    while ((line = reader.ReadLine()) != null) {
      if (line == "====" || line == "end")
        break;
      if (line.Length == 0)
        continue;
      var decoded = Convert.FromBase64String(line);
      output.Write(decoded);
    }
    return (filename, mode, output.ToArray());
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

  private static char UuChar(int val) => (char)(val == 0 ? 96 : val + 32);
}
