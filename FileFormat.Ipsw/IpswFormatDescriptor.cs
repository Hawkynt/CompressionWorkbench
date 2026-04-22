#pragma warning disable CS1591
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ipsw;

/// <summary>
/// Apple IPSW / OTA firmware package. An IPSW is just a ZIP file (with an Apple-specific layout).
/// Rather than surfacing entries as a flat generic ZIP, this descriptor lifts the well-known
/// Apple artifacts (<c>BuildManifest.plist</c>, <c>Firmware/</c> subtree, <c>LLB.*</c>, <c>iBSS.*</c>,
/// <c>iBEC.*</c>, <c>iBoot.*</c>, root-filesystem <c>*.dmg</c>) into first-class canonical entries.
/// Everything else is exposed under <c>other/</c>.
///
/// <para>This is a compound-extension descriptor (<c>.ipsw</c>, <c>.otazip</c>): magic is empty so
/// it does not steal generic ZIPs. Read-only; the plist and DMG payloads are emitted as raw bytes
/// — no plist parsing or DMG mounting.</para>
/// </summary>
public sealed class IpswFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Ipsw";
  public string DisplayName => "Apple IPSW";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ipsw";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [".ipsw", ".otazip"];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate"), new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apple firmware package (ZIP containing BuildManifest.plist, Firmware/, DMG root FS).";

  private sealed record CanonicalEntry(string CanonicalName, string ZipEntryName, long Size, string Method, DateTime? LastModified, string? Kind);

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (canonical, total) = EnumerateCanonical(stream);
    var entries = new List<ArchiveEntryInfo>(2 + canonical.Count);
    entries.Add(new ArchiveEntryInfo(0, "FULL.ipsw", stream.Length, stream.Length, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: $"total_zip_entries={total}"));
    for (var i = 0; i < canonical.Count; ++i) {
      var c = canonical[i];
      entries.Add(new ArchiveEntryInfo(
        Index: 2 + i,
        Name: c.CanonicalName,
        OriginalSize: c.Size,
        CompressedSize: c.Size,
        Method: c.Method,
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: c.LastModified,
        Kind: c.Kind));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // FULL.ipsw: stream directly from the input — never materialize into memory.
    if (Wants(files, "FULL.ipsw")) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.ipsw");
      var dir = Path.GetDirectoryName(fullPath);
      if (dir != null) Directory.CreateDirectory(dir);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }

    // Re-open ZIP on the seekable input stream.
    stream.Seek(0, SeekOrigin.Begin);
    using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    var (canonical, total) = EnumerateCanonicalFromZip(zip);

    string? identifier = null;
    string? productVersion = null;
    string? buildVersion = null;

    foreach (var c in canonical) {
      if (!Wants(files, c.CanonicalName)) continue;
      var zipEntry = zip.GetEntry(c.ZipEntryName);
      if (zipEntry == null) continue;

      var destPath = SafeCombine(outputDir, c.CanonicalName);
      var destDir = Path.GetDirectoryName(destPath);
      if (destDir != null) Directory.CreateDirectory(destDir);
      using (var es = zipEntry.Open())
      using (var outFile = File.Create(destPath)) {
        es.CopyTo(outFile);
      }

      if (c.CanonicalName == "BuildManifest.plist") {
        // BuildManifest is bounded (typically <1 MB) — safe to read back for metadata parsing.
        var data = File.ReadAllBytes(destPath);
        TryParsePlistFields(data, out identifier, out productVersion, out buildVersion);
      }
    }

    if (Wants(files, "metadata.ini")) {
      // If we didn't extract the manifest above, best-effort parse it now to populate metadata.
      if (identifier == null && productVersion == null && buildVersion == null) {
        var manifest = zip.GetEntry("BuildManifest.plist");
        if (manifest != null) {
          // Manifest is small and bounded — fine to materialize.
          using var es = manifest.Open();
          using var ms = new MemoryStream();
          es.CopyTo(ms);
          TryParsePlistFields(ms.ToArray(), out identifier, out productVersion, out buildVersion);
        }
      }
      WriteFile(outputDir, "metadata.ini",
        Encoding.UTF8.GetBytes(BuildMetadataIni(identifier, productVersion, buildVersion, total)));
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static (List<CanonicalEntry> Canonical, int TotalZipEntries) EnumerateCanonical(Stream stream) {
    stream.Seek(0, SeekOrigin.Begin);
    using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    return EnumerateCanonicalFromZip(zip);
  }

  private static (List<CanonicalEntry> Canonical, int TotalZipEntries) EnumerateCanonicalFromZip(ZipArchive zip) {
    var canonical = new List<CanonicalEntry>();
    foreach (var entry in zip.Entries) {
      // Skip directory entries (trailing slash, zero length implied).
      if (entry.FullName.EndsWith('/')) continue;

      var name = entry.FullName.Replace('\\', '/');
      var filename = Path.GetFileName(name);
      var method = entry.CompressedLength == entry.Length ? "Stored" : "Deflate";
      DateTime? lastModified = entry.LastWriteTime.DateTime;

      string canonicalName;
      string? kind;
      if (string.Equals(filename, "BuildManifest.plist", StringComparison.OrdinalIgnoreCase)) {
        canonicalName = "BuildManifest.plist";
        kind = "manifest";
      } else if (name.Contains("Firmware/", StringComparison.OrdinalIgnoreCase)) {
        canonicalName = "Firmware/" + filename;
        kind = "firmware";
      } else if (IsBootloaderStage(filename)) {
        canonicalName = filename;
        kind = "bootloader";
      } else if (filename.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)) {
        canonicalName = filename;
        kind = "rootfs";
      } else {
        canonicalName = "other/" + name;
        kind = "other";
      }

      // Use entry.Length — never call entry.Open() during List enumeration.
      canonical.Add(new CanonicalEntry(
        CanonicalName: canonicalName,
        ZipEntryName: entry.FullName,
        Size: entry.Length,
        Method: method,
        LastModified: lastModified,
        Kind: kind));
    }
    var total = zip.Entries.Count;
    return (canonical, total);
  }

  private static bool IsBootloaderStage(string filename) {
    // Apple boot stage prefixes: LLB., iBSS., iBEC., iBoot.
    if (filename.StartsWith("LLB.", StringComparison.OrdinalIgnoreCase)) return true;
    if (filename.StartsWith("iBSS.", StringComparison.OrdinalIgnoreCase)) return true;
    if (filename.StartsWith("iBEC.", StringComparison.OrdinalIgnoreCase)) return true;
    if (filename.StartsWith("iBoot.", StringComparison.OrdinalIgnoreCase)) return true;
    return false;
  }

  /// <summary>
  /// Mirrors the path-sanitization done by <see cref="FormatHelpers.WriteFile"/> so we can
  /// stream directly into the target file without first materializing the payload.
  /// </summary>
  private static string SafeCombine(string baseDir, string entryName) {
    var safeName = entryName.Replace('\\', '/').TrimStart('/');
    if (safeName.Contains("..")) safeName = Path.GetFileName(safeName);
    return Path.Combine(baseDir, safeName);
  }

  /// <summary>
  /// Best-effort plist field scrape. We intentionally avoid full plist parsing; this just finds
  /// the &lt;key&gt;...&lt;/key&gt;&lt;string&gt;...&lt;/string&gt; pairs for a few known Apple
  /// manifest fields (ProductVersion, ProductBuildVersion, identifier-like). Returns null if the
  /// plist is binary or un-parseable.
  /// </summary>
  private static void TryParsePlistFields(byte[] data, out string? identifier, out string? productVersion, out string? buildVersion) {
    identifier = null;
    productVersion = null;
    buildVersion = null;
    if (data.Length < 16) return;
    // Binary plist — give up; we surface raw bytes so consumers can parse.
    if (data[0] == (byte)'b' && data[1] == (byte)'p' && data[2] == (byte)'l') return;

    string text;
    try { text = Encoding.UTF8.GetString(data); }
    catch { return; }

    productVersion = FindStringValue(text, "ProductVersion");
    buildVersion = FindStringValue(text, "ProductBuildVersion") ?? FindStringValue(text, "BuildVersion");
    identifier = FindStringValue(text, "ProductType") ?? FindStringValue(text, "Identifier");
  }

  private static string? FindStringValue(string plistXml, string key) {
    var keyTag = $"<key>{key}</key>";
    var keyIdx = plistXml.IndexOf(keyTag, StringComparison.Ordinal);
    if (keyIdx < 0) return null;
    var searchFrom = keyIdx + keyTag.Length;
    var openIdx = plistXml.IndexOf("<string>", searchFrom, StringComparison.Ordinal);
    if (openIdx < 0) return null;
    var closeIdx = plistXml.IndexOf("</string>", openIdx + 8, StringComparison.Ordinal);
    if (closeIdx < 0) return null;
    return plistXml.Substring(openIdx + 8, closeIdx - (openIdx + 8));
  }

  private static string BuildMetadataIni(string? identifier, string? productVersion, string? buildVersion, int totalZipEntries) {
    var sb = new StringBuilder();
    sb.Append("[Ipsw]\n");
    sb.Append(CultureInfo.InvariantCulture, $"identifier={identifier ?? string.Empty}\n");
    sb.Append(CultureInfo.InvariantCulture, $"product_version={productVersion ?? string.Empty}\n");
    sb.Append(CultureInfo.InvariantCulture, $"build_version={buildVersion ?? string.Empty}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_zip_entries={totalZipEntries}\n");
    return sb.ToString();
  }
}
