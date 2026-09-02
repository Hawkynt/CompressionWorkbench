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
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://specs.webrecorder.net/wacz/1.1.1/</c> — the WACZ 1.1.1 specification (Webrecorder)</description></item>
///   <item><description><c>https://webrecorder.net</c> — Webrecorder, the format's author and reference tooling (py-wacz, ReplayWeb.page)</description></item>
/// </list>
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
/// </remarks>
public sealed class WaczFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveLayoutMap {

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => ZipLayoutMap.Enumerate(archive);

  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Wacz";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "WACZ";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc/>
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <inheritdoc/>
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".wacz";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".wacz"];

  /// <inheritdoc/>
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc/>
  /// <remarks>Empty: outer container is ZIP; detection is by extension to avoid
  /// shadowing the ZIP descriptor.</remarks>
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <inheritdoc/>
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];

  /// <inheritdoc/>
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;

  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Web Archive Collection Zipped — ZIP container around WARC files with " +
    "datapackage.json metadata and a page index.";

  /// <inheritdoc/>
  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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
  /// Opens a single entry as a bounded read-only stream. The synthetic
  /// <c>metadata.ini</c> entry is materialised on the fly; all other
  /// entries delegate to the inner <see cref="ZipReader"/> and are wrapped
  /// in a <see cref="Compression.Registry.Streaming.BoundedEntryStream"/>
  /// sized to the entry's uncompressed length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var zip = new ZipReader(archive, leaveOpen: true, password: password);
    if (string.Equals(entryName, "metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      EnsureLooksLikeWacz(zip);
      var datapackage = TryReadEntry(zip, "datapackage.json");
      var pages = TryReadEntry(zip, "pages/pages.jsonl");
      var meta = BuildMetadata(zip, datapackage, pages);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(meta, writable: false), meta.Length, leaveOpen: false);
    }
    foreach (var e in zip.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = zip.ExtractEntry(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => WaczCreator.Create(output, inputs);

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
