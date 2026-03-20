using System.Text;

namespace FileFormat.Shar;

/// <summary>
/// Reads a shell archive (shar) file.
/// Supports the common 'cat &gt; file &lt;&lt; delimiter' and 'sed ... &gt; file &lt;&lt; delimiter' patterns.
/// Also decodes uuencoded binary entries.
/// </summary>
public sealed class SharReader {
  private readonly List<SharEntry> _entries = [];

  /// <summary>The entries found in the shar archive.</summary>
  public IReadOnlyList<SharEntry> Entries => this._entries;

  /// <summary>
  /// Reads and parses a shar archive from a stream.
  /// </summary>
  public SharReader(Stream stream) {
    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
    Parse(reader);
  }

  private void Parse(StreamReader reader) {
    string? line;
    while ((line = reader.ReadLine()) != null) {
      // Pattern 1: cat > 'filename' << 'DELIMITER' or cat > filename << DELIMITER
      // Pattern 2: sed 's/^X//' > 'filename' << 'DELIMITER'
      if (TryParseHeredoc(line, out var fileName, out var delimiter, out var sedPrefix)) {
        var content = ReadHeredoc(reader, delimiter, sedPrefix);
        this._entries.Add(new SharEntry { FileName = fileName, Data = Encoding.UTF8.GetBytes(content) });
        continue;
      }

      // Pattern 3: uudecode << 'DELIMITER' or begin [mode] [filename]
      if (line.TrimStart().StartsWith("begin ", StringComparison.Ordinal)) {
        var parts = line.TrimStart().Split(' ', 3);
        if (parts.Length >= 3) {
          var fn = parts[2].Trim('\'', '"');
          var data = ReadUuencoded(reader);
          this._entries.Add(new SharEntry { FileName = fn, Data = data });
        }
      }
    }
  }

  private static bool TryParseHeredoc(string line, out string fileName, out string delimiter, out string? sedPrefix) {
    fileName = "";
    delimiter = "";
    sedPrefix = null;

    var trimmed = line.Trim();

    // Match: cat > 'file' << 'DELIM'  or  sed 's/^X//' > 'file' << 'DELIM'
    var heredocIdx = trimmed.IndexOf("<<", StringComparison.Ordinal);
    if (heredocIdx < 0) return false;

    var redirectIdx = trimmed.IndexOf("> ", StringComparison.Ordinal);
    if (redirectIdx < 0 || redirectIdx > heredocIdx) return false;

    // Check for sed prefix stripping
    if (trimmed.StartsWith("sed ", StringComparison.Ordinal)) {
      // Extract the sed replacement pattern to know what prefix to strip
      var sedMatch = trimmed.IndexOf("'s/^", StringComparison.Ordinal);
      if (sedMatch >= 0) {
        var endSlash = trimmed.IndexOf("//", sedMatch + 4, StringComparison.Ordinal);
        if (endSlash >= 0)
          sedPrefix = trimmed[(sedMatch + 4)..endSlash];
      }
    }

    // Extract filename between > and <<
    var fileStr = trimmed[(redirectIdx + 2)..heredocIdx].Trim().Trim('\'', '"');
    if (string.IsNullOrEmpty(fileStr)) return false;

    // Extract delimiter after <<
    var delimStr = trimmed[(heredocIdx + 2)..].Trim().Trim('\'', '"', '\\');
    if (string.IsNullOrEmpty(delimStr)) return false;

    fileName = fileStr;
    delimiter = delimStr;
    return true;
  }

  private static string ReadHeredoc(StreamReader reader, string delimiter, string? sedPrefix) {
    var sb = new StringBuilder();
    string? line;
    while ((line = reader.ReadLine()) != null) {
      if (line == delimiter) break;
      if (sedPrefix != null && line.StartsWith(sedPrefix, StringComparison.Ordinal))
        line = line[sedPrefix.Length..];
      if (sb.Length > 0) sb.Append('\n');
      sb.Append(line);
    }
    return sb.ToString();
  }

  private static byte[] ReadUuencoded(StreamReader reader) {
    using var ms = new MemoryStream();
    string? line;
    while ((line = reader.ReadLine()) != null) {
      if (line == "end") break;
      if (line.Length == 0) continue;
      int len = (line[0] - 32) & 0x3F;
      if (len == 0) continue;
      int idx = 1;
      for (int i = 0; i < len; i += 3) {
        int c0 = idx < line.Length ? (line[idx++] - 32) & 0x3F : 0;
        int c1 = idx < line.Length ? (line[idx++] - 32) & 0x3F : 0;
        int c2 = idx < line.Length ? (line[idx++] - 32) & 0x3F : 0;
        int c3 = idx < line.Length ? (line[idx++] - 32) & 0x3F : 0;
        ms.WriteByte((byte)((c0 << 2) | (c1 >> 4)));
        if (i + 1 < len) ms.WriteByte((byte)(((c1 & 0xF) << 4) | (c2 >> 2)));
        if (i + 2 < len) ms.WriteByte((byte)(((c2 & 0x3) << 6) | c3));
      }
    }
    return ms.ToArray();
  }
}
