using System.Text;

namespace FileFormat.Shar;

/// <summary>
/// Creates a shell archive (shar) file.
/// Text files use heredoc (cat), binary files use uuencode.
/// </summary>
public sealed class SharWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the shar archive.</summary>
  public void AddFile(string name, byte[] data) => this._files.Add((name, data));

  /// <summary>Writes the shar archive to a stream.</summary>
  public void WriteTo(Stream output) {
    using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

    writer.WriteLine("#!/bin/sh");
    writer.WriteLine("# This is a shell archive (produced by cwb).");
    writer.WriteLine("# To extract the files, run this script with /bin/sh.");
    writer.WriteLine();

    foreach (var (name, data) in this._files) {
      var safeName = name.Replace("'", "'\\''");
      writer.WriteLine($"echo x - {safeName}");

      if (IsBinary(data)) {
        WriteUuencoded(writer, safeName, data);
      }
      else {
        WriteHeredoc(writer, safeName, data);
      }
      writer.WriteLine();
    }

    writer.WriteLine("exit 0");
    writer.Flush();
  }

  /// <summary>Writes the shar archive to a byte array.</summary>
  public byte[] ToByteArray() {
    using var ms = new MemoryStream();
    WriteTo(ms);
    return ms.ToArray();
  }

  private static bool IsBinary(byte[] data) {
    for (var i = 0; i < Math.Min(data.Length, 8192); ++i) {
      var b = data[i];
      if (b == 0 || (b < 0x20 && b != '\t' && b != '\n' && b != '\r'))
        return true;
    }
    return false;
  }

  private static void WriteHeredoc(StreamWriter writer, string name, byte[] data) {
    const string delimiter = "SHAR_EOF";
    writer.WriteLine($"sed 's/^X//' > '{name}' << '{delimiter}'");
    var text = Encoding.UTF8.GetString(data);
    foreach (var line in text.Split('\n'))
      writer.WriteLine("X" + line);
    writer.WriteLine(delimiter);
  }

  private static void WriteUuencoded(StreamWriter writer, string name, byte[] data) {
    writer.WriteLine($"uudecode << 'SHAR_UU_EOF'");
    writer.WriteLine($"begin 644 {name}");

    var offset = 0;
    while (offset < data.Length) {
      var len = Math.Min(45, data.Length - offset);
      var sb = new StringBuilder();
      sb.Append((char)(len + 32));

      for (var i = 0; i < len; i += 3) {
        var b0 = data[offset + i];
        var b1 = (i + 1 < len) ? data[offset + i + 1] : (byte)0;
        var b2 = (i + 2 < len) ? data[offset + i + 2] : (byte)0;
        sb.Append(UuChar(b0 >> 2));
        sb.Append(UuChar(((b0 & 0x3) << 4) | (b1 >> 4)));
        sb.Append(UuChar(((b1 & 0xF) << 2) | (b2 >> 6)));
        sb.Append(UuChar(b2 & 0x3F));
      }
      writer.WriteLine(sb.ToString());
      offset += len;
    }

    writer.WriteLine("`");  // zero-length line
    writer.WriteLine("end");
    writer.WriteLine("SHAR_UU_EOF");
  }

  private static char UuChar(int val) => (char)((val & 0x3F) + 32);
}
