#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dxf;

/// <summary>
/// AutoCAD Drawing Exchange Format (ASCII variant). Content is a stream of
/// group-code / value pairs on alternating lines: the numeric group code (e.g. 0
/// for an entity type, 2 for a name, 10 for an X coordinate) on one line and its
/// value on the next. The document is divided into sections bracketed by
/// <c>0/SECTION</c> … <c>0/ENDSEC</c> pairs; known sections are <c>HEADER</c>,
/// <c>CLASSES</c>, <c>TABLES</c>, <c>BLOCKS</c>, <c>ENTITIES</c>, <c>OBJECTS</c>,
/// and the document terminates with <c>0/EOF</c>. Binary DXF is proprietary and
/// ignored here.
/// </summary>
public sealed class DxfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>Format identifier.</summary>
  public string Id => "Dxf";
  /// <summary>Display name.</summary>
  public string DisplayName => "DXF (AutoCAD Drawing Exchange)";
  /// <summary>Archive category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>Read-only archive capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>Default extension.</summary>
  public string DefaultExtension => ".dxf";
  /// <summary>Known extensions.</summary>
  public IReadOnlyList<string> Extensions => [".dxf"];
  /// <summary>No compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>No reliable binary magic; ASCII DXF starts with a group code (typically "  0")
  /// and is extension-primary.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>Stored only.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>Not a tar compound format.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Archive family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>Short description.</summary>
  public string Description => "AutoCAD DXF (ASCII); per-section slices + entity histogram.";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <inheritdoc />
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.dxf", "Track", blob),
    };

    var text = Encoding.ASCII.GetString(blob);
    var lines = text.Replace("\r\n", "\n").Split('\n');

    var sections = new List<(string Name, List<string> Lines)>();
    var groupCodeCounts = new Dictionary<int, int>();
    var entityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    List<string>? currentSection = null;
    string currentSectionName = "";
    var inSection = false;
    var expectingSectionName = false;
    var prevWasEntityMarker = false; // true after "  0" then we look for entity type on next line

    // We walk paired lines: code line + value line.
    for (var i = 0; i + 1 < lines.Length; i += 2) {
      var codeLine = lines[i].Trim();
      var valueLine = lines[i + 1]; // Preserve value line whitespace as-is
      if (!int.TryParse(codeLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) {
        // Desynced — try to resync to next integer-looking line
        i--;
        continue;
      }

      groupCodeCounts.TryGetValue(code, out var existing);
      groupCodeCounts[code] = existing + 1;

      var value = valueLine.Trim();

      if (code == 0) {
        if (value.Equals("SECTION", StringComparison.OrdinalIgnoreCase)) {
          // Next iteration provides 2/name for section name.
          expectingSectionName = true;
          inSection = true;
          currentSection = [];
          currentSectionName = "";
        } else if (value.Equals("ENDSEC", StringComparison.OrdinalIgnoreCase)) {
          if (currentSection != null) sections.Add((currentSectionName, currentSection));
          currentSection = null;
          inSection = false;
          expectingSectionName = false;
        } else if (value.Equals("EOF", StringComparison.OrdinalIgnoreCase)) {
          // End of document.
        } else if (inSection && currentSectionName.Equals("ENTITIES", StringComparison.OrdinalIgnoreCase)) {
          // Entity record inside ENTITIES section.
          if (!string.IsNullOrEmpty(value)) {
            entityCounts.TryGetValue(value, out var ec);
            entityCounts[value] = ec + 1;
          }
          prevWasEntityMarker = true;
        }
      } else if (code == 2 && expectingSectionName) {
        currentSectionName = value;
        expectingSectionName = false;
      }

      // Record both lines in the current section (if any).
      if (currentSection != null) {
        currentSection.Add(lines[i]);
        currentSection.Add(lines[i + 1]);
      }
      _ = prevWasEntityMarker;
    }

    // Emit per-section entries.
    foreach (var s in sections) {
      var content = string.Join('\n', s.Lines) + "\n";
      var safe = string.IsNullOrWhiteSpace(s.Name) ? "section" : Sanitize(s.Name);
      entries.Add(($"section_{safe}.txt", "Track", Encoding.ASCII.GetBytes(content)));
    }

    var meta = new StringBuilder();
    meta.AppendLine("; DXF metadata");
    meta.Append("line_count=").AppendLine(lines.Length.ToString(CultureInfo.InvariantCulture));
    meta.Append("section_count=").AppendLine(sections.Count.ToString(CultureInfo.InvariantCulture));
    meta.Append("sections=").AppendLine(string.Join(',', sections.Select(s => s.Name)));
    meta.AppendLine();
    meta.AppendLine("; Group code histogram");
    foreach (var kv in groupCodeCounts.OrderBy(k => k.Key)) {
      meta.Append("group_").Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append('=').AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
    }
    if (entityCounts.Count > 0) {
      meta.AppendLine();
      meta.AppendLine("; Entity histogram");
      foreach (var kv in entityCounts.OrderByDescending(k => k.Value)) {
        meta.Append("entity_").Append(kv.Key).Append('=').AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
      }
    }
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    return entries;
  }

  private static string Sanitize(string s) {
    var sb = new StringBuilder(Math.Min(s.Length, 40));
    foreach (var c in s) {
      if (sb.Length >= 40) break;
      if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
      else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
    }
    return sb.Length > 0 ? sb.ToString().Trim('_') : "section";
  }
}
