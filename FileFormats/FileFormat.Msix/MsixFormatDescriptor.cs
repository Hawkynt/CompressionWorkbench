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
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/windows/msix/</c> — Microsoft MSIX documentation portal</description></item>
///   <item><description><c>https://github.com/microsoft/msix-packaging</c> — Microsoft MSIX SDK — canonical packaging implementation</description></item>
///   <item><description><c>https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT</c> — PKWARE ZIP APPNOTE — the underlying container format</description></item>
/// </list>
/// </summary>
public sealed class MsixFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty {

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => ZipLayoutMap.Enumerate(archive);

  /// <summary>Rebuild-based defrag delegating to ZIP (MSIX is a ZIP variant).</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag delegating to ZIP (MSIX is a ZIP variant).</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ZipReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.ExtractEntry(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new ZipWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
          w.Finish();
        }
        return ms.ToArray();
      });
  }


  /// <summary>
  /// Adds (or replaces by name) files inside an existing MSIX package. Routes to
  /// <see cref="ZipModifier"/> for true random-access I/O — only the central
  /// directory, EOCD, and the appended file's local file header + compressed data
  /// are read or written; pre-existing entries stay byte-identical. The synthetic
  /// <c>metadata.ini</c> listing entry is a derived view and is skipped. Note that
  /// editing a signed package invalidates its <c>AppxSignature.p7x</c>; re-signing
  /// is out of scope.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      ZipModifier.RemoveFile(archive, name, wipeData: true);
      ZipModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes named entries via <see cref="ZipModifier"/>. The synthetic
  /// <c>metadata.ini</c> listing entry is a derived view and is skipped.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames) {
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      ZipModifier.RemoveFile(archive, name, wipeData: true);
    }
  }

  /// <summary>
  /// Zeros every dead byte in the package: gaps between entries not covered by a
  /// live extent in the ZIP layout map. Local headers, entry data, the central
  /// directory and EOCD are live and preserved. Cluster-tip wiping is N/A (ZIP
  /// packs entries back to back with no per-file slack).
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = ZipLayoutMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  /// <summary>Unique format identifier.</summary>
  public string Id => "Msix";

  /// <summary>Human-readable name.</summary>
  public string DisplayName => "MSIX";

  /// <summary>This format describes an archive container.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>
  /// Capabilities supported by this descriptor. R/W: a mutable ZIP-based package —
  /// Add/Replace/Remove are genuine in-place ZIP edits (<see cref="ZipModifier"/>),
  /// matching the sibling APPX/APK descriptors. See FormatCapabilities.cs (WORM vs R/W).
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
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
  /// Opens a single entry as a bounded read-only stream. The synthetic
  /// <c>metadata.ini</c> entry is materialised on the fly from the
  /// <c>AppxManifest.xml</c> identity; all other entries delegate to the
  /// inner <see cref="ZipReader"/> and are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's uncompressed length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new ZipReader(archive, leaveOpen: true, password: password);
    if (string.Equals(entryName, "metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      var meta = BuildMetadata(r);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(meta, writable: false), meta.Length, leaveOpen: false);
    }
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.ExtractEntry(e);
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
      w.AddEntry(i.ArchiveName, i.ReadContent());
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
