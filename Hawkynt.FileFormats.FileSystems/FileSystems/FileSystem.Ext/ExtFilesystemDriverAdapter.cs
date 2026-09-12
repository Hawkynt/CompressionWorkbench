#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Ext;

/// <summary>
/// Native ext2/3/4 driver sidecar. Inode numbers + i_generation form stable node
/// identities and hard-linked directory entries converge on the same node. Classic
/// ext2 profiles can additionally be opened through the direct mounted writer;
/// journaled/extents/checksummed profiles remain read-only.
/// </summary>
public sealed class ExtFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "Ext";

  private const FilesystemDriverCapabilities WritableCapabilities =
    FilesystemDriverCapabilities.WriteData |
    FilesystemDriverCapabilities.Truncate |
    FilesystemDriverCapabilities.CreateFile |
    FilesystemDriverCapabilities.DeleteFile |
    FilesystemDriverCapabilities.CreateDirectory |
    FilesystemDriverCapabilities.RemoveDirectory |
    FilesystemDriverCapabilities.Rename |
    FilesystemDriverCapabilities.HardLinks |
    FilesystemDriverCapabilities.SymbolicLinks |
    FilesystemDriverCapabilities.SetMetadata |
    FilesystemDriverCapabilities.SparseFiles |
    FilesystemDriverCapabilities.Flush;

  /// <summary>
  /// A session opened read-only reports a read-only profile: the image may well be
  /// writable, but this session will never mutate it, and the mount layer decides what
  /// it may offer from the session's own profile.
  /// </summary>
  private static FilesystemDriverProfile WithoutWriteAccess(FilesystemDriverProfile profile)
    => profile.CanMountWritable
      ? profile with {
        Capabilities = profile.Capabilities & ~WritableCapabilities,
        MutationModel = FilesystemMutationModel.None,
        CanMountWritable = false,
      }
      : profile;

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var super = ExtDriverSuperblock.Parse(image);
      super.ValidateReadableProfile(image);
      using var reader = new ExtReader(image, leaveOpen: true);
      var entries = reader.Entries.ToArray();
      ValidateEntries(image, super, entries);
      var writableLimitations = Ext2MountedFilesystemSession.GetWritableLimitations(image, super, entries);
      var canWrite = writableLimitations.Count == 0;

      var capabilities =
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.CaseSensitiveNames |
        FilesystemDriverCapabilities.CasePreservingNames;
      if (canWrite) capabilities |= WritableCapabilities;

      var limitations = new List<string> {
        "Native inode+i_generation identities and hard-link aliasing are preserved.",
        "File handles use native classic block maps for writable ext2 profiles; read-only ext3/ext4 handles continue to use the existing streaming inode/extent reader.",
      };
      if (canWrite) {
        limitations.Add("Writable mounting is intentionally limited to clean classic ext2 block-map profiles; JBD/JBD2, extent trees, indexed directory state, active xattr blocks and metadata-checksummed layouts stay fail-closed.");
        limitations.Add("Classic ext2 crash recovery remains fsck-based rather than journaled; e2fsck interoperability tests pin the post-flush consistency contract.");
      } else {
        limitations.AddRange(writableLimitations);
      }

      return new FilesystemDriverProfile(
        FormatId,
        canWrite ? "ext2 native direct mounted reader/writer" : super.ProfileName,
        capabilities,
        canWrite ? FilesystemMutationModel.Direct : FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: canWrite,
        limitations);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        FormatId,
        "unsupported or damaged ext profile",
        FilesystemDriverCapabilities.None,
        FilesystemMutationModel.None,
        CanMount: false,
        CanMountWritable: false,
        [FirstLine(e.Message)]);
    } finally {
      if (image.CanSeek) image.Position = original;
    }
  }

  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("ext image is not mountable: " + string.Join("; ", profile.Limitations));
    if (options.ReadOnly)
      return new ExtReadOnlyFilesystemSession(image, WithoutWriteAccess(profile), options.LeaveOpen);
    if (!profile.CanMountWritable)
      throw new NotSupportedException("ext image is not safe for writable mounting: " + string.Join("; ", profile.Limitations));
    if (!image.CanWrite)
      throw new ArgumentException("Writable ext2 mounting requires a writable image stream.", nameof(image));
    return new Ext2MountedFilesystemSession(image, profile, options.LeaveOpen);
  }

  public FilesystemDriverProfile ProbeFilesystem(IRandomAccessBlockDevice device) {
    using var stream = new BlockDeviceStream(device, leaveOpen: true);
    return ProbeFilesystem(stream);
  }

  public IFilesystemSession OpenFilesystem(IRandomAccessBlockDevice device, FilesystemOpenOptions options) {
    var stream = new BlockDeviceStream(device, leaveOpen: false);
    try {
      return OpenFilesystem(stream, options with { LeaveOpen = false });
    } catch {
      stream.Dispose();
      throw;
    }
  }

  public FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(
      Stream image,
      FilesystemDriverTarget target) {
    var profile = ProbeFilesystem(image);
    var readRequired =
      FilesystemDriverReadinessLayer.ImageValidation |
      FilesystemDriverReadinessLayer.Namespace |
      FilesystemDriverReadinessLayer.SessionStableNodeIds |
      FilesystemDriverReadinessLayer.ReadData |
      FilesystemDriverReadinessLayer.RandomAccessRead;
    var writeRequired = readRequired |
      FilesystemDriverReadinessLayer.AllocationMap |
      FilesystemDriverReadinessLayer.WriteData |
      FilesystemDriverReadinessLayer.Truncate |
      FilesystemDriverReadinessLayer.NamespaceMutation |
      FilesystemDriverReadinessLayer.MetadataMutation |
      FilesystemDriverReadinessLayer.Links |
      FilesystemDriverReadinessLayer.Flush |
      FilesystemDriverReadinessLayer.DurabilityModel |
      FilesystemDriverReadinessLayer.Recovery |
      FilesystemDriverReadinessLayer.Concurrency;

    var available = profile.CanMount
      ? readRequired |
        FilesystemDriverReadinessLayer.NativeStableNodeIds |
        FilesystemDriverReadinessLayer.AllocationMap
      : FilesystemDriverReadinessLayer.None;
    if (profile.CanMountWritable)
      available |=
        FilesystemDriverReadinessLayer.WriteData |
        FilesystemDriverReadinessLayer.Truncate |
        FilesystemDriverReadinessLayer.NamespaceMutation |
        FilesystemDriverReadinessLayer.MetadataMutation |
        FilesystemDriverReadinessLayer.Links |
        FilesystemDriverReadinessLayer.Flush |
        FilesystemDriverReadinessLayer.DurabilityModel |
        FilesystemDriverReadinessLayer.Recovery |
        FilesystemDriverReadinessLayer.Concurrency;

    var required = target == FilesystemDriverTarget.ReadOnly ? readRequired : writeRequired;
    var blockers = target == FilesystemDriverTarget.ReadWrite && !profile.CanMountWritable
      ? profile.Limitations
      : Array.Empty<string>();
    return new FilesystemDriverReadinessReport(
      FormatId,
      target,
      available,
      required,
      profile.CanMount && (available & required) == required,
      UsesNativeProvider: true,
      blockers.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static void ValidateEntries(
      Stream image,
      ExtDriverSuperblock super,
      IReadOnlyList<ExtEntry> entries) {
    var byInode = new Dictionary<uint, ExtDriverInode>();
    foreach (var entry in entries) {
      if (entry.Inode == 0) throw new InvalidDataException($"ext entry '{entry.Name}' has inode 0.");
      if (!byInode.TryGetValue(entry.Inode, out var inode))
        byInode[entry.Inode] = inode = super.ReadInode(image, entry.Inode);

      var expectedKind = inode.Kind;
      if (entry.IsDirectory != (expectedKind == FilesystemNodeKind.Directory)
          || entry.IsSymlink != (expectedKind == FilesystemNodeKind.SymbolicLink))
        throw new InvalidDataException($"ext inode {entry.Inode} type disagrees with directory entry '{entry.Name}'.");
      if (!entry.IsDirectory && inode.Size != entry.Size)
        throw new NotSupportedException(
          $"ext inode {entry.Inode} for '{entry.Name}' has 64-bit size {inode.Size:N0}, but the current namespace reader exposed {entry.Size:N0}; direct large-file decoding must be completed before mounting this profile.");
    }
  }

  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}