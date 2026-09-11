#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.GlusterFs;

/// <summary>
/// Read-only single-brick view for GlusterFS backing-store images.
///
/// <para>GlusterFS has no independent block format: a volume is a logical
/// collection of bricks and each brick is an export directory on an ordinary
/// filesystem with extended-attribute support. This descriptor therefore
/// delegates real backing images to the repository's XFS or ext2/3/4 reader and
/// surfaces the physical contents of one brick. It does not reconstruct the
/// distributed Gluster namespace.</para>
///
/// <para>Detection is extension-only. XFS and ext images already belong to their
/// native descriptors and there is no Gluster-specific superblock magic with
/// which to distinguish a brick image automatically. The former workbench-only
/// 0xCAFE5BAB probe convention is deliberately not recognised as GlusterFS.</para>
///
/// <para>Mutating maintenance operations are intentionally not advertised. A
/// Gluster brick relies on <c>trusted.gfid</c> and <c>trusted.glusterfs.*</c>
/// xattrs for identity, DHT layout, replication/heal and related state. The
/// current backing-filesystem rebuild paths do not preserve those attributes,
/// so create/modify/defrag/shrink/layout/wipe/purge would risk silent brick
/// corruption.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.gluster.org/en/latest/Administrator-Guide/Setting-Up-Volumes/</c> — a volume is a logical collection of export-directory bricks</description></item>
///   <item><description><c>https://docs.gluster.org/en/latest/Administrator-Guide/GlusterFS-Introduction/</c> — backing filesystems must support extended attributes</description></item>
///   <item><description><c>https://docs.gluster.org/en/latest/Administrator-Guide/Managing-Volumes/</c> — shrink/rebalance/fix-layout are cluster operations</description></item>
///   <item><description><c>https://github.com/gluster/glusterfs</c> — canonical implementation, dual GPLv2/LGPLv3+</description></item>
/// </list>
/// </summary>
public sealed class GlusterFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "GlusterFs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "GlusterFS brick";
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
  public string DefaultExtension => ".gluster";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".gluster"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures. Empty by design because GlusterFS has no separate on-disk magic.
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
    "GlusterFS single-brick R/O backing-store view via XFS/ext delegation. GlusterFS has no " +
    "standalone image format; the supplied .gluster image is treated as one brick's backing " +
    "filesystem. The .glusterfs GFID index is hidden from normal listings. trusted.gfid and " +
    "trusted.glusterfs.* xattrs remain preserved in the raw image but are not interpreted, so " +
    "cluster namespace reconstruction and all mutations remain disabled.";

  /// <summary>
  /// Lists the entries in the supplied brick backing-store image.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new GlusterFsReader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Size, entry.Size, "Stored", entry.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Extracts entries from the supplied single-brick physical view.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new GlusterFsReader(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (files != null && !MatchesFilter(entry.Name, files)) continue;
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    using var reader = new GlusterFsReader(archive);
    var entry = reader.Entries.FirstOrDefault(candidate => candidate.Name == entryName)
      ?? throw new FileNotFoundException($"GlusterFS brick entry not found: {entryName}");
    var data = reader.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
