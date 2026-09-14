#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Structured;

internal sealed record StructuredVirtualEntry(string Name, bool IsDirectory, string Kind, byte[] Data);

internal static class StructuredArchive {
  public static List<ArchiveEntryInfo> List(Stream stream, Func<Stream, StructuredNode> parse) {
    var entries = Project(parse(stream));
    return entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.IsDirectory ? 0 : entry.Data.LongLength,
      entry.IsDirectory ? 0 : entry.Data.LongLength, "stored", entry.IsDirectory,
      false, null, entry.Kind)).ToList();
  }

  public static void Extract(Stream stream, string outputDir, string[]? files, Func<Stream, StructuredNode> parse) {
    foreach (var entry in Project(parse(stream))) {
      if (entry.IsDirectory) continue;
      if (files is { Length: > 0 } && !FormatHelpers.MatchesFilter(entry.Name, files)) continue;
      FormatHelpers.WriteFile(outputDir, entry.Name, entry.Data);
    }
  }

  public static void ExtractEntry(Stream stream, string entryName, Stream output, Func<Stream, StructuredNode> parse) {
    var entry = Project(parse(stream)).FirstOrDefault(e => !e.IsDirectory && e.Name.Equals(entryName, StringComparison.Ordinal));
    if (entry is null) throw new FileNotFoundException($"Entry not found: {entryName}");
    output.Write(entry.Data);
  }

  public static StructuredNode FromInputs(IReadOnlyList<ArchiveInputInfo> inputs) {
    var root = StructuredNode.Object("archive");
    foreach (var input in inputs.OrderBy(x => x.ArchiveName, StringComparer.Ordinal)) {
      var parts = input.ArchiveName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) continue;
      var parent = root;
      for (var i = 0; i < parts.Length - 1; ++i) parent = GetOrAddDirectory(parent, parts[i]);
      if (input.IsDirectory) {
        _ = GetOrAddDirectory(parent, parts[^1]);
        continue;
      }
      if (parent.Members.Any(x => x.Key.Equals(parts[^1], StringComparison.Ordinal)))
        throw new InvalidDataException($"Structured archive input path collides with another entry: {input.ArchiveName}");
      parent.Add(parts[^1], StructuredNode.Binary(input.ReadContent(), "file"));
    }
    return root;
  }

  public static byte[] ReadAll(Stream stream, int maxBytes = 256 * 1024 * 1024) {
    if (stream.CanSeek && stream.Length - stream.Position > maxBytes)
      throw new InvalidDataException($"Structured document exceeds the {maxBytes:N0}-byte safety limit.");
    using var memory = new MemoryStream();
    var buffer = new byte[64 * 1024];
    var total = 0;
    while (true) {
      var read = stream.Read(buffer, 0, buffer.Length);
      if (read == 0) break;
      total = checked(total + read);
      if (total > maxBytes) throw new InvalidDataException($"Structured document exceeds the {maxBytes:N0}-byte safety limit.");
      memory.Write(buffer, 0, read);
    }
    return memory.ToArray();
  }

  public static string DecodeUtf8OrLatin1(ReadOnlySpan<byte> bytes) {
    try { return new UTF8Encoding(false, true).GetString(bytes); }
    catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
  }

  private static StructuredNode GetOrAddDirectory(StructuredNode parent, string name) {
    foreach (var member in parent.Members) {
      if (!member.Key.Equals(name, StringComparison.Ordinal)) continue;
      if (member.Value.Kind != StructuredNodeKind.Object)
        throw new InvalidDataException($"Structured archive path uses '{name}' as both a file and a directory.");
      return member.Value;
    }
    var directory = StructuredNode.Object("directory");
    parent.Add(name, directory);
    return directory;
  }

  private static List<StructuredVirtualEntry> Project(StructuredNode root) {
    var result = new List<StructuredVirtualEntry>();
    Walk(root, "", true, result, 0);
    return result;
  }

  private static void Walk(StructuredNode node, string path, bool isRoot, List<StructuredVirtualEntry> result, int depth) {
    if (depth > 256) throw new InvalidDataException("Structured object graph exceeds the 256-level safety limit.");
    if (node.Kind == StructuredNodeKind.Object) {
      if (!isRoot) result.Add(new(path, true, node.TypeName ?? "object", []));
      var totalNames = node.Members.GroupBy(x => EscapeSegment(x.Key), StringComparer.Ordinal)
        .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
      var seen = new Dictionary<string, int>(StringComparer.Ordinal);
      foreach (var member in node.Members) {
        var segment = EscapeSegment(member.Key);
        if (totalNames[segment] > 1) {
          var ordinal = seen.GetValueOrDefault(segment);
          seen[segment] = ordinal + 1;
          segment = $"{segment}~{ordinal:D6}";
        }
        Walk(member.Value, Join(path, segment), false, result, depth + 1);
      }
      return;
    }
    if (node.Kind == StructuredNodeKind.Array) {
      if (!isRoot) result.Add(new(path, true, node.TypeName ?? "array", []));
      for (var i = 0; i < node.Items.Count; ++i)
        Walk(node.Items[i], Join(path, $"[{i:D6}]"), false, result, depth + 1);
      return;
    }
    result.Add(new(string.IsNullOrEmpty(path) ? "value" : path, false, node.TypeName ?? KindName(node.Kind), node.Data));
  }

  private static string KindName(StructuredNodeKind kind) => kind switch {
    StructuredNodeKind.String => "string", StructuredNodeKind.Binary => "binary",
    StructuredNodeKind.Number => "number", StructuredNodeKind.Boolean => "boolean",
    StructuredNodeKind.Null => "null", StructuredNodeKind.Reference => "reference", _ => "value",
  };

  private static string Join(string parent, string child) => string.IsNullOrEmpty(parent) ? child : $"{parent}/{child}";

  private static string EscapeSegment(string value) {
    if (value.Length == 0) return "%00";
    var builder = new StringBuilder();
    foreach (var b in Encoding.UTF8.GetBytes(value)) {
      if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
          or (byte)'-' or (byte)'_' or (byte)'.')
        builder.Append((char)b);
      else
        builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
    }

    var result = builder.ToString();
    if (result is "." or "..") result = result.Replace(".", "%2E", StringComparison.Ordinal);
    else if (result.EndsWith('.')) result = result[..^1] + "%2E";

    if (IsWindowsDeviceName(result))
      result = $"%{(byte)result[0]:X2}{result[1..]}";
    return result;
  }

  private static bool IsWindowsDeviceName(string value) {
    var dot = value.IndexOf('.');
    var stem = value.AsSpan(0, dot >= 0 ? dot : value.Length);
    return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
      || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
      || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
      || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
      || stem.Length == 4 && (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
                             || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase))
                        && stem[3] is >= '1' and <= '9';
  }
}
