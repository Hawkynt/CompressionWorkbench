#pragma warning disable CS1591
using System.Text;
using System.Xml.Linq;
using Compression.Registry;

namespace FileFormat.Fb2;

/// <summary>
/// FictionBook 2 eBook — single-file XML. The archive view exposes the original
/// XML under <c>FULL.fb2</c>, per-<c>section</c> entries as <c>chapter_NN.xml</c>,
/// each <c>binary</c> element (base64-encoded embedded image) decoded to its
/// content-type-derived extension under <c>images/</c>, plus <c>metadata.ini</c>
/// carrying title/author/date from the <c>description</c>/<c>title-info</c> block.
/// </summary>
public sealed class Fb2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Fb2";
  public string DisplayName => "FB2 (FictionBook)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".fb2";
  public IReadOnlyList<string> Extensions => [".fb2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // FB2 is XML without a fixed BOM/pre-amble; resolve by extension only.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "FictionBook 2 eBook; chapters and embedded images surface as entries.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string, string, byte[])> {
      ("FULL.fb2", "Track", blob),
    };

    XDocument doc;
    try { doc = XDocument.Parse(Encoding.UTF8.GetString(blob)); }
    catch { return entries; }

    var root = doc.Root;
    if (root == null) return entries;
    var ns = root.Name.Namespace;

    // Sections (chapters): any <section> directly under <body>.
    var chapterIdx = 0;
    foreach (var body in root.Elements(ns + "body")) {
      foreach (var section in body.Elements(ns + "section")) {
        var name = $"chapter_{chapterIdx:D3}.xml";
        entries.Add((name, "Track", Encoding.UTF8.GetBytes(section.ToString())));
        ++chapterIdx;
      }
    }

    // Embedded binaries.
    foreach (var bin in root.Elements(ns + "binary")) {
      var id = bin.Attribute("id")?.Value ?? $"image_{entries.Count}";
      var contentType = bin.Attribute("content-type")?.Value ?? "application/octet-stream";
      try {
        var data = Convert.FromBase64String(bin.Value);
        var ext = ContentTypeExtension(contentType);
        entries.Add(($"images/{SanitizeForPath(id)}{ext}", "Tag", data));
      } catch (FormatException) { /* skip malformed base64 */ }
    }

    // Metadata from title-info.
    var titleInfo = root.Element(ns + "description")?.Element(ns + "title-info");
    if (titleInfo != null) {
      var ini = new StringBuilder();
      ini.AppendLine("; FB2 title-info");
      var title = titleInfo.Element(ns + "book-title")?.Value;
      if (!string.IsNullOrWhiteSpace(title)) ini.Append("title=").AppendLine(title.Trim());
      foreach (var author in titleInfo.Elements(ns + "author")) {
        var first = author.Element(ns + "first-name")?.Value ?? "";
        var last = author.Element(ns + "last-name")?.Value ?? "";
        ini.Append("author=").AppendLine($"{first} {last}".Trim());
      }
      var lang = titleInfo.Element(ns + "lang")?.Value;
      if (!string.IsNullOrWhiteSpace(lang)) ini.Append("language=").AppendLine(lang.Trim());
      var date = titleInfo.Element(ns + "date")?.Value;
      if (!string.IsNullOrWhiteSpace(date)) ini.Append("date=").AppendLine(date.Trim());
      entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));
    }

    return entries;
  }

  private static string ContentTypeExtension(string ct) => ct.ToLowerInvariant() switch {
    "image/jpeg" or "image/jpg" => ".jpg",
    "image/png" => ".png",
    "image/gif" => ".gif",
    "image/webp" => ".webp",
    "image/svg+xml" => ".svg",
    _ => ".bin",
  };

  private static string SanitizeForPath(string s) {
    var sb = new StringBuilder(Math.Min(s.Length, 60));
    foreach (var c in s) {
      if (sb.Length >= 60) break;
      if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.') sb.Append(c);
      else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
    }
    return sb.Length > 0 ? sb.ToString().Trim('_') : "binary";
  }
}
