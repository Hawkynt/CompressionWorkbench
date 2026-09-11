#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Xfs;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Cxfs;

/// <summary>
/// R/W descriptor for SGI CXFS filesystem images.
///
/// <para>SGI documents CXFS as using the same filesystem structure as XFS and
/// creating the filesystem with the same <c>mkfs</c> command. CXFS clustering,
/// metadata-server selection and mount policy live in the external cluster
/// database / XVM management layer rather than in a different file/directory
/// disk format. Consequently all offline filesystem-image operations delegate
/// to the XFS backend.</para>
///
/// <para>This descriptor deliberately does not claim to author the surrounding
/// CXFS cluster database, XVM volume definition, fencing policy or metadata
/// server configuration. It handles the filesystem image that CXFS places on
/// that storage.</para>
///
/// <para>Extension-only detection (<c>.cxfs</c>) avoids first-match collision
/// with <see cref="XfsFormatDescriptor"/> because CXFS has no distinct filesystem
/// magic: both are XFS on disk.</para>
///
/// References:
/// <list type="bullet">
///   <item><description>SGI "CXFS Administration Guide" — "CXFS uses the same filesystem structure as XFS" and is created with the same <c>mkfs</c></description></item>
///   <item><description><c>https://mirrors.edge.kernel.org/pub/linux/utils/fs/xfs/docs/xfs_filesystem_structure.pdf</c> — "XFS Algorithms &amp; Data Structures"</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/xfs</c> — Linux XFS reference implementation, consulted as a behavioral reference only</description></item>
/// </list>
/// </summary>
public sealed class CxfsFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveShrinkable,
  IArchiveWriteConstraints,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IFilesystemExtentMap,
  IFilesystemBlockMover,
  IWipeEmpty,
  IFormatOptionsSchema,
  ILayoutOptimizable {

  private readonly XfsFormatDescriptor _xfs = new();

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => this._xfs.OptionsSchema;

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => this._xfs.EnumerateExtents(image);

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => this._xfs.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => this._xfs.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  /// <inheritdoc />
  public void Defragment(Stream archive)
    => this._xfs.Defragment(archive);

  /// <inheritdoc />
  public void Defragment(Stream archive, DefragOptions options)
    => this._xfs.Defragment(archive, options);

  /// <inheritdoc />
  public long? MaxTotalArchiveSize => this._xfs.MaxTotalArchiveSize;

  /// <inheritdoc />
  public long? MinTotalArchiveSize => this._xfs.MinTotalArchiveSize;

  /// <inheritdoc />
  public string AcceptedInputsDescription =>
    "CXFS filesystem image (same on-disk filesystem structure as XFS); cluster database and XVM configuration are external.";

  /// <inheritdoc />
  public bool CanAccept(ArchiveInputInfo input, out string? reason)
    => this._xfs.CanAccept(input, out reason);

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true)
    => this._xfs.WipeUnusedSpace(image, wipeClusterTips, wipeDeletedEntries);

  /// <inheritdoc />
  public void Shrink(Stream input, Stream output)
    => ((IArchiveShrinkable)this._xfs).Shrink(input, output);

  /// <inheritdoc />
  public LayoutAnalysis AnalyzeLayout(Stream image)
    => ((ILayoutOptimizable)this._xfs).AnalyzeLayout(image);

  /// <inheritdoc />
  public LayoutReclaim ReclaimSupport => ((ILayoutOptimizable)this._xfs).ReclaimSupport;

  /// <inheritdoc />
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options)
    => ((ILayoutOptimizable)this._xfs).RebuildStreaming(source, target, options);

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
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
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
  // CXFS has no distinct filesystem signature: the filesystem is XFS on disk.
  // Extension-only detection prevents a first-match fight with FileSystem.Xfs.
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
    "SGI CXFS filesystem image — R/W via the XFS filesystem backend because SGI defines CXFS as using the same filesystem structure as XFS. " +
    "CXFS cluster database, XVM topology, fencing and metadata-server configuration are external and are not represented by this image descriptor.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new CxfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new CxfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    using var r = new CxfsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase))
      ?? throw new FileNotFoundException($"CXFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => this._xfs.Create(output, inputs, options);

  /// <inheritdoc />
  public void CreateFromStreams(Stream output, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options)
    => this._xfs.CreateFromStreams(output, inputs, options);

  /// <inheritdoc />
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => this._xfs.Add(archive, inputs);

  /// <inheritdoc />
  public void Remove(Stream archive, string[] entryNames)
    => this._xfs.Remove(archive, entryNames);
}
