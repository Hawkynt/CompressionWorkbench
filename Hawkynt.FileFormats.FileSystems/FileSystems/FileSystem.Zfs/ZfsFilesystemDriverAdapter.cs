#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Zfs;

/// <summary>
/// Native read-only ZFS sidecar for the repository's v28 single-vdev profile.
/// Dataset dnode object IDs are preserved as path-independent identities and
/// file bytes are streamed through <see cref="ZfsReader"/>, which validates the
/// Fletcher-4 checksum of every block it follows.  The driver rejects compressed
/// or hole-bearing data mappings instead of exposing the raw physical bytes as
/// logical file contents.
/// </summary>
public sealed class ZfsFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "Zfs";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var geometry = ZfsDriverGeometry.Parse(image);
      using var reader = new ZfsReader(image, leaveOpen: true);
      var layout = ZfsLayout.Read(image)
        ?? throw new InvalidDataException("ZFS block-pointer layout could not be resolved.");
      ValidateEntries(image, reader.Entries, layout);

      return new FilesystemDriverProfile(
        FormatId,
        $"ZFS v{geometry.Version} native dnode reader",
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.HardLinks |
        FilesystemDriverCapabilities.CaseSensitiveNames |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,
        [
          $"Dataset dnode object IDs are preserved as native node identities; selected uberblock TXG is {geometry.Txg}.",
          "Regular files are accepted only when their complete logical data is covered by uncompressed, non-hole block pointers understood by ZfsReader/ZfsLayout.",
          "Fletcher-4 verification remains on the read path; positional file handles spool the checksum-verified logical stream and never require a whole-file byte array.",
          "The native profile is intentionally limited to legacy v28 single-vdev semantics; feature-flag pools, RAID-Z/mirror repair, encryption and advanced compression remain outside this mount target.",
          "Mounted writes remain disabled until TXG publication, metaslab/spacemap ownership, ZIL replay and complete DSL/ZAP/dnode mutation share one crash-consistent transaction core.",
        ]);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        FormatId,
        "unsupported or damaged ZFS native profile",
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
    if (!options.ReadOnly)
      throw new NotSupportedException(
        "ZFS mounted writes require TXG/ZIL publication, metaslab/spacemap allocation and complete CoW DSL/ZAP/dnode mutation; offline archive modification is not a mounted transaction model.");

    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("ZFS image is not accepted by the native mounted profile: " + string.Join("; ", profile.Limitations));
    return new ZfsReadOnlyFilesystemSession(image, profile, options.LeaveOpen);
  }

  public FilesystemDriverProfile ProbeFilesystem(IRandomAccessBlockDevice device) {
    ArgumentNullException.ThrowIfNull(device);
    using var stream = new BlockDeviceStream(device, leaveOpen: true);
    return ProbeFilesystem(stream);
  }

  public IFilesystemSession OpenFilesystem(IRandomAccessBlockDevice device, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(device);
    ArgumentNullException.ThrowIfNull(options);
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
      FilesystemDriverReadinessLayer.NativeStableNodeIds |
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
        FilesystemDriverReadinessLayer.AllocationMap |
        FilesystemDriverReadinessLayer.Links |
        FilesystemDriverReadinessLayer.ValidationCorpus
      : FilesystemDriverReadinessLayer.None;
    var blockers = new List<string>(profile.Limitations);
    if (target == FilesystemDriverTarget.ReadWrite) {
      blockers.Add("Implement gang blocks, holes, ditto copies, mirrors/RAID-Z reconstruction and all enabled compression/checksum algorithms as logical block mappings.");
      blockers.Add("Implement dnode bonus/SA, ZAP micro/fat update, directory link/unlink/rename, hard links, symlinks, xattrs, ACLs and exact ZPL metadata semantics.");
      blockers.Add("Allocate/free through metaslabs, space maps and deferred frees while preserving birth TXGs and reference ownership for snapshots/clones.");
      blockers.Add("Publish dirty object sets through a new TXG and uberblock only after all child blocks/checksums are durable; never overwrite the active graph in place.");
      blockers.Add("Implement ZIL intent-log emission, commit records and replay ordering so synchronous mutations survive crashes before their TXG reaches the uberblock ring.");
      blockers.Add("Cover feature-flag pools, snapshots/clones, encryption, send/receive metadata, multi-vdev topologies and crash fault-injection with zpool import/scrub interoperability tests.");
    }

    var required = target == FilesystemDriverTarget.ReadOnly ? readRequired : writeRequired;
    return new FilesystemDriverReadinessReport(
      FormatId,
      target,
      available,
      required,
      profile.CanMount && (available & required) == required,
      UsesNativeProvider: true,
      blockers.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static void ValidateEntries(Stream image, IReadOnlyList<ZfsEntry> entries, ZfsLayout.Layout layout) {
    var paths = new HashSet<string>(StringComparer.Ordinal);
    var dataByOwner = layout.DataBlocks
      .GroupBy(b => Normalize(b.Owner), StringComparer.Ordinal)
      .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

    foreach (var entry in entries) {
      var path = Normalize(entry.Name);
      if (path.Length == 0 || !paths.Add(path))
        throw new InvalidDataException($"ZFS namespace contains an empty or duplicate path '{path}'.");
      if (entry.ObjectId == 0)
        throw new InvalidDataException($"ZFS entry '{path}' has object id zero.");
      if (entry.Size < 0)
        throw new InvalidDataException($"ZFS entry '{path}' has a negative logical size.");
      if (entry.IsDirectory || entry.Size == 0) continue;

      if (!dataByOwner.TryGetValue(path, out var blocks) || blocks.Length == 0)
        throw new NotSupportedException($"ZFS file '{path}' has no completely decoded physical data mapping.");

      long logicalCoverage = 0;
      foreach (var block in blocks.OrderBy(b => b.Offset)) {
        var props = ReadUInt64(image, checked(block.PointerOffset + 0x30));
        var logicalSectors = (props & 0xFFFFUL) + 1;
        var compression = (byte)((props >> 32) & 0x7F);
        var checksum = (byte)((props >> 40) & 0xFF);
        if (compression != ZfsConstants.ZioCompressOff)
          throw new NotSupportedException($"ZFS file '{path}' uses compression id {compression}; the native driver currently accepts uncompressed records only.");
        if (checksum != ZfsConstants.ZioChecksumFletcher4)
          throw new NotSupportedException($"ZFS file '{path}' uses checksum id {checksum}; the native profile currently requires Fletcher-4.");
        logicalCoverage = checked(logicalCoverage + checked((long)logicalSectors * ZfsConstants.SectorSize));
      }
      if (logicalCoverage < entry.Size)
        throw new NotSupportedException(
          $"ZFS file '{path}' has only {logicalCoverage:N0} decoded logical bytes for a {entry.Size:N0}-byte file (hole/gang/unsupported mapping)." );
    }
  }

  private static ulong ReadUInt64(Stream image, long offset) {
    Span<byte> bytes = stackalloc byte[8];
    var original = image.Position;
    try {
      image.Position = offset;
      image.ReadExactly(bytes);
      return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    } finally {
      image.Position = original;
    }
  }

  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}

internal sealed class ZfsReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly ZfsReader _reader;
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public ZfsReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ZFS mounted reads require a readable, seekable image.", nameof(image));
    _image = image;
    _leaveOpen = leaveOpen;
    _reader = new ZfsReader(image, leaveOpen: true);
    var records = _reader.Entries.ToArray();
    var rootId = new FilesystemNodeId(0, 0); // synthetic namespace root; dataset objects retain native dnode IDs.
    var (nodes, links) = BuildNamespace(records, rootId);
    _namespace = new ReadOnlyFilesystemSnapshotSession(profile, rootId, nodes, links);
  }

  public FilesystemDriverProfile Profile => _namespace.Profile;
  public FilesystemNodeId RootNodeId => _namespace.RootNodeId;
  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) => _namespace.Stat(nodeId);
  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) => _namespace.Lookup(parentDirectory, name);
  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) => _namespace.Enumerate(directory);
  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) => _namespace.OpenFile(nodeId, access);
  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) => _namespace.CreateFile(parentDirectory, name);
  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) => _namespace.CreateDirectory(parentDirectory, name);
  public void DeleteFile(FilesystemNodeId parentDirectory, string name) => _namespace.DeleteFile(parentDirectory, name);
  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) => _namespace.RemoveDirectory(parentDirectory, name);
  public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace) => _namespace.Rename(oldParent, oldName, newParent, newName, replace);
  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName) => _namespace.CreateHardLink(existingNode, newParent, newName);
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target) => _namespace.CreateSymbolicLink(parentDirectory, name, target);
  public string ReadSymbolicLink(FilesystemNodeId nodeId) => _namespace.ReadSymbolicLink(nodeId);
  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) => _namespace.SetMetadata(nodeId, patch);
  public void Flush() => _namespace.Flush();
  public IFilesystemTransaction BeginTransaction() => _namespace.BeginTransaction();

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    _namespace.Dispose();
    _reader.Dispose();
    if (!_leaveOpen) _image.Dispose();
  }

  private (FilesystemSnapshotNode[] Nodes, FilesystemSnapshotDirectoryEntry[] Links) BuildNamespace(
      IReadOnlyList<ZfsEntry> records,
      FilesystemNodeId rootId) {
    var aliases = records.GroupBy(r => r.ObjectId).ToDictionary(g => g.Key, g => checked((uint)g.Count()));
    var nodes = new Dictionary<ulong, FilesystemSnapshotNode> {
      [0] = new(rootId, default, string.Empty, FilesystemNodeKind.Directory, 0, 0),
    };
    var links = new List<FilesystemSnapshotDirectoryEntry>(records.Count);
    var pathToNode = new Dictionary<string, FilesystemNodeId>(StringComparer.Ordinal) { [string.Empty] = rootId };

    foreach (var record in records.OrderBy(r => Depth(r.Name)).ThenBy(r => r.Name, StringComparer.Ordinal)) {
      var path = Normalize(record.Name);
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var name = slash < 0 ? path : path[(slash + 1)..];
      if (!pathToNode.TryGetValue(parentPath, out var parent))
        throw new InvalidDataException($"ZFS entry '{path}' has no decoded parent '{parentPath}'.");

      var nodeId = new FilesystemNodeId(record.ObjectId, 0);
      var kind = record.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile;
      if (!nodes.TryGetValue(record.ObjectId, out var existing)) {
        Func<IFilesystemFileHandle>? open = null;
        if (kind == FilesystemNodeKind.RegularFile) {
          var captured = record;
          open = () => SpoolingReadOnlyFileHandle.Create(
            nodeId,
            captured.Size,
            output => {
              long written;
              lock (_ioGate) written = _reader.ExtractTo(captured, output);
              if (written != captured.Size)
                throw new InvalidDataException($"ZFS dnode {captured.ObjectId} yielded {written:N0} of {captured.Size:N0} logical bytes.");
            });
        }

        nodes[record.ObjectId] = new FilesystemSnapshotNode(
          nodeId,
          parent,
          name,
          kind,
          kind == FilesystemNodeKind.Directory ? 0 : record.Size,
          0,
          LinkCount: aliases[record.ObjectId],
          Modified: ToOffset(record.LastModified),
          OpenReadHandle: open);
      } else if (existing.Kind != kind || existing.Size != (kind == FilesystemNodeKind.Directory ? 0 : record.Size)) {
        throw new InvalidDataException($"ZFS aliases for object {record.ObjectId} disagree on object metadata.");
      }

      links.Add(new FilesystemSnapshotDirectoryEntry(parent, name, nodeId));
      if (kind == FilesystemNodeKind.Directory) pathToNode[path] = nodeId;
    }

    return (nodes.Values.ToArray(), links.ToArray());
  }

  private static int Depth(string path) => Normalize(path).Count(c => c == '/');
  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
  private static DateTimeOffset? ToOffset(DateTime? value)
    => value == null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), TimeSpan.Zero);
}

