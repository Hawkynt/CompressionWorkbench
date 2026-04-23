using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.Zip;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Wacz;

/// <summary>
/// Descriptor for the <b>WACZ</b> (Web Archive Collection Zipped) format — a ZIP
/// container that wraps one or more WARC files together with a Frictionless-Data
/// manifest, page index and optional resource bundles.
/// </summary>
/// <remarks>
/// <para>
/// A valid WACZ contains at minimum:
/// </para>
/// <list type="bullet">
///   <item><c>datapackage.json</c> at the root (Frictionless Data manifest).</item>
///   <item>An <c>archive/</c> directory holding one or more <c>*.warc.gz</c> files.</item>
///   <item>Usually a <c>pages/pages.jsonl</c> index of crawled pages.</item>
/// </list>
/// <para>
/// Detection is by extension only because the outer container is a plain ZIP whose
/// magic is shared with countless other formats — declaring a magic signature here
/// would shadow the regular ZIP descriptor. The descriptor wraps
/// <see cref="ZipReader"/> for the underlying container and adds a parsed metadata
/// summary entry that surfaces the <c>datapackage.json</c> fields most callers care
/// about (title, version, software, page count, archive count).
/// </para>
/// <para>
/// Reference: <see href="https://specs.webrecorder.net/wacz/1.1.1/"/>.
/// </para>
/// </remarks>
public sealed class WaczFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc/>
  public string Id => "Wacz";

  /// <inheritdoc/>
  public string DisplayName => "WACZ";

  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc/>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <inheritdoc/>
  public string DefaultExtension => ".wacz";

  /// <inheritdoc/>
  public IReadOnlyList<string> Extensions => [".wacz"];

  /// <inheritdoc/>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc/>
  /// <remarks>Empty: outer container is ZIP; detection is by extension to avoid
  /// shadowing the ZIP descriptor.</remarks>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];

  /// <inheritdoc/>
  public string? TarCompressionFormatId => null;

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc/>
  public string Description =>
    "Web Archive Collection Zipped — ZIP container around WARC files with " +
    "datapackage.json metadata and a page index.";

  /// <inheritdoc/>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var zip = new ZipReader(stream, leaveOpen: true, password: password);
    EnsureLooksLikeWacz(zip);

    var result = new List<ArchiveEntryInfo>();
    var datapackage = TryReadEntry(zip, "datapackage.json");
    var pages = TryReadEntry(zip, "pages/pages.jsonl");
    result.Add(new(0, "metadata.ini", 0, 0, "Tag", false, false, null,
      Kind: "Tag"));
    var idx = 1;
    foreach (var e in zip.Entries) {
      result.Add(new(idx++, e.FileName, e.UncompressedSize, e.CompressedSize,
        e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified));
    }

    // Backfill metadata.ini size now that we know what we're going to write.
    var meta = BuildMetadata(zip, datapackage, pages);
    result[0] = result[0] with { OriginalSize = meta.Length, CompressedSize = meta.Length };
    return result;
  }

  /// <inheritdoc/>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var zip = new ZipReader(stream, leaveOpen: true, password: password);
    EnsureLooksLikeWacz(zip);

    var datapackage = TryReadEntry(zip, "datapackage.json");
    var pages = TryReadEntry(zip, "pages/pages.jsonl");
    var meta = BuildMetadata(zip, datapackage, pages);

    if (files == null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", meta);

    foreach (var e in zip.Entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.FileName, files))
        continue;
      if (e.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, e.FileName));
        continue;
      }
      WriteFile(outputDir, e.FileName, zip.ExtractEntry(e));
    }
  }

  /// <summary>
  /// Throws <see cref="InvalidDataException"/> unless the ZIP root looks like a WACZ
  /// (must contain <c>datapackage.json</c> and an <c>archive/</c> directory).
  /// </summary>
  private static void EnsureLooksLikeWacz(ZipReader zip) {
    var hasDataPackage = false;
    var hasArchiveDir = false;
    foreach (var e in zip.Entries) {
      if (e.FileName.Equals("datapackage.json", StringComparison.OrdinalIgnoreCase))
        hasDataPackage = true;
      else if (e.FileName.StartsWith("archive/", StringComparison.OrdinalIgnoreCase))
        hasArchiveDir = true;
      if (hasDataPackage && hasArchiveDir)
        return;
    }
    throw new InvalidDataException(
      "Not a WACZ archive: must contain 'datapackage.json' at the root and an 'archive/' directory.");
  }

  private static byte[]? TryReadEntry(ZipReader zip, string name) {
    foreach (var e in zip.Entries) {
      if (e.FileName.Equals(name, StringComparison.OrdinalIgnoreCase))
        return zip.ExtractEntry(e);
    }
    return null;
  }

  /// <summary>
  /// Builds a small INI file summarising the WACZ from the parsed
  /// <c>datapackage.json</c>, the WARC inventory and the page count from
  /// <c>pages/pages.jsonl</c>. The JSON parser is intentionally minimal to
  /// avoid pulling a JSON dependency into this descriptor.
  /// </summary>
  private static byte[] BuildMetadata(ZipReader zip, byte[]? datapackage, byte[]? pages) {
    var sb = new StringBuilder();
    sb.AppendLine("[wacz]");

    string? title = null, version = null, software = null;
    if (datapackage is { Length: > 0 }) {
      var json = Encoding.UTF8.GetString(datapackage);
      title = ExtractJsonString(json, "title");
      version = ExtractJsonString(json, "wacz_version") ?? ExtractJsonString(json, "version");
      software = ExtractJsonString(json, "software");
    }

    sb.Append("title = ").AppendLine(title ?? "(unknown)");
    sb.Append("wacz_version = ").AppendLine(version ?? "(unknown)");
    sb.Append("software = ").AppendLine(software ?? "(unknown)");

    var warcCount = 0;
    long warcBytes = 0;
    foreach (var e in zip.Entries) {
      if (e.IsDirectory) continue;
      var name = e.FileName;
      if (name.StartsWith("archive/", StringComparison.OrdinalIgnoreCase)
          && (name.EndsWith(".warc", StringComparison.OrdinalIgnoreCase)
              || name.EndsWith(".warc.gz", StringComparison.OrdinalIgnoreCase))) {
        ++warcCount;
        warcBytes += e.UncompressedSize;
      }
    }
    sb.Append("warc_count = ").Append(warcCount.ToString(CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("warc_bytes_uncompressed = ").Append(warcBytes.ToString(CultureInfo.InvariantCulture)).AppendLine();

    if (pages is { Length: > 0 }) {
      var pageCount = CountLines(pages) - 1; // minus the JSON-Lines header line
      if (pageCount < 0) pageCount = 0;
      sb.Append("page_count = ").Append(pageCount.ToString(CultureInfo.InvariantCulture)).AppendLine();
    }

    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  /// <summary>
  /// Hand-rolled, dependency-free extractor for a top-level JSON string property.
  /// Returns the literal string value (with simple <c>\"</c> and <c>\\</c> unescaping)
  /// or <see langword="null"/> if the key is absent or the value is not a string.
  /// </summary>
  private static string? ExtractJsonString(string json, string key) {
    var needle = "\"" + key + "\"";
    var start = json.IndexOf(needle, StringComparison.Ordinal);
    if (start < 0) return null;
    var colon = json.IndexOf(':', start + needle.Length);
    if (colon < 0) return null;
    var i = colon + 1;
    while (i < json.Length && char.IsWhiteSpace(json[i])) ++i;
    if (i >= json.Length || json[i] != '"') return null;
    ++i;
    var sb = new StringBuilder();
    while (i < json.Length) {
      var c = json[i];
      if (c == '\\' && i + 1 < json.Length) {
        sb.Append(json[i + 1]);
        i += 2;
        continue;
      }
      if (c == '"') break;
      sb.Append(c);
      ++i;
    }
    return sb.ToString();
  }

  private static int CountLines(byte[] data) {
    var count = 0;
    foreach (var b in data)
      if (b == (byte)'\n') ++count;
    return count;
  }
}
