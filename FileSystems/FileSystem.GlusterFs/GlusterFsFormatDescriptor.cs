#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.GlusterFs;

/// <summary>
/// Stage 0 detection-only descriptor for GlusterFS. Honest fallback:
/// GlusterFS has no on-disk image format. A GlusterFS volume is a
/// logical aggregation of one or more "bricks", and every brick is
/// just a normal directory on a local POSIX filesystem (typically
/// XFS or ext4). Volume files live at their normal POSIX paths inside
/// the brick directory and carry GlusterFS state in extended
/// attributes (<c>trusted.gfid</c>, <c>trusted.glusterfs.dht</c>,
/// <c>trusted.glusterfs.volume-id</c>, <c>trusted.glusterfs.pathinfo</c>,
/// etc.). There is no superblock, no brick header, no portable
/// single-file representation that this image-based pipeline can
/// consume.
///
/// We therefore stay Stage 0 permanently. The 0xCAFE5BAB magic
/// recognised here is a workbench-internal convention for hand-dumped
/// brick-object probes — it is not a real on-disk GlusterFS structure
/// and no real GlusterFS deployment will produce it. Promotion to R/O
/// would require walking a live directory tree and reading xattrs,
/// which is outside the image-stream contract enforced by
/// <see cref="IArchiveFormatOperations"/>.
/// </summary>
public sealed class GlusterFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "GlusterFs";
  public string DisplayName => "GlusterFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".gluster";
  public IReadOnlyList<string> Extensions => [".gluster"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0xCAFE5BAB — workbench-internal convention for hand-dumped brick
    // object probes, NOT a real on-disk GlusterFS marker. GlusterFS
    // itself has no on-disk header. See type-level doc.
    new([0xCA, 0xFE, 0x5B, 0xAB], Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "GlusterFS — Stage 0 (detection-only, permanent). A GlusterFS brick is a normal " +
    "directory on a local POSIX filesystem (XFS/ext4); files live at their POSIX paths " +
    "and metadata lives in xattrs (trusted.gfid, trusted.glusterfs.*). There is no " +
    "on-disk image format, so no R/O promotion is possible from a single image stream. " +
    "The 0xCAFE5BAB magic is a workbench-internal probe convention, not a real " +
    "GlusterFS marker.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new GlusterFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new GlusterFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new GlusterFsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"GlusterFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
