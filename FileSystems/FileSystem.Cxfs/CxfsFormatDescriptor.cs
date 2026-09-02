#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Cxfs;

/// <summary>
/// R/O descriptor for SGI CXFS (Cluster XFS) volume images. Because the
/// on-disk format is XFS-compatible (same <c>"XFSB"</c> superblock magic,
/// same <c>dinode</c> / dir2 / dir3 layout), the reader delegates content
/// extraction to <see cref="FileSystem.Xfs.XfsReader"/> and surfaces the
/// underlying file tree. CXFS-specific cluster metadata (sb_features2
/// flags, cluster UUIDs, distributed-lock bookkeeping) is intentionally
/// ignored — those are the CMS / dmF / RGM layers, not file content. If
/// the XFS reader cannot walk the image the descriptor falls back to a
/// Stage-0 <c>metadata.ini</c> + <c>cxfs-volume.bin</c> surface so the
/// volume is still identifiable.
///
/// <para>Extension-only detection (<c>.cxfs</c>) avoids first-match
/// collision with the vanilla FileSystem.Xfs descriptor — both share the
/// same magic bytes.</para>
///
/// References:
/// <list type="bullet">
///   <item><description>SGI "CXFS Administration Guide" (SGI techpubs) — the vendor documentation of the cluster layer</description></item>
///   <item><description><c>https://mirrors.edge.kernel.org/pub/linux/utils/fs/xfs/docs/xfs_filesystem_structure.pdf</c> — "XFS Algorithms &amp; Data Structures", the on-disk spec CXFS volumes follow</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/CXFS</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class CxfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Cxfs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "SGI CXFS (Cluster XFS)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".cxfs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".cxfs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // CXFS shares the XFS 'XFSB' magic — extension-only detection here so
  // FormatDetector's first-match doesn't fight FileSystem.Xfs. Reader
  // verifies magic + reads sb_features2 internally.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
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
    "SGI CXFS (Cluster XFS) — R/O via XFS reader delegation. On-disk format is XFS-compatible " +
    "(same 'XFSB' magic, same dinode/dir2/dir3 layout); cluster metadata in sb_features2 is " +
    "intentionally ignored. Extension-only detection avoids XFS first-match collision.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CxfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CxfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new CxfsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"CXFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
