using System.Text;

namespace FileFormat.Shar;

/// <summary>
/// Creates a shell archive (shar) file that <c>/bin/sh</c> extracts.
/// Text files use a here-document through <c>sed</c>, everything else uuencode.
/// </summary>
public sealed class SharWriter {
  private const string Delimiter = "SHAR_EOF";
  private const string UuDelimiter = "SHAR_UU_EOF";

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

    foreach (var (name, data) in this._files)
      WriteEntry(writer, name, data);

    writer.WriteLine("exit 0");
    writer.Flush();
  }

  /// <summary>Writes the shar archive to a byte array.</summary>
  public byte[] ToByteArray() {
    using var ms = new MemoryStream();
    WriteTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Writes one file's block: an announcement, the directory it needs, and its body. Shared with
  /// <see cref="SharInPlaceModifier"/> so appended entries look exactly like written ones.
  /// </summary>
  internal static void WriteEntry(TextWriter writer, string name, byte[] data) {
    var quoted = Quote(name);
    writer.WriteLine($"echo x - {quoted}");
    var slash = name.LastIndexOf('/');
    if (slash > 0)
      writer.WriteLine($"mkdir -p {Quote(name[..slash])}");

    if (IsText(data))
      WriteHeredoc(writer, quoted, data);
    else
      WriteUuencoded(writer, name, data);
    writer.WriteLine();
  }

  /// <summary>A single-quoted shell word: every character literal, a quote written as <c>'\''</c>.</summary>
  private static string Quote(string word) => "'" + word.Replace("'", "'\\''") + "'";

  /// <summary>
  /// Whether this content survives a here-document unchanged.
  /// </summary>
  /// <remarks>
  /// <para>A here-document hands each of its lines on with a newline, so only content that is
  /// lines can travel that way: empty, or ending in a newline. Anything else would gain a newline.</para>
  /// <para>The body goes through <see cref="Encoding.UTF8" />, so a byte that is not 7-bit ASCII
  /// would come back as the replacement character; any byte with the high bit set, NUL and the
  /// other control characters (tab, CR and LF aside) mean uuencode. The whole file is looked at.</para>
  /// </remarks>
  internal static bool IsText(byte[] data) {
    if (data.Length > 0 && data[^1] != '\n')
      return false;
    foreach (var b in data)
      if (b == 0 || b >= 0x80 || (b < 0x20 && b != '\t' && b != '\n' && b != '\r'))
        return false;
    return true;
  }

  private static void WriteHeredoc(TextWriter writer, string quotedName, byte[] data) {
    // Every line gets an X so none of them can read as the delimiter.
    writer.WriteLine($"sed 's/^X//' > {quotedName} << '{Delimiter}'");
    if (data.Length > 0)
      foreach (var line in Encoding.ASCII.GetString(data, 0, data.Length - 1).Split('\n'))
        writer.WriteLine("X" + line);
    writer.WriteLine(Delimiter);
  }

  private static void WriteUuencoded(TextWriter writer, string name, byte[] data) {
    writer.WriteLine($"uudecode << '{UuDelimiter}'");
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
    writer.WriteLine(UuDelimiter);
  }

  // uuencode writes 0 as a backquote rather than a space, so no line ends in spaces.
  private static char UuChar(int val) => (val & 0x3F) == 0 ? '`' : (char)((val & 0x3F) + 32);
}
