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
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://theapplewiki.com</c> — The Apple Wiki (formerly The iPhone Wiki) — community IPSW documentation</description></item>
///   <item><description><c>https://github.com/blacktop/ipsw</c> — ipsw — maintained IPSW research and extraction tool</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/IPSW</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class IpswFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveLayoutMap, IArchiveCreatable, IArchiveModifiable {

  /// <inheritdoc />
    /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => FileFormat.Zip.ZipLayoutMap.Enumerate(archive);

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Ipsw";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Apple IPSW";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".ipsw";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [".ipsw", ".otazip"];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate"), new("stored", "Stored")];
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
public string Description =>
    "Apple firmware package (ZIP containing BuildManifest.plist, Firmware/, DMG root FS). " +
    "R/W: in-place Add / Remove against the ZIP central directory (delegates to ZipModifier). " +
    "Inner DMG / firmware blob mutation is delegated to FileFormat.Dmg and the per-firmware descriptors.";

  private sealed record CanonicalEntry(string CanonicalName, string ZipEntryName, long Size, string Method, DateTime? LastModified, string? Kind);

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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

  // ── IArchiveCreatable ─────────────────────────────────────────────

  /// <summary>
  /// Emits a fresh IPSW (ZIP) container from the supplied inputs. Synthetic
  /// canonical entries the descriptor surfaces on read (<c>FULL.ipsw</c>,
  /// <c>metadata.ini</c>) are silently dropped — they aren't real ZIP entries.
  /// All other inputs are stored under their <see cref="ArchiveInputInfo.ArchiveName"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var zip = new FileFormat.Zip.ZipWriter(output, leaveOpen: true);
    foreach (var (name, data) in FilesOnly(inputs)) {
      if (string.Equals(name, "FULL.ipsw", StringComparison.OrdinalIgnoreCase)) continue;
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      zip.AddEntry(name, data);
    }
    zip.Finish();
  }

  // ── IArchiveModifiable ─────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by ZIP path) entries inside an existing IPSW.
  /// Routes through <see cref="IpswInPlaceModifier"/> — only the central
  /// directory, EOCD, and the appended LFH + payload are touched.
  /// Synthetic canonical entries are silently dropped.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs)) {
      if (string.Equals(name, "FULL.ipsw", StringComparison.OrdinalIgnoreCase)) continue;
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      IpswInPlaceModifier.AddEntry(archive, name, data);
    }
  }

  /// <summary>
  /// Removes named ZIP entries from an existing IPSW. Routes through
  /// <see cref="IpswInPlaceModifier"/> — the LFH + compressed payload of
  /// the dropped entry are zero-wiped and the central directory is rewritten.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames) {
      if (string.Equals(name, "FULL.ipsw", StringComparison.OrdinalIgnoreCase)) continue;
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      IpswInPlaceModifier.RemoveEntry(archive, name);
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
