#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lzx;

/// <summary>
/// Amiga LZX archive (Jonathan Forbes and Tomi Salo) — LZ77+Huffman with merged-file compression groups.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/LZX</c> — Wikipedia — covers the Amiga LZX archiver lineage</description></item>
///   <item><description><c>https://aminet.net</c> — Aminet — home of the original archiver and the unlzx extractor whose source is the de-facto format reference</description></item>
/// </list>
/// </summary>
public sealed class LzxAmigaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty {

  /// <summary>Rebuild-based defrag: extracts then re-creates the LZX archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the LZX archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new LzxAmigaReader(stream);
        return r.Entries.Select(e => (e.FileName, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new LzxAmigaWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddFile(n, d, DateTime.Now);
        }
        return ms.ToArray();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    // 3-byte magic "LZX"
    yield return new DefragBlockInfo(0, LzxAmigaConstants.MagicLength, DefragBlockKind.MetadataReserved, FileName: "LZX Magic");
    LzxAmigaReader r;
    try {
      r = new LzxAmigaReader(archive, leaveOpen: true);
    } catch {
      yield break;
    }
    foreach (var e in r.Entries) {
      var headerSize = e.DataOffset - (e.DataOffset - LzxAmigaConstants.FixedHeaderSize - e.FileName.Length - e.Comment.Length);
      // Header starts just before DataOffset by (FixedHeaderSize + filenameLen + commentLen) bytes
      var hdrLen = LzxAmigaConstants.FixedHeaderSize + e.FileName.Length + e.Comment.Length;
      var hdrStart = e.DataOffset - hdrLen;
      if (hdrStart >= 0 && hdrLen > 0)
        yield return new DefragBlockInfo(hdrStart, hdrLen, DefragBlockKind.MetadataReserved, FileName: "Header: " + e.FileName);
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
    }
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "LzxAmiga";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "LZX (Amiga)";
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
  public string DefaultExtension => ".lzx";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".lzx"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'L', (byte)'Z', (byte)'X'], Confidence: 0.80)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzx", "LZX")];
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
  public string Description => "Amiga LZX archive with LZ+Huffman";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LzxAmigaReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.Method == 0 ? "Stored" : "LZX", false, false, e.LastModified)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LzxAmigaReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new LzxAmigaWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data, DateTime.Now);
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
