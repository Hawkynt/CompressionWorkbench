#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Warc;

/// <summary>
/// Descriptor for WARC (Web ARChive, ISO 28500) files — the record-oriented
/// container web crawlers use to store captured HTTP transactions and metadata.
///
/// References:
/// <list type="bullet">
///   <item><description>ISO 28500:2017 "WARC file format" — the defining standard</description></item>
///   <item><description><c>https://iipc.github.io/warc-specifications/</c> — IIPC-maintained WARC specifications and proposals</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/WARC_(file_format)</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class WarcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the WARC archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the WARC archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new WarcReader(stream, leaveOpen: true);
        var all = r.ReadAll();
        var list = new List<(string, byte[])>();
        for (var i = 0; i < all.Count; ++i) {
          var (entry, payload) = all[i];
          var name = string.IsNullOrEmpty(entry.TargetUri)
            ? (string.IsNullOrEmpty(entry.RecordId) ? $"record-{i}" : entry.RecordId)
            : entry.TargetUri;
          list.Add((name, payload));
        }
        return list;
      },
      buildImage: files => {
        var w = new WarcWriter();
        foreach (var (n, d) in files) w.AddResource(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new WarcReader(archive);
    foreach (var (entry, _) in r.ReadAll()) {
      if (entry.PayloadOffset >= 0 && entry.ContentLength > 0)
        yield return new DefragBlockInfo(entry.PayloadOffset, entry.ContentLength, DefragBlockKind.Used, FileName: entry.TargetUri ?? entry.RecordId);
    }
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Warc";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "WARC";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".warc";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".warc"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("WARC/"u8.ToArray(), Confidence: 0.90)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("warc", "WARC")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Web ARChive (ISO 28500)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new WarcReader(stream, leaveOpen: true);
    var all = r.ReadAll();
    var result = new List<ArchiveEntryInfo>(all.Count);
    for (var i = 0; i < all.Count; i++) {
      var (entry, _) = all[i];
      var name = EntryDisplayName(entry, i);
      DateTime? lastMod = entry.Date != null &&
        DateTime.TryParse(entry.Date, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
        ? dt : null;
      result.Add(new ArchiveEntryInfo(i, name, entry.ContentLength, entry.ContentLength,
        "warc", false, false, lastMod));
    }
    return result;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new WarcReader(stream, leaveOpen: true);
    var index = 0;
    while (r.ReadNext() is { } pair) {
      var (entry, payload) = pair;
      var name = EntryFileName(entry, index);
      if (files == null || MatchesFilter(name, files))
        WriteFile(outputDir, name, payload);
      index++;
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new WarcWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var data = i.ReadContent();
      // Use the archive name as the WARC-Target-URI so the extractor can
      // reconstruct it (SanitizeUri keeps slashes/dots/dashes/alphanumeric).
      w.AddResource(i.ArchiveName, data);
    }
    w.WriteTo(output);
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  /// <summary>Human-readable listing name: "type: uri" or "type: record-id".</summary>
  private static string EntryDisplayName(WarcEntry entry, int index) {
    var label = string.IsNullOrEmpty(entry.TargetUri)
      ? (string.IsNullOrEmpty(entry.RecordId) ? $"record-{index}" : entry.RecordId)
      : entry.TargetUri;
    return string.IsNullOrEmpty(entry.Type) ? label : $"{entry.Type}: {label}";
  }

  /// <summary>
  /// Safe filename for extraction.  Derives a path from the URI when available,
  /// otherwise falls back to "record-{index}".
  /// </summary>
  private static string EntryFileName(WarcEntry entry, int index) {
    if (!string.IsNullOrEmpty(entry.TargetUri)) {
      var sanitized = SanitizeUri(entry.TargetUri);
      if (!string.IsNullOrEmpty(sanitized))
        return sanitized;
    }
    return $"record-{index:D4}";
  }

  private static string SanitizeUri(string uri) {
    // Strip scheme (e.g. "https://")
    var noScheme = uri;
    var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
    if (schemeEnd >= 0)
      noScheme = uri[(schemeEnd + 3)..];

    // Strip query and fragment
    var q = noScheme.IndexOf('?');
    if (q >= 0) noScheme = noScheme[..q];
    var f = noScheme.IndexOf('#');
    if (f >= 0) noScheme = noScheme[..f];

    // Replace characters that are invalid in path segments
    var sb = new StringBuilder(noScheme.Length);
    foreach (var c in noScheme) {
      if (c == '/' || c == '.' || c == '-' || c == '_' || (c >= 'a' && c <= 'z') ||
          (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
        sb.Append(c);
      else
        sb.Append('_');
    }

    var result = sb.ToString().Trim('/').TrimStart('.');
    // Remove path-traversal sequences
    result = result.Replace("..", "_");
    return result;
  }
}
