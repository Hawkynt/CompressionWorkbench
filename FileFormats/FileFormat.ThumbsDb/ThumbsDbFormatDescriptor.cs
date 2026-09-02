#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ThumbsDb;

/// <summary>
/// Windows Thumbs.db thumbnail cache — an OLE2/CFB compound document holding per-image thumbnail streams.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/53989ce4-7b05-4f8d-829b-d08d6148375b</c> — [MS-CFB]: Compound File Binary File Format — the OLE2 container</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Windows_thumbnail_cache</c> — Wikipedia overview</description></item>
///   <item><description>internal stream layout undocumented by Microsoft; reverse-engineered by the digital-forensics community (e.g. the vinetto tool)</description></item>
/// </list>
/// </summary>
public sealed class ThumbsDbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty {

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "Thumbs.db is an OLE2 Compound File with a Catalog stream + per-thumbnail JPEG streams — " +
      "rebuilding from the surface stream list would destroy the catalog references.");
  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);


  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => Msi.CfbLayoutMap.Enumerate(archive);

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "ThumbsDb";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Thumbs.db";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".db";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".db"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("cfb", "Compound File Binary")];
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
public string Description => "Windows thumbnail cache (OLE2)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Msi.MsiReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.FullPath, e.Size, e.Size, "Stored",
      e.IsDirectory, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Msi.MsiReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: structurally-valid CFB envelope; not a real Thumbs.db (which has a
    // specific Catalog stream + per-thumbnail JPEG streams).
    var w = new Msi.CfbWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddStream(CfbStreamName(i.ArchiveName), i.ReadContent());
    }
    w.WriteTo(output);
  }

  private static string CfbStreamName(string archiveName) {
    var leaf = Path.GetFileName(archiveName);
    if (string.IsNullOrEmpty(leaf)) leaf = archiveName;
    return leaf.Length > 31 ? leaf[..31] : leaf;
  }

  /// <summary>
  /// Zeros every dead byte in the archive: any byte not covered by a live extent
  /// in the layout map (headers, entry data and directory structures are live and
  /// preserved, so the archive still lists and extracts identically). Cluster-tip
  /// wiping is N/A (entries are stored byte-exact with no per-file slack).
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = this.EnumerateLayout(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }
}
