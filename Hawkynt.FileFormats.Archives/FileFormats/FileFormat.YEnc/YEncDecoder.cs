using System.Text;

namespace FileFormat.YEnc;

/// <summary>
/// yEnc binary-to-text decoder for Usenet binary encoding.
/// </summary>
public static class YEncDecoder {

  /// <summary>Decodes yEnc-encoded data.</summary>
  /// <remarks>
  /// The body is read as a file, not as an NNTP transfer: a line starting with two dots is two
  /// encoded bytes, as yEnc 1.3 defines it (dot-doubling belongs to the transport). The
  /// <c>=yend</c> size and CRC-32 (<c>pcrc32</c> for a part, else <c>crc32</c>) are checked when
  /// present.
  /// </remarks>
  /// <exception cref="InvalidDataException">No <c>=ybegin</c>/<c>=yend</c>, a dangling escape, or a size or CRC mismatch.</exception>
  public static (string FileName, long Size, uint Crc32, byte[] Data) Decode(Stream input) {
    // yEnc is raw 8-bit: Latin-1 maps every byte to the char of the same value.
    using var reader = new StreamReader(input, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    string? line;
    string filename = "unknown";
    long size = 0;

    // Find =ybegin header
    var begun = false;
    while ((line = reader.ReadLine()) != null) {
      if (line.StartsWith("=ybegin ", StringComparison.Ordinal)) {
        filename = DecodeName(ExtractParam(line, "name")) ?? "unknown";
        var sizeStr = ExtractParam(line, "size");
        if (sizeStr != null) long.TryParse(sizeStr, out size);
        begun = true;
        break;
      }
    }

    if (!begun)
      throw new InvalidDataException("No =ybegin line: not a yEnc file.");

    // Skip =ypart if present
    var nextLine = reader.ReadLine();
    var isPart = false;
    if (nextLine != null && nextLine.StartsWith("=ypart ", StringComparison.Ordinal)) {
      nextLine = null; // skip, read next in loop
      isPart = true;
    }

    using var output = new MemoryStream();
    var startLine = nextLine;

    void ProcessLine(string l) {
      for (var i = 0; i < l.Length; i++) {
        if (l[i] == '=') {
          if (++i == l.Length)
            throw new InvalidDataException("yEnc escape character at the end of a line.");
          output.WriteByte((byte)((l[i] - 64 - 42) & 0xFF));
        } else {
          output.WriteByte((byte)((l[i] - 42) & 0xFF));
        }
      }
    }

    string? endLine = null;
    if (startLine != null) {
      if (startLine.StartsWith("=yend", StringComparison.Ordinal))
        endLine = startLine;
      else
        ProcessLine(startLine);
    }

    while (endLine == null && (line = reader.ReadLine()) != null) {
      if (line.StartsWith("=yend", StringComparison.Ordinal)) {
        endLine = line;
        break;
      }
      ProcessLine(line);
    }

    if (endLine == null)
      throw new InvalidDataException("No =yend line: the yEnc file is truncated.");

    var data = output.ToArray();
    var endSize = ExtractParam(endLine, "size");
    if (endSize != null && (!long.TryParse(endSize, out var expectedSize) || expectedSize != data.Length))
      throw new InvalidDataException($"yEnc =yend size {endSize} does not match the {data.Length} decoded bytes.");

    var actualCrc = Compression.Core.Checksums.Crc32.Compute(data);
    var partCrc = ParseCrc(ExtractParam(endLine, "pcrc32"));
    var fileCrc = ParseCrc(ExtractParam(endLine, "crc32"));
    if (partCrc is { } expectedPartCrc && expectedPartCrc != actualCrc)
      throw new InvalidDataException($"yEnc part CRC-32 mismatch: 0x{actualCrc:x8} != 0x{expectedPartCrc:x8}.");
    if (!isPart && partCrc == null && fileCrc is { } expectedFileCrc && expectedFileCrc != actualCrc)
      throw new InvalidDataException($"yEnc CRC-32 mismatch: 0x{actualCrc:x8} != 0x{expectedFileCrc:x8}.");

    return (filename, size, fileCrc ?? partCrc ?? actualCrc, data);
  }

  private static uint? ParseCrc(string? text) {
    if (text == null)
      return null;
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
      text = text[2..];
    return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value)
      ? value
      : throw new InvalidDataException($"yEnc CRC-32 '{text}' is not hexadecimal.");
  }

  /// <summary>Header names are UTF-8 in practice; keep the Latin-1 reading when they are not valid UTF-8.</summary>
  private static string? DecodeName(string? latin1) {
    if (latin1 == null) return null;
    try {
      return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
        .GetString(Encoding.Latin1.GetBytes(latin1));
    } catch (DecoderFallbackException) {
      return latin1;
    }
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
