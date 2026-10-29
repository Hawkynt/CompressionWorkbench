#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.FirmwareCommon;

/// <summary>One member a firmware container exposes: a slice of the image or a rendering of it.</summary>
public sealed record FirmwareEntry(string Name, ReadOnlyMemory<byte> Data);

/// <summary>
/// The list / extract plumbing every firmware pseudo-archive shares: a firmware image is
/// parsed into an ordered set of named slices, and listing and extracting both walk that set.
/// </summary>
public static class FirmwareArchive {

  public const string MetadataName = "metadata.ini";

  public static byte[] ReadAll(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  public static List<ArchiveEntryInfo> List(IReadOnlyList<FirmwareEntry> entries)
    => entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.Length, e.Data.Length,
      "stored", false, false, null)).ToList();

  public static void Extract(IReadOnlyList<FirmwareEntry> entries, string outputDir, string[]? files) {
    foreach (var e in entries) {
      if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
      using var target = CreateEntryFile(outputDir, e.Name);
      target.Write(e.Data.Span);
    }
  }

  /// <summary>A NUL-terminated field as text: everything before the first NUL, Latin-1 so no byte is lost.</summary>
  public static string CString(ReadOnlySpan<byte> field) {
    var end = field.IndexOf((byte)0);
    return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
  }

  public static string Hex(ulong value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

  /// <summary>
  /// Parses the <c>key = value</c> lines of an INI rendering into section-qualified keys
  /// (<c>section.key</c>); keys before any section are unqualified. The value keeps its
  /// exact text after the single blank that follows the <c>=</c>.
  /// </summary>
  public static Dictionary<string, string> ParseIni(ReadOnlySpan<byte> ini) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var section = "";
    foreach (var raw in Encoding.UTF8.GetString(ini).Split('\n')) {
      var line = raw.TrimEnd('\r');
      var trimmed = line.Trim();
      if (trimmed.Length == 0 || trimmed[0] is ';' or '#') continue;
      if (trimmed[0] == '[' && trimmed[^1] == ']') {
        section = trimmed[1..^1].Trim();
        continue;
      }
      var eq = line.IndexOf('=');
      if (eq <= 0) continue;
      var key = line[..eq].Trim();
      var value = line[(eq + 1)..];
      if (value.StartsWith(' ')) value = value[1..];
      result[section.Length == 0 ? key : section + "." + key] = value;
    }
    return result;
  }

  public static bool TryParseNumber(string text, out ulong value) {
    text = text.Trim();
    var space = text.IndexOf(' ');
    if (space > 0) text = text[..space];
    return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      ? ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
      : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
  }
}