internal readonly record struct ZfsDriverGeometry(ulong Version, ulong Txg) {
  public static ZfsDriverGeometry Parse(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ZFS driver probing requires a readable, seekable stream.", nameof(image));
    if (image.Length < ZfsConstants.LabelSize)
      throw new InvalidDataException("ZFS image is too small for a vdev label.");

    var original = image.Position;
    try {
      Span<byte> slot = stackalloc byte[40];
      ulong bestTxg = 0;
      ulong bestVersion = 0;
      var found = false;
      for (long at = ZfsConstants.UberblockArrayOffset;
           at + ZfsConstants.UberblockSize <= ZfsConstants.LabelSize;
           at += ZfsConstants.UberblockSize) {
        image.Position = at;
        image.ReadExactly(slot);
        if (BinaryPrimitives.ReadUInt64LittleEndian(slot) != ZfsConstants.UberblockMagic) continue;
        var version = BinaryPrimitives.ReadUInt64LittleEndian(slot[8..]);
        var txg = BinaryPrimitives.ReadUInt64LittleEndian(slot[16..]);
        if (!found || txg > bestTxg) {
          found = true;
          bestTxg = txg;
          bestVersion = version;
        }
      }
      if (!found) throw new InvalidDataException("ZFS vdev label contains no valid uberblock magic.");
      if (bestVersion != ZfsConstants.PoolVersion)
        throw new NotSupportedException($"ZFS pool version {bestVersion} is outside the native v{ZfsConstants.PoolVersion} mounted profile.");
      return new ZfsDriverGeometry(bestVersion, bestTxg);
    } finally {
      image.Position = original;
    }
  }
}
