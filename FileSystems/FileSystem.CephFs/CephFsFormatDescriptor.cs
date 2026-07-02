#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.CephFs;

/// <summary>
/// Stage 0 detection-only descriptor for CephFS / RADOS OSD object
/// metadata dumps. Surfaces only a synthetic <c>metadata.ini</c> and the
/// raw image bytes; no real file-walk is attempted.
///
/// <para><b>Stage-0 confirmation — promotion to R/O is structurally impossible
/// from a single image.</b> CephFS has no standalone on-disk image format. A
/// CephFS volume consists of:</para>
/// <list type="bullet">
///   <item><description><b>Metadata</b> (inodes, dirfrags, MDS journal) stored
///     as RADOS objects inside a dedicated metadata pool, managed by one or
///     more MDS daemons. Resolving a path requires replaying the MDS journal
///     and walking dirfrag objects across the metadata pool.</description></item>
///   <item><description><b>File data</b> striped across many RADOS objects
///     (default 4 MiB stripe-unit, named <c>{inode}.{stripe-index}</c>) and
///     placed across OSDs via CRUSH against the cluster's mon-map / osd-map /
///     CRUSH-map — none of which live in any single file.</description></item>
///   <item><description>OSDs themselves store those RADOS objects in a
///     BlueStore (RocksDB + raw-block) or legacy FileStore backend; neither
///     exposes CephFS-level paths.</description></item>
/// </list>
/// <para>Reconstructing a CephFS namespace would require: (a) a full OSD-set
/// snapshot, (b) the live mon/mds cluster state (osd-map, mds-map, CRUSH-map),
/// and (c) a BlueStore reader. Even with all three, the result is OSD-level
/// objects, not CephFS-level paths. Treatment confirmed: stay Stage 0.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.ceph.com/en/latest/cephfs/</c> — official CephFS documentation (MDS, RADOS layout, striping)</description></item>
///   <item><description><c>https://github.com/ceph/ceph</c> — canonical Ceph source</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Ceph_(software)</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class CephFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "CephFs";
  public string DisplayName => "CephFS / RADOS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".ceph";
  public IReadOnlyList<string> Extensions => [".ceph", ".rados"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "CEPH" (0x43455048 BE) at offset 0.
    new("CEPH"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "CephFS / RADOS — detection-only — distributed FS, no single-image content surface. " +
    "Magic 'CEPH' at offset 0 of OSD object metadata. " +
    "Stage-0 confirmed: metadata lives in a RADOS metadata pool (MDS-managed), " +
    "file data is striped across many RADOS objects placed via CRUSH across OSDs " +
    "(BlueStore/FileStore backends); R/O over a single image is structurally impossible " +
    "without the live mon/mds cluster state.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CephFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CephFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new CephFsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"CephFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
