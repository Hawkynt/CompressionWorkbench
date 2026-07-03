#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Doc;

public sealed class DocFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "DOC is an OLE2 Compound File envelope with cross-referenced internal streams (WordDocument, 1Table, etc.) — " +
      "rebuilding from the surface stream list would destroy the document's internal references.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => Msi.CfbLayoutMap.Enumerate(archive);

  public string Id => "Doc";
  public string DisplayName => "DOC";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".doc";
  public IReadOnlyList<string> Extensions => [".doc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cfb", "Compound File Binary")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft Word 97-2003 document (OLE2)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Msi.MsiReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.FullPath, e.Size, e.Size, "Stored",
      e.IsDirectory, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Msi.MsiReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM creation: produce a structurally-valid CFB envelope holding the
    // input files as streams. The result roundtrips through our reader and
    // through other permissive CFB tools (libgsf, Apache POI). It is NOT a
    // valid Word document -- producing one would require generating Word's
    // internal binary streams (WordDocument, 1Table, etc.) which is far out of
    // scope for a compression toolkit.
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
