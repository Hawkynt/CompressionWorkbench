namespace FileFormat.YEnc;

/// <summary>
/// yEnc binary-to-text decoder for Usenet binary encoding.
/// </summary>
public static class YEncDecoder {

  /// <summary>Decodes yEnc-encoded data.</summary>
  public static (string FileName, long Size, uint Crc32, byte[] Data) Decode(Stream input) {
    using var reader = new StreamReader(input, leaveOpen: true);
    string? line;
    string filename = "unknown";
    long size = 0;

    // Find =ybegin header
    while ((line = reader.ReadLine()) != null) {
      if (line.StartsWith("=ybegin ")) {
        filename = ExtractParam(line, "name") ?? "unknown";
        var sizeStr = ExtractParam(line, "size");
        if (sizeStr != null) long.TryParse(sizeStr, out size);
        break;
      }
    }

    // Skip =ypart if present
    var nextLine = reader.ReadLine();
    if (nextLine != null && nextLine.StartsWith("=ypart "))
      nextLine = null; // skip, read next in loop

    using var output = new MemoryStream();
    var startLine = nextLine;

    void ProcessLine(string l) {
      for (var i = 0; i < l.Length; i++) {
        if (l[i] == '=' && i + 1 < l.Length) {
          i++;
          output.WriteByte((byte)((l[i] - 64 - 42) & 0xFF));
        } else {
          output.WriteByte((byte)((l[i] - 42) & 0xFF));
        }
      }
    }

    if (startLine != null && !startLine.StartsWith("=y"))
      ProcessLine(startLine);

    uint trailCrc = 0;
    while ((line = reader.ReadLine()) != null) {
      if (line.StartsWith("=yend")) {
        var crcStr = ExtractParam(line, "crc32");
        if (crcStr != null) {
          // CRC32 is often hex
          if (crcStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trailCrc = uint.Parse(crcStr[2..], System.Globalization.NumberStyles.HexNumber);
          else
            trailCrc = uint.Parse(crcStr, System.Globalization.NumberStyles.HexNumber);
        }
        break;
      }
      ProcessLine(line);
    }

    var data = output.ToArray();
    return (filename, size, trailCrc, data);
  }

  private static string? ExtractParam(string line, string param) {
    // "name" is special: it's always at the end and the value extends to EOL
    if (param == "name") {
      var idx = line.IndexOf(" name=", StringComparison.Ordinal);
      if (idx < 0) return null;
      return line[(idx + 6)..];
    }
    var key = $" {param}=";
    var start = line.IndexOf(key, StringComparison.Ordinal);
    if (start < 0) return null;
    start += key.Length;
    var end = line.IndexOf(' ', start);
    return end < 0 ? line[start..] : line[start..end];
  }
}
