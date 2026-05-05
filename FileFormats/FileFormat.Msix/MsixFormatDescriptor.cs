using System.Globalization;
using System.Text;
using System.Xml;
using Compression.Registry;
using FileFormat.Zip;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Msix;

/// <summary>
/// Descriptor for MSIX and MSIXBUNDLE packages.
/// On disk these are ZIP archives whose root contains an <c>AppxManifest.xml</c>
/// (MSIX) or <c>AppxBundleManifest.xml</c> (MSIX bundle). The on-disk structure
/// is identical to APPX; only the manifest semantics and file extensions differ.
/// The descriptor surfaces a synthetic <c>metadata.ini</c> summarising identity
/// and capability declarations parsed from the manifest, followed by every
/// ZIP entry verbatim.
/// </summary>
public sealed class MsixFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  /// <summary>Unique format identifier.</summary>
  public string Id => "Msix";

  /// <summary>Human-readable name.</summary>
  public string DisplayName => "MSIX";

  /// <summary>This format describes an archive container.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Capabilities supported by this descriptor.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <summary>Preferred extension when producing a new package.</summary>
  public string DefaultExtension => ".msix";

  /// <summary>Extensions recognised as MSIX packages.</summary>
  public IReadOnlyList<string> Extensions => [".msix", ".msixbundle"];

  /// <summary>Compound extensions are not used by this format.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// No magic bytes are advertised: MSIX is a ZIP archive and detection relies on
  /// extension plus the presence of <c>AppxManifest.xml</c> or <c>AppxBundleManifest.xml</c>.
  /// Declaring the ZIP magic here would cause first-match conflicts with the bare ZIP descriptor.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <summary>Compression methods exposed for creation.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];

  /// <summary>Not a TAR-compound format.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Algorithmic family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Short description.</summary>
  public string Description => "Windows MSIX / MSIXBUNDLE application package (ZIP-based)";

  /// <summary>
  /// Lists the synthetic <c>metadata.ini</c> entry followed by every ZIP entry in the package.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZipReader(stream, leaveOpen: true, password: password);
    var metadata = BuildMetadata(r);

    var entries = new List<ArchiveEntryInfo> {
      new(0, "metadata.ini", metadata.Length, metadata.Length, "stored", false, false, null),
    };
    for (var i = 0; i < r.Entries.Count; i++) {
      var e = r.Entries[i];
      entries.Add(new ArchiveEntryInfo(
        i + 1, e.FileName, e.UncompressedSize, e.CompressedSize,
        e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified));
    }
    return entries;
  }

  /// <summary>
  /// Extracts ZIP entries to <paramref name="outputDir"/> and also emits
  /// <c>metadata.ini</c> when no explicit file filter is provided or when
  /// the filter explicitly names it.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZipReader(stream, leaveOpen: true, password: password);
    var metadata = BuildMetadata(r);

    if (files == null || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", metadata);

    foreach (var entry in r.Entries) {
      if (files != null && !MatchesFilter(entry.FileName, files)) continue;
      if (entry.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, entry.FileName));
        continue;
      }
      WriteFile(outputDir, entry.FileName, r.ExtractEntry(entry));
    }
  }

  /// <summary>
  /// Creates a new MSIX package as a plain ZIP archive. The caller is responsible
  /// for supplying a valid <c>AppxManifest.xml</c> among the inputs.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new ZipWriter(output, leaveOpen: true);
    foreach (var i in inputs) {
      if (i.IsDirectory) {
        w.AddDirectory(i.ArchiveName);
        continue;
      }
      w.AddEntry(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
  }

  private static byte[] BuildMetadata(ZipReader r) {
    var identity = TryReadIdentity(r);
    var sb = new StringBuilder();
    sb.Append("[msix]\n");
    sb.Append(CultureInfo.InvariantCulture, $"entry_count = {r.Entries.Count}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"manifest_kind = {identity.ManifestKind ?? "unknown"}\n");
    if (identity.Name is not null) sb.Append(CultureInfo.InvariantCulture, $"name = {identity.Name}\n");
    if (identity.Publisher is not null) sb.Append(CultureInfo.InvariantCulture, $"publisher = {identity.Publisher}\n");
    if (identity.Version is not null) sb.Append(CultureInfo.InvariantCulture, $"version = {identity.Version}\n");
    if (identity.ProcessorArchitecture is not null)
      sb.Append(CultureInfo.InvariantCulture, $"processor_architecture = {identity.ProcessorArchitecture}\n");
    if (identity.DisplayName is not null)
      sb.Append(CultureInfo.InvariantCulture, $"display_name = {identity.DisplayName}\n");
    if (identity.PublisherDisplayName is not null)
      sb.Append(CultureInfo.InvariantCulture, $"publisher_display_name = {identity.PublisherDisplayName}\n");
    if (identity.Description is not null)
      sb.Append(CultureInfo.InvariantCulture, $"description = {identity.Description}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static IdentityInfo TryReadIdentity(ZipReader reader) {
    var info = new IdentityInfo();

    ZipEntry? manifestEntry = null;
    foreach (var entry in reader.Entries) {
      if (entry.FileName.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)) {
        manifestEntry = entry;
        info.ManifestKind = "AppxManifest";
        break;
      }
      if (entry.FileName.Equals("AppxBundleManifest.xml", StringComparison.OrdinalIgnoreCase)) {
        manifestEntry = entry;
        info.ManifestKind = "AppxBundleManifest";
      }
    }
    if (manifestEntry is null) return info;

    byte[] xmlBytes;
    try {
      xmlBytes = reader.ExtractEntry(manifestEntry);
    } catch {
      return info;
    }

    var doc = new XmlDocument();
    try {
      using var ms = new MemoryStream(xmlBytes);
      doc.Load(ms);
    } catch {
      return info;
    }

    var root = doc.DocumentElement;
    if (root is null) return info;

    var identity = FindFirstLocal(root, "Identity");
    if (identity is not null) {
      info.Name = NullIfEmpty(identity.GetAttribute("Name"));
      info.Publisher = NullIfEmpty(identity.GetAttribute("Publisher"));
      info.Version = NullIfEmpty(identity.GetAttribute("Version"));
      info.ProcessorArchitecture = NullIfEmpty(identity.GetAttribute("ProcessorArchitecture"));
    }

    var properties = FindFirstLocal(root, "Properties");
    if (properties is not null) {
      info.DisplayName = TextOfChild(properties, "DisplayName");
      info.PublisherDisplayName = TextOfChild(properties, "PublisherDisplayName");
      info.Description = TextOfChild(properties, "Description");
    }

    return info;
  }

  private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

  private static XmlElement? FindFirstLocal(XmlElement root, string localName) {
    foreach (var node in root.ChildNodes)
      if (node is XmlElement e && e.LocalName.Equals(localName, StringComparison.Ordinal))
        return e;
    return null;
  }

  private static string? TextOfChild(XmlElement parent, string localName) {
    foreach (var node in parent.ChildNodes)
      if (node is XmlElement e && e.LocalName.Equals(localName, StringComparison.Ordinal)) {
        var txt = e.InnerText.Trim();
        return string.IsNullOrEmpty(txt) ? null : txt;
      }
    return null;
  }

  private sealed class IdentityInfo {
    public string? ManifestKind { get; set; }
    public string? Name { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? ProcessorArchitecture { get; set; }
    public string? DisplayName { get; set; }
    public string? PublisherDisplayName { get; set; }
    public string? Description { get; set; }
  }
}
