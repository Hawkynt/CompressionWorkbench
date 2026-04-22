#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Warc;

public sealed class WarcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Warc";
  public string DisplayName => "WARC";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".warc";
  public IReadOnlyList<string> Extensions => [".warc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("WARC/"u8.ToArray(), Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("warc", "WARC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Web ARChive (ISO 28500)";

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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new WarcWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var data = File.ReadAllBytes(i.FullPath);
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
