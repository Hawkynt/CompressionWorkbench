#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ZipContainer;

/// <summary>
/// Base descriptor for the format family whose container <em>is</em> a ZIP: APK, APPX, CBZ, CRX,
/// DOCX, EAR, EPUB, IPA, JAR, KMZ, MAFF, NUPKG, ODP/ODS/ODT, PPTX, VSDX, WAR, XLSX, XPI and XPS all
/// list, extract, create and defragment through <see cref="FileFormat.Zip.ZipReader"/> and
/// <see cref="FileFormat.Zip.ZipWriter"/> and differ only in what they call themselves and which
/// extensions they answer to.
///
/// <para>A concrete format supplies a display name, an extension list and a description. Everything
/// else has a default here, and the two ways a member of this family can legitimately differ from a
/// bare ZIP are the hooks <see cref="PrepareRead"/> and <see cref="WriteContainerPrefix"/>: a
/// container that wraps the ZIP payload in a prefix (CRX's <c>Cr24</c> envelope) overrides those
/// rather than restating the eight operations.</para>
///
/// <para>This tier is write-once-read-many. Formats that also accept in-place entry edits derive
/// from <see cref="ModifiableZipContainerFormatDescriptor"/>, and those that additionally expose a
/// native free-space wipe from <see cref="WipeableZipContainerFormatDescriptor"/>.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT</c> — PKWARE APPNOTE — the underlying ZIP container spec</description></item>
/// </list>
/// </summary>
public abstract class ZipContainerFormatDescriptor
  : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  private const string DescriptorSuffix = "FormatDescriptor";

  /// <summary>
  /// Gets the id, derived from the concrete class name minus its <c>FormatDescriptor</c> suffix.
  /// </summary>
  /// <remarks>
  /// <c>Compression.Registry.Generator</c> builds the <c>FormatDetector.Format</c> enum from that
  /// same class name and never reads this property, while <c>EnsureRegistryMapped</c> looks the
  /// descriptor up by <c>Enum.TryParse&lt;Format&gt;(Id)</c>. A hand-written id that disagrees with
  /// the class name therefore drops the format out of every extension, signature and explicit
  /// selection table without any error — which is exactly how three Office/CFB descriptors once
  /// shipped dead. Deriving the id from the name makes the two incapable of disagreeing.
  /// </remarks>
  public string Id { get; }

  /// <summary>Initializes the shared ZIP-container state.</summary>
  protected ZipContainerFormatDescriptor() {
    var name = this.GetType().Name;
    this.Id = name.EndsWith(DescriptorSuffix, StringComparison.Ordinal)
      ? name[..^DescriptorSuffix.Length]
      : name;
  }

  /// <summary>
  /// Gets the display name.
  /// </summary>
  public abstract string DisplayName { get; }

  /// <summary>
  /// Gets the extensions. The first entry doubles as <see cref="DefaultExtension"/>.
  /// </summary>
  public abstract IReadOnlyList<string> Extensions { get; }

  /// <summary>
  /// Gets the description.
  /// </summary>
  public abstract string Description { get; }

  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public virtual string DefaultExtension => this.Extensions[0];

  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public virtual FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public virtual IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// Gets the magic signatures. Empty by default: a plain ZIP container is recognised by the ZIP
  /// descriptor, so only a family member with an envelope of its own (CRX) claims a signature.
  /// </summary>
  public virtual IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <summary>
  /// Gets the methods.
  /// </summary>
  public virtual IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];

  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public virtual string? TarCompressionFormatId => null;

  /// <summary>
  /// Positions <paramref name="stream"/> on the embedded ZIP and returns it. The default is the
  /// identity, because for most of the family the container is the ZIP. A format that prefixes the
  /// ZIP payload overrides this to validate and skip that prefix.
  /// </summary>
  protected virtual Stream PrepareRead(Stream stream) => stream;

  /// <summary>
  /// Writes whatever precedes the ZIP payload in a container this descriptor builds. The default
  /// writes nothing; it is the counterpart of <see cref="PrepareRead"/>.
  /// </summary>
  protected virtual void WriteContainerPrefix(Stream output) { }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => FileFormat.Zip.ZipLayoutMap.Enumerate(archive);

  /// <summary>Rebuild-based defrag delegating to ZIP.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag delegating to ZIP.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new FileFormat.Zip.ZipReader(this.PrepareRead(stream));
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.ExtractEntry(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        this.WriteContainerPrefix(ms);
        using (var w = new FileFormat.Zip.ZipWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
          w.Finish();
        }
        return ms.ToArray();
      });
  }

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FileFormat.Zip.ZipReader(this.PrepareRead(stream), password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FileFormat.Zip.ZipReader(this.PrepareRead(stream), password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.FileName)); continue; }
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. Delegates to the
  /// underlying ZIP reader and wraps the decoded byte buffer in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized to
  /// the entry's uncompressed length, so block padding and adjacent entries
  /// are physically unreachable through the returned view.
  /// </summary>
  /// <remarks>
  /// Deliberately reads from position 0 rather than through <see cref="PrepareRead"/>: a ZIP
  /// records absolute stream offsets, so the payload of a prefixed container is reachable without
  /// skipping the prefix, and every descriptor in this family already behaved that way.
  /// </remarks>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new FileFormat.Zip.ZipReader(archive, leaveOpen: true, password: password);
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
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    this.WriteContainerPrefix(output);
    using var w = new FileFormat.Zip.ZipWriter(output, leaveOpen: true);
    foreach (var i in inputs) {
      if (i.IsDirectory) { w.AddDirectory(i.ArchiveName); continue; }
      w.AddEntry(i.ArchiveName, i.ReadContent());
    }
  }
}

/// <summary>
/// A <see cref="ZipContainerFormatDescriptor"/> whose container also accepts in-place entry edits,
/// which is every member of the family except CRX — a CRX is signature-sealed, so mutating the
/// trailing ZIP would invalidate the signatures the envelope carries.
/// </summary>
public abstract class ModifiableZipContainerFormatDescriptor : ZipContainerFormatDescriptor, IArchiveModifiable {

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public override FormatCapabilities Capabilities => base.Capabilities | FormatCapabilities.CanModify;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing archive. Routes to
  /// <see cref="FileFormat.Zip.ZipModifier"/> for true random-access I/O — only
  /// the central directory, EOCD, and the appended file's local file header +
  /// compressed data are read or written. Pre-existing entry LFH + payload
  /// bytes at original offsets remain byte-identical.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      FileFormat.Zip.ZipModifier.RemoveFile(archive, name, wipeData: true);
      FileFormat.Zip.ZipModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named entries; uses <see cref="FileFormat.Zip.ZipModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      FileFormat.Zip.ZipModifier.RemoveFile(archive, name, wipeData: true);
  }
}

/// <summary>
/// A <see cref="ModifiableZipContainerFormatDescriptor"/> that answers <see cref="IWipeEmpty"/> with
/// a native ZIP-layout wipe instead of the interface default.
/// </summary>
public abstract class WipeableZipContainerFormatDescriptor : ModifiableZipContainerFormatDescriptor, IWipeEmpty {

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
    var extents = FileFormat.Zip.ZipLayoutMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }
}
