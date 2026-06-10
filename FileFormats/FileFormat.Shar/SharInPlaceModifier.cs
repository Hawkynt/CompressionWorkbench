#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Shar;

/// <summary>
/// Random-access in-place modifier for shell-archive (.shar) files. Shar
/// is a plain-text shell script with a trailing <c>exit 0</c> sentinel —
/// the textbook "append before the terminator" shape. Add seeks to the
/// last <c>exit 0</c> line, overwrites it with a new <c>echo x - name</c>
/// block (heredoc for text, uudecode for binary), and re-writes a fresh
/// <c>exit 0</c> sentinel.
/// </summary>
/// <remarks>
/// The pre-existing bytes before the old <c>exit 0</c> line are byte-identical
/// after the operation: we never touch the script preamble or any prior entry
/// block. Remove is not implemented in-place — Shar's variable-length text
/// records do not lend themselves to a fast safe compactor for the
/// heredoc-with-arbitrary-content case, so callers should rebuild via
/// <see cref="SharReader"/>/<see cref="SharWriter"/>.
/// </remarks>
public static class SharInPlaceModifier {

  private static readonly byte[] ExitMarker = "exit 0\n"u8.ToArray();
  private const string Delimiter = "SHAR_EOF";
  private const string UuDelimiter = "SHAR_UU_EOF";

  /// <summary>
  /// Appends a file entry to a shar archive. The byte range before the
  /// existing <c>exit 0</c> sentinel is not modified.
  /// </summary>
  public static void AddFile(Stream shar, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(shar);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var exitOffset = FindExitMarkerOffset(shar);
    shar.Position = exitOffset;

    // Emit a new entry block. Mirrors SharWriter's layout so SharReader
    // round-trips the append.
    using (var sw = new StreamWriter(shar, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" }) {
      var safeName = name.Replace("'", "'\\''");
      sw.WriteLine($"echo x - {safeName}");
      if (IsBinary(data))
        WriteUuencoded(sw, safeName, data);
      else
        WriteHeredoc(sw, safeName, data);
      sw.WriteLine();
      sw.WriteLine("exit 0");
      sw.Flush();
    }
    shar.SetLength(shar.Position);
  }

  /// <summary>
  /// Returns the byte offset of the trailing <c>exit 0\n</c> sentinel.
  /// Scans the tail of the stream backwards so very large archives don't
  /// need to be re-buffered in full.
  /// </summary>
  private static long FindExitMarkerOffset(Stream shar) {
    // Scan the trailing region for the most recent "exit 0\n" line —
    // walk the last 64 KiB which is more than enough for any plausible
    // shar trailer (the writer emits a single line, the agent's own
    // marker is 7 bytes).
    var tailLen = (int)Math.Min(shar.Length, 65536);
    if (tailLen <= 0) return shar.Length;
    var tailStart = shar.Length - tailLen;
    var tail = new byte[tailLen];
    shar.Position = tailStart;
    var read = 0;
    while (read < tailLen) {
      var n = shar.Read(tail, read, tailLen - read);
      if (n <= 0) break;
      read += n;
    }

    // We look for "\nexit 0\n" (preceding newline anchors the start of the line).
    var pattern = new byte[ExitMarker.Length + 1];
    pattern[0] = (byte)'\n';
    Array.Copy(ExitMarker, 0, pattern, 1, ExitMarker.Length);

    var match = LastIndexOf(tail.AsSpan(0, read), pattern);
    if (match >= 0)
      return tailStart + match + 1; // +1 so the leading "\n" of the previous line is preserved

    // Tolerate "exit 0\n" at offset 0 (degenerate shar with no preamble).
    var bare = LastIndexOf(tail.AsSpan(0, read), ExitMarker);
    if (bare >= 0)
      return tailStart + bare;

    // No sentinel found — append at EOF and the new entry will become the trailer.
    return shar.Length;
  }

  private static int LastIndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    if (needle.Length == 0 || needle.Length > haystack.Length) return -1;
    for (var i = haystack.Length - needle.Length; i >= 0; --i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j) {
        if (haystack[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return i;
    }
    return -1;
  }

  // ── Entry body writers (mirror SharWriter so SharReader round-trips) ──

  private static bool IsBinary(byte[] data) {
    var probe = Math.Min(data.Length, 8192);
    for (var i = 0; i < probe; ++i) {
      var b = data[i];
      if (b == 0 || (b < 0x20 && b != '\t' && b != '\n' && b != '\r'))
        return true;
    }
    return false;
  }

  private static void WriteHeredoc(StreamWriter writer, string name, byte[] data) {
    writer.WriteLine($"sed 's/^X//' > '{name}' << '{Delimiter}'");
    var text = Encoding.UTF8.GetString(data);
    foreach (var line in text.Split('\n'))
      writer.WriteLine("X" + line);
    writer.WriteLine(Delimiter);
  }

  private static void WriteUuencoded(StreamWriter writer, string name, byte[] data) {
    writer.WriteLine($"uudecode << '{UuDelimiter}'");
    writer.WriteLine($"begin 644 {name}");
    var offset = 0;
    while (offset < data.Length) {
      var len = Math.Min(45, data.Length - offset);
      var sb = new StringBuilder();
      sb.Append((char)(len + 32));
      for (var i = 0; i < len; i += 3) {
        var b0 = data[offset + i];
        var b1 = i + 1 < len ? data[offset + i + 1] : (byte)0;
        var b2 = i + 2 < len ? data[offset + i + 2] : (byte)0;
        sb.Append(UuChar(b0 >> 2));
        sb.Append(UuChar(((b0 & 0x3) << 4) | (b1 >> 4)));
        sb.Append(UuChar(((b1 & 0xF) << 2) | (b2 >> 6)));
        sb.Append(UuChar(b2 & 0x3F));
      }
      writer.WriteLine(sb.ToString());
      offset += len;
    }
    writer.WriteLine("`");
    writer.WriteLine("end");
    writer.WriteLine(UuDelimiter);
  }

  private static char UuChar(int val) => (char)((val & 0x3F) + 32);
}
