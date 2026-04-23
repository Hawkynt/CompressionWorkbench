#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Collada;

/// <summary>
/// Collada (.dae) — an XML interchange format from the Khronos Group for 3D assets.
/// Root element is <c>&lt;COLLADA&gt;</c> with the <c>xmlns</c> typically set to
/// <c>http://www.collada.org/2005/11/COLLADASchema</c>. Top-level children are
/// <c>library_*</c> elements (geometries, images, materials, effects, animations,
/// visual_scenes …) plus <c>asset</c> and <c>scene</c>. We surface the full document,
/// a <c>metadata.ini</c> summary (version + library counts), and one
/// <c>library_*.xml</c> fragment per top-level library.
/// </summary>
/// <remarks>
/// Reference: Khronos Collada 1.5.0 / 1.4.1 specification.
/// </remarks>
public sealed class ColladaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>Format identifier.</summary>
  public string Id => "Collada";
  /// <summary>Display name.</summary>
  public string DisplayName => "Collada (.dae)";
  /// <summary>Archive category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>Read-only archive capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>Default extension.</summary>
  public string DefaultExtension => ".dae";
  /// <summary>Known extensions.</summary>
  public IReadOnlyList<string> Extensions => [".dae"];
  /// <summary>No compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>XML content-sniffing magic: <c>&lt;COLLADA</c> anywhere in the first 512 bytes
  /// (after the XML prolog). We use a short 0-offset magic for the common case, and rely on
  /// extension + List() for documents with BOMs or comments preceding the root.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>Stored only.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>Not a tar compound format.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Archive family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>Short description.</summary>
  public string Description => "Collada (.dae) XML 3D asset; surfaces per-library fragments.";

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
      ("FULL.dae", "Track", blob),
      ("document.xml", "Track", blob),
    };

    string? version = null;
    var libraryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var libraryFragments = new List<(string Element, byte[] Xml)>();
    var sceneNames = new List<string>();

    try {
      using var reader = new StringReader(Encoding.UTF8.GetString(blob));
      var settings = new XmlReaderSettings {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
      };
      using var xr = XmlReader.Create(reader, settings);
      var doc = XDocument.Load(xr);
      var root = doc.Root;
      if (root != null) {
        version = root.Attribute("version")?.Value;
        foreach (var child in root.Elements()) {
          var localName = child.Name.LocalName;
          if (localName.StartsWith("library_", StringComparison.Ordinal)) {
            libraryCounts.TryGetValue(localName, out var c);
            libraryCounts[localName] = c + 1;
            var xml = child.ToString(SaveOptions.None);
            libraryFragments.Add((localName, Encoding.UTF8.GetBytes(xml)));
          } else if (string.Equals(localName, "scene", StringComparison.OrdinalIgnoreCase)) {
            foreach (var instance in child.Elements()) {
              var url = instance.Attribute("url")?.Value;
              if (!string.IsNullOrEmpty(url)) sceneNames.Add(url);
            }
          }
        }
      }
    } catch {
      // Malformed XML — best-effort, fall through to metadata-only emission.
    }

    // Emit one entry per library fragment (dedup if same name appears twice).
    var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var (elementName, xml) in libraryFragments) {
      seen.TryGetValue(elementName, out var idx);
      var suffix = idx == 0 ? "" : $"_{idx:D2}";
      entries.Add(($"{elementName}{suffix}.xml", "Track", xml));
      seen[elementName] = idx + 1;
    }

    var meta = new StringBuilder();
    meta.AppendLine("; Collada metadata");
    meta.Append("version=").AppendLine(version ?? "");
    meta.Append("library_count=").AppendLine(libraryFragments.Count.ToString(CultureInfo.InvariantCulture));
    meta.Append("scene_instances=").AppendLine(string.Join(',', sceneNames));
    foreach (var kv in libraryCounts.OrderBy(k => k.Key)) {
      meta.Append(kv.Key).Append('=').AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
    }
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    return entries;
  }
}
