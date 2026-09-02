#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vpk;

/// <summary>
/// Valve Pak (VPK) game resource archive — directory-tree index, optionally split across numbered data packs.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://developer.valvesoftware.com/wiki/VPK</c> — Valve Developer Community — VPK format and tool documentation</description></item>
///   <item><description><c>https://github.com/ValvePython/vpk</c> — open VPK implementation</description></item>
/// </list>
/// </summary>
public sealed class VpkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty {

  /// <summary>Rebuild-based defrag: extracts then re-creates the VPK archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the VPK archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new VpkReader(stream);
        return r.Entries.Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new VpkWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddFile(n, d);
          w.Finish();
        }
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    VpkReader r;
    try {
      archive.Position = 0;
      r = new VpkReader(archive);
    } catch {
      yield break;
    }
    // Header: 4 (sig) + 4 (ver) + 4 (treeSize) [+ 16 for v2]
    var headerSize = r.Version == 2 ? 28 : 12;
    yield return new DefragBlockInfo(0, headerSize, DefragBlockKind.MetadataReserved, FileName: "VPK Header");
    // Directory tree sits between header and data start
    var dataStart = r.DataOffset;
    if (dataStart > headerSize)
      yield return new DefragBlockInfo(headerSize, dataStart - headerSize, DefragBlockKind.MetadataReserved, FileName: "Directory Tree");
    // Each entry's data region
    foreach (var e in r.Entries) {
      if (e.ArchiveIndex != 0x7FFF) continue; // only embedded entries (0x7FFF = this file)
      if (e.Length > 0)
        yield return new DefragBlockInfo(dataStart + e.Offset, e.Length, DefragBlockKind.Used, FileName: e.FullPath);
    }
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Vpk";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "VPK";
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
public string DefaultExtension => ".vpk";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".vpk"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x34, 0x12, 0xAA, 0x55], Confidence: 0.90)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("vpk", "VPK")];
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
public string Description => "Valve Pak game resource archive";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new VpkReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath,
      e.PreloadBytes.Length + e.Length, e.PreloadBytes.Length + e.Length,
      "Stored", false, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new VpkReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new VpkWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
    w.Finish();
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
