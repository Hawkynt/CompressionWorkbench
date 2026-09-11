#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Ext;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.GlusterFs;

/// <summary>
/// Read-only single-brick view for GlusterFS backing-store images, with a
/// conservative backing-store shrink operation where it is provably metadata-safe.
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
/// <para>The native XFS/ext layers can now read Gluster xattrs and conservatively
/// mutate the common short-form/in-inode storage cases. General archive mutation
/// remains disabled until every xattr storage form that a maintenance operation can
/// encounter (including ext external blocks/EA inodes and XFS leaf/btree/remote
/// attributes) is preserved. Advertising a rebuild-based mutation before then
/// could silently discard <c>trusted.gfid</c> or <c>trusted.glusterfs.*</c> state.</para>
///
/// <para>Shrink is the deliberate exception. For ext-backed bricks the native
/// in-place shrinker chooses its boundary from the filesystem allocation bitmap;
/// every allocated xattr block therefore pins the boundary just like file data and
/// is preserved byte-for-byte. XFS-backed bricks copy through unchanged because the
/// current XFS shrink path may rebuild the image. This is a backing-store geometry
/// operation only; it is not Gluster's remove-brick command.</para>
///
/// <para>Gluster volume operations such as rebalance, fix-layout, and remove-brick
/// are explicitly outside this single-image abstraction. They coordinate multiple
/// bricks and belong to a live cluster/volume control plane, not an offline brick
/// image editor.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.gluster.org/en/latest/Administrator-Guide/Setting-Up-Volumes/</c> — a volume is a logical collection of export-directory bricks</description></item>
///   <item><description><c>https://docs.gluster.org/en/latest/Administrator-Guide/GlusterFS-Introduction/</c> — backing filesystems must support extended attributes</description></item>
///   <item><description><c>https://docs.gluster.org/en/latest/Administrator-Guide/Managing-Volumes/</c> — remove-brick/rebalance/fix-layout are volume operations</description></item>
///   <item><description><c>https://docs.kernel.org/filesystems/ext4/attributes.html</c> — ext4 xattr on-disk layout</description></item>
///   <item><description><c>https://www.kernel.org/pub/linux/utils/fs/xfs/docs/xfs_filesystem_structure.pdf</c> — XFS short-form attribute layout</description></item>
///   <item><description><c>https://github.com/gluster/glusterfs</c> — canonical GlusterFS implementation, dual GPLv2/LGPLv3+</description></item>
/// </list>
/// </summary>
public sealed class GlusterFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveShrinkable {

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
    "GlusterFS single-brick R/O backing-store view via XFS/ext delegation. Native backing " +
    "accessors read trusted.gfid/trusted.glusterfs.* and mutate inline/short-form xattrs. " +
    "General writes, defrag, wipe, layout and purge stay disabled until all xattr storage " +
    "forms are safe. Shrink-to-fit is supported for ext-backed bricks via the allocation " +
    "bitmap; XFS-backed bricks copy through unchanged. Cluster rebalance, fix-layout and " +
    "remove-brick are intentionally out of scope.";

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

  /// <summary>
  /// Shrinks an ext-backed brick to its highest allocated block without rebuilding
  /// file or xattr metadata. XFS-backed bricks are copied unchanged.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("GlusterFS shrink requires a readable, seekable input.", nameof(input));
    if (!output.CanRead || !output.CanWrite || !output.CanSeek)
      throw new ArgumentException("GlusterFS shrink requires a readable, writable, seekable output.", nameof(output));

    Stream target;
    if (ReferenceEquals(input, output)) {
      target = output;
    } else {
      input.Position = 0;
      output.Position = 0;
      output.SetLength(0);
      input.CopyTo(output);
      output.Flush();
      target = output;
    }

    if (!IsExtBacking(target)) {
      target.Position = 0;
      return;
    }

    target.Position = 0;
    try {
      ExtInPlaceShrinker.ShrinkToFit(target);
    } catch (NotSupportedException) {
      // Some valid ext geometries cannot be reduced by the conservative in-place
      // shrinker. Leaving the already-copied image unchanged is the safe result.
    }
    target.Position = 0;
  }

  private static bool IsExtBacking(Stream image) {
    const long magicOffset = 1024 + 56;
    if (image.Length < magicOffset + sizeof(ushort)) return false;
    var original = image.Position;
    Span<byte> magic = stackalloc byte[sizeof(ushort)];
    try {
      image.Position = magicOffset;
      image.ReadExactly(magic);
      return BinaryPrimitives.ReadUInt16LittleEndian(magic) == 0xEF53;
    } finally {
      image.Position = original;
    }
  }
}
