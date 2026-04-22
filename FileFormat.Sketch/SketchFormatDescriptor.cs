#pragma warning disable CS1591
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Sketch;

/// <summary>
/// Sketch design files (.sketch). A ZIP container holding document.json,
/// meta.json, user.json, pages/*.json, previews/preview.png and embedded
/// image/text-preview assets. Read-only descriptor surfacing the canonical
/// parts plus a derived metadata.ini.
/// </summary>
public sealed class SketchFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Sketch";
  public string DisplayName => "Sketch";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".sketch";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [".sketch"];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Sketch design document (ZIP-based)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var result = new List<ArchiveEntryInfo>();
    try {
      var view = BuildView(stream);
      var idx = 0;
      result.Add(new ArchiveEntryInfo(idx++, "FULL.sketch", view.TotalBytes, view.TotalBytes, "Stored", false, false, null, Kind: "Passthrough"));
      result.Add(new ArchiveEntryInfo(idx++, "metadata.ini", view.MetadataIni.Length, view.MetadataIni.Length, "Stored", false, false, null, Kind: "Metadata"));

      if (view.DocumentJson is { } docJson)
        result.Add(new ArchiveEntryInfo(idx++, "document.json", docJson.Length, docJson.Length, "Stored", false, false, view.DocumentJsonModified, Kind: "Document"));
      if (view.MetaJson is { } metaJson)
        result.Add(new ArchiveEntryInfo(idx++, "meta.json", metaJson.Length, metaJson.Length, "Stored", false, false, view.MetaJsonModified, Kind: "Metadata"));
      if (view.UserJson is { } userJson)
        result.Add(new ArchiveEntryInfo(idx++, "user.json", userJson.Length, userJson.Length, "Stored", false, false, view.UserJsonModified, Kind: "UserPrefs"));

      foreach (var (name, size, modified) in view.Pages)
        result.Add(new ArchiveEntryInfo(idx++, "pages/" + name, size, size, "Stored", false, false, modified, Kind: "Page"));
      foreach (var (name, size, modified) in view.Previews)
        result.Add(new ArchiveEntryInfo(idx++, "previews/" + name, size, size, "Stored", false, false, modified, Kind: "Preview"));
      foreach (var (name, size, modified) in view.Images)
        result.Add(new ArchiveEntryInfo(idx++, "images/" + name, size, size, "Stored", false, false, modified, Kind: "Image"));
      foreach (var (name, size, modified) in view.Other)
        result.Add(new ArchiveEntryInfo(idx++, "other/" + name, size, size, "Stored", false, false, modified, Kind: "Other"));
    } catch {
      // Robust: never throw. Empty or partial list on parse failure.
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var view = BuildView(stream);

    if (files == null || MatchesFilter("FULL.sketch", files))
      WriteFile(outputDir, "FULL.sketch", view.FullBytes);
    if (files == null || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", view.MetadataIni);
    if (view.DocumentJson is { } docJson && (files == null || MatchesFilter("document.json", files)))
      WriteFile(outputDir, "document.json", docJson);
    if (view.MetaJson is { } metaJson && (files == null || MatchesFilter("meta.json", files)))
      WriteFile(outputDir, "meta.json", metaJson);
    if (view.UserJson is { } userJson && (files == null || MatchesFilter("user.json", files)))
      WriteFile(outputDir, "user.json", userJson);

    foreach (var (name, data) in view.PageData)
      if (files == null || MatchesFilter("pages/" + name, files))
        WriteFile(outputDir, "pages/" + name, data);
    foreach (var (name, data) in view.PreviewData)
      if (files == null || MatchesFilter("previews/" + name, files))
        WriteFile(outputDir, "previews/" + name, data);
    foreach (var (name, data) in view.ImageData)
      if (files == null || MatchesFilter("images/" + name, files))
        WriteFile(outputDir, "images/" + name, data);
    foreach (var (name, data) in view.OtherData)
      if (files == null || MatchesFilter("other/" + name, files))
        WriteFile(outputDir, "other/" + name, data);
  }

  private static SketchView BuildView(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var fullBytes = ms.ToArray();

    byte[]? documentJson = null, metaJson = null, userJson = null;
    DateTime? documentJsonModified = null, metaJsonModified = null, userJsonModified = null;
    var pages = new List<(string Name, long Size, DateTime? Modified)>();
    var previews = new List<(string Name, long Size, DateTime? Modified)>();
    var images = new List<(string Name, long Size, DateTime? Modified)>();
    var other = new List<(string Name, long Size, DateTime? Modified)>();
    var pageData = new List<(string Name, byte[] Data)>();
    var previewData = new List<(string Name, byte[] Data)>();
    var imageData = new List<(string Name, byte[] Data)>();
    var otherData = new List<(string Name, byte[] Data)>();

    int totalEntries = 0;
    bool hasPreview = false;
    bool hasDocumentJson = false;
    string? appVersion = null;

    using (var zipStream = new MemoryStream(fullBytes)) {
      using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);
      foreach (var e in archive.Entries) {
        totalEntries++;
        var name = e.FullName.Replace('\\', '/');
        if (name.EndsWith('/') || e.Length == 0 && string.IsNullOrEmpty(e.Name)) continue;

        var data = ReadEntry(e);
        var lm = e.LastWriteTime == DateTimeOffset.MinValue ? (DateTime?)null : e.LastWriteTime.UtcDateTime;

        if (name.Equals("document.json", StringComparison.OrdinalIgnoreCase)) {
          documentJson = data; documentJsonModified = lm; hasDocumentJson = true;
        } else if (name.Equals("meta.json", StringComparison.OrdinalIgnoreCase)) {
          metaJson = data; metaJsonModified = lm;
          appVersion = TryExtractJsonString(data, "appVersion");
        } else if (name.Equals("user.json", StringComparison.OrdinalIgnoreCase)) {
          userJson = data; userJsonModified = lm;
        } else if (name.StartsWith("pages/", StringComparison.OrdinalIgnoreCase)) {
          var leaf = name["pages/".Length..];
          pages.Add((leaf, data.LongLength, lm));
          pageData.Add((leaf, data));
        } else if (name.StartsWith("previews/", StringComparison.OrdinalIgnoreCase)) {
          var leaf = name["previews/".Length..];
          previews.Add((leaf, data.LongLength, lm));
          previewData.Add((leaf, data));
          if (leaf.Equals("preview.png", StringComparison.OrdinalIgnoreCase)) hasPreview = true;
        } else if (name.StartsWith("images/", StringComparison.OrdinalIgnoreCase)) {
          var leaf = name["images/".Length..];
          images.Add((leaf, data.LongLength, lm));
          imageData.Add((leaf, data));
        } else {
          other.Add((name, data.LongLength, lm));
          otherData.Add((name, data));
        }
      }
    }

    var sb = new StringBuilder();
    sb.AppendLine("[sketch]");
    sb.AppendLine($"page_count={pages.Count}");
    sb.AppendLine($"has_preview={(hasPreview ? "true" : "false")}");
    sb.AppendLine($"has_document_json={(hasDocumentJson ? "true" : "false")}");
    sb.AppendLine($"total_entries={totalEntries}");
    if (appVersion != null)
      sb.AppendLine($"app_version={appVersion}");
    var metadataIni = Encoding.UTF8.GetBytes(sb.ToString());

    return new SketchView(
      fullBytes, fullBytes.LongLength, metadataIni,
      documentJson, documentJsonModified,
      metaJson, metaJsonModified,
      userJson, userJsonModified,
      pages, pageData,
      previews, previewData,
      images, imageData,
      other, otherData
    );
  }

  private static byte[] ReadEntry(ZipArchiveEntry e) {
    using var s = e.Open();
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Cheap-and-safe "find string value for key in JSON". Does NOT parse JSON —
  /// only used for surfacing app_version in metadata.ini on a best-effort basis.
  /// Returns null if the key is not found or can't be extracted cleanly.
  /// </summary>
  private static string? TryExtractJsonString(byte[] json, string key) {
    try {
      var text = Encoding.UTF8.GetString(json);
      var needle = "\"" + key + "\"";
      var i = text.IndexOf(needle, StringComparison.Ordinal);
      if (i < 0) return null;
      i += needle.Length;
      while (i < text.Length && (text[i] == ' ' || text[i] == ':' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n')) i++;
      if (i >= text.Length || text[i] != '"') return null;
      i++;
      var start = i;
      while (i < text.Length && text[i] != '"') {
        if (text[i] == '\\' && i + 1 < text.Length) i += 2;
        else i++;
      }
      if (i >= text.Length) return null;
      return text[start..i];
    } catch {
      return null;
    }
  }

  private sealed record SketchView(
    byte[] FullBytes,
    long TotalBytes,
    byte[] MetadataIni,
    byte[]? DocumentJson, DateTime? DocumentJsonModified,
    byte[]? MetaJson, DateTime? MetaJsonModified,
    byte[]? UserJson, DateTime? UserJsonModified,
    List<(string Name, long Size, DateTime? Modified)> Pages,
    List<(string Name, byte[] Data)> PageData,
    List<(string Name, long Size, DateTime? Modified)> Previews,
    List<(string Name, byte[] Data)> PreviewData,
    List<(string Name, long Size, DateTime? Modified)> Images,
    List<(string Name, byte[] Data)> ImageData,
    List<(string Name, long Size, DateTime? Modified)> Other,
    List<(string Name, byte[] Data)> OtherData
  );
}
