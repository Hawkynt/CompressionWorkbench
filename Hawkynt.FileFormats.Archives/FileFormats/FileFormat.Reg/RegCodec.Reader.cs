#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Reg;

internal static partial class RegCodec {
  public static StructuredNode Read(Stream stream) {
    var bytes = StructuredArchive.ReadAll(stream);
    var text = Decode(bytes);
    var logicalLines = LogicalLines(text).ToArray();
    var first = logicalLines.FirstOrDefault(line => line.Length != 0 && !line.StartsWith(';'));
    if (first is not "Windows Registry Editor Version 5.00" and not "REGEDIT4")
      throw new InvalidDataException("Not a Windows Registry export file.");

    var root = StructuredNode.Object("registry");
    StructuredNode? current = null;
    foreach (var raw in logicalLines) {
      var line = raw.Trim();
      if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line == first) continue;
      if (line.StartsWith('[')) {
        if (!line.EndsWith(']')) throw new InvalidDataException("Malformed registry key header.");
        var path = line[1..^1];
        if (path.StartsWith('-')) path = path[1..];
        current = EnsurePath(root, path.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        continue;
      }
      if (current is null) throw new InvalidDataException("Registry value appears before the first key header.");
      var equals = FindEquals(line);
      if (equals < 0) throw new InvalidDataException($"Malformed registry value line: {line}");
      var nameToken = line[..equals].Trim();
      var dataToken = line[(equals + 1)..].Trim();
      var name = nameToken == "@" ? "@" : ParseQuoted(nameToken);
      current.Add(name, ParseValue(dataToken));
    }
    return root;
  }

  private static StructuredNode ParseValue(string token) {
    if (token == "-") return StructuredNode.Null("REG_DELETE");
    if (token.StartsWith('"')) return StructuredNode.Text(StructuredNodeKind.String, ParseQuoted(token), "REG_SZ");
    if (token.StartsWith("dword:", StringComparison.OrdinalIgnoreCase)) {
      var hex = token[6..].Trim();
      if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        throw new InvalidDataException("Invalid REG_DWORD value.");
      return StructuredNode.Text(StructuredNodeKind.Number, $"0x{value:X8}", "REG_DWORD");
    }
    if (!token.StartsWith("hex", StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException($"Unsupported registry value encoding '{token}'.");

    var colon = token.IndexOf(':');
    if (colon < 0) throw new InvalidDataException("Malformed registry hex value.");
    var kind = token[..colon].ToLowerInvariant();
    var data = ParseHex(token[(colon + 1)..]);
    return kind switch {
      "hex" => StructuredNode.Binary(data, "REG_BINARY"),
      "hex(2)" => StructuredNode.Text(StructuredNodeKind.String, DecodeUtf16String(data), "REG_EXPAND_SZ"),
      "hex(7)" => StructuredNode.Text(StructuredNodeKind.String, DecodeMultiString(data), "REG_MULTI_SZ"),
      "hex(b)" when data.Length == 8 => StructuredNode.Text(StructuredNodeKind.Number,
        $"0x{BinaryPrimitives.ReadUInt64LittleEndian(data):X16}", "REG_QWORD"),
      _ => StructuredNode.Binary(data, kind.ToUpperInvariant()),
    };
  }

  private static StructuredNode EnsurePath(StructuredNode root, IEnumerable<string> parts) {
    var current = root;
    foreach (var part in parts) {
      var next = current.Members.FirstOrDefault(x => x.Key.Equals(part, StringComparison.OrdinalIgnoreCase)).Value;
      if (next is null) {
        next = StructuredNode.Object("registry-key");
        current.Add(part, next);
      } else if (next.Kind != StructuredNodeKind.Object) {
        throw new InvalidDataException($"Registry key '{part}' collides with a value.");
      }
      current = next;
    }
    return current;
  }

  private static IEnumerable<string> LogicalLines(string text) {
    using var reader = new StringReader(text);
    string? line;
    while ((line = reader.ReadLine()) is not null) {
      var builder = new StringBuilder(line);
      while (builder.ToString().TrimEnd().EndsWith('\\')) {
        var trimmed = builder.ToString().TrimEnd();
        builder.Clear().Append(trimmed[..^1]);
        var continuation = reader.ReadLine() ?? throw new InvalidDataException("Registry hex continuation is truncated.");
        builder.Append(continuation.TrimStart());
      }
      yield return builder.ToString();
    }
  }

  private static int FindEquals(string line) {
    var quoted = false;
    var escaped = false;
    for (var i = 0; i < line.Length; ++i) {
      var c = line[i];
      if (escaped) { escaped = false; continue; }
      if (c == '\\') { escaped = true; continue; }
      if (c == '"') quoted = !quoted;
      else if (c == '=' && !quoted) return i;
    }
    return -1;
  }

  private static string ParseQuoted(string token) {
    if (token.Length < 2 || token[0] != '"' || token[^1] != '"') throw new InvalidDataException("Malformed quoted registry string.");
    var builder = new StringBuilder(token.Length - 2);
    for (var i = 1; i < token.Length - 1; ++i) {
      var c = token[i];
      if (c != '\\') { builder.Append(c); continue; }
      if (++i >= token.Length - 1) throw new InvalidDataException("Truncated registry string escape.");
      builder.Append(token[i]);
    }
    return builder.ToString();
  }

  private static byte[] ParseHex(string value) {
    var tokens = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var result = new byte[tokens.Length];
    for (var i = 0; i < tokens.Length; ++i)
      if (!byte.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
        throw new InvalidDataException($"Invalid registry hex byte '{tokens[i]}'.");
    return result;
  }

  private static string DecodeUtf16String(byte[] data) {
    if ((data.Length & 1) != 0) throw new InvalidDataException("Registry UTF-16 value has an odd byte length.");
    return Encoding.Unicode.GetString(data).TrimEnd('\0');
  }

  private static string DecodeMultiString(byte[] data)
    => string.Join('\n', DecodeUtf16String(data).Split('\0', StringSplitOptions.RemoveEmptyEntries));

  private static string Decode(byte[] data) {
    if (data is [0xff, 0xfe, ..]) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
    if (data is [0xfe, 0xff, ..]) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
    if (data is [0xef, 0xbb, 0xbf, ..]) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
    return Encoding.Latin1.GetString(data);
  }
}
