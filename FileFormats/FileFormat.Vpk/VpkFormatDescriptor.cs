#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vpk;

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

  public string Id => "Vpk";
  public string DisplayName => "VPK";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".vpk";
  public IReadOnlyList<string> Extensions => [".vpk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x34, 0x12, 0xAA, 0x55], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("vpk", "VPK")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Valve Pak game resource archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new VpkReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath,
      e.PreloadBytes.Length + e.Length, e.PreloadBytes.Length + e.Length,
      "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new VpkReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

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
