namespace FileFormat.UuEncoding;

/// <summary>
/// Classic Unix-to-Unix encoding for binary-to-text conversion.
/// </summary>
public static class UuEncoder {

  /// <summary>Encodes binary data into UUEncoded text.</summary>
  public static void Encode(Stream input, Stream output, string filename, int mode = 0644) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();
    using var writer = new StreamWriter(output, leaveOpen: true);
    writer.NewLine = "\n";
    writer.WriteLine($"begin {Convert.ToString(mode, 8)} {filename}");

    var offset = 0;
    while (offset < data.Length) {
      var count = Math.Min(45, data.Length - offset);
      writer.Write((char)(count + 32));
      for (var i = 0; i < count; i += 3) {
        var b0 = data[offset + i];
        var b1 = i + 1 < count ? data[offset + i + 1] : (byte)0;
        var b2 = i + 2 < count ? data[offset + i + 2] : (byte)0;
        writer.Write(UuChar((b0 >> 2) & 0x3F));
        writer.Write(UuChar(((b0 << 4) | (b1 >> 4)) & 0x3F));
        writer.Write(UuChar(((b1 << 2) | (b2 >> 6)) & 0x3F));
        writer.Write(UuChar(b2 & 0x3F));
      }
      writer.WriteLine();
      offset += count;
    }
    // End marker: backtick (zero-length line) then "end"
    writer.WriteLine("`");
    writer.WriteLine("end");
    writer.Flush();
  }

  /// <summary>Decodes UUEncoded text back to binary.</summary>
  public static (string FileName, int Mode, byte[] Data) Decode(Stream input) {
    using var reader = new StreamReader(input, leaveOpen: true);
    string? line;
    string filename = "unknown";
    int mode = 0644;

    // Find begin line
    while ((line = reader.ReadLine()) != null) {
      if (line.StartsWith("begin ")) {
        var parts = line.Split(' ', 3);
        if (parts.Length >= 3) {
          try { mode = Convert.ToInt32(parts[1], 8); } catch { mode = 0644; }
          filename = parts[2];
        }
        break;
      }
      if (line.StartsWith("begin-base64 ")) {
        var parts = line.Split(' ', 3);
        if (parts.Length >= 3) {
          try { mode = Convert.ToInt32(parts[1], 8); } catch { mode = 0644; }
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
    var sb = new System.Text.StringBuilder();
    string? line;
    while ((line = reader.ReadLine()) != null) {
      if (line == "====" || line == "end") break;
      sb.Append(line);
    }
    return (filename, mode, Convert.FromBase64String(sb.ToString()));
  }

  private static char UuChar(int val) => (char)(val == 0 ? 96 : val + 32); // 0 maps to backtick, rest to space+val
}
