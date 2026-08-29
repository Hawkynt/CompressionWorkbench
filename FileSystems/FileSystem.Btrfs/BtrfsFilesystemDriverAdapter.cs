#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Btrfs;

/// <summary>
/// Native read-only Btrfs driver for single-device, single-stripe volumes whose
/// namespace and EXTENT_DATA records are completely understood. Native inode IDs
/// are retained across aliases; a global logical extent map provides true
/// positional reads, including holes and preallocated unwritten ranges, without
/// depending on the archive reader's leaf-local extraction order.
/// </summary>
public sealed class BtrfsFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  private static readonly FilesystemNodeId RootId = new(256, 0);
  public string FormatId => "Btrfs";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      using var reader = new BtrfsReader(image, leaveOpen: true);
      var records = reader.Entries.ToArray();
      ValidateNamespace(records);
      var map = BtrfsNativeFileMap.Read(image, records);

      return new FilesystemDriverProfile(
        FormatId,
        $"Btrfs native single-device extent reader ({map.NodeSize:N0}-byte nodes)",
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.HardLinks |
        FilesystemDriverCapabilities.SparseFiles |
        FilesystemDriverCapabilities.CaseSensitiveNames |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,
        [
          "Native Btrfs inode object IDs are used as path-independent identities; the root directory uses native object id 256.",
          "EXTENT_DATA records are merged globally across all FS-tree leaves before reads; missing ranges, explicit sparse extents and prealloc extents return zeroes.",
          "Only single-device, single-stripe chunks with uncompressed/unencrypted/unencoded inline or regular/prealloc extents are accepted; RAID and compressed profiles fail closed.",
          "Mounted writes remain disabled until CoW tree mutation, delayed refs, checksum/free-space trees, transaction generation and log-tree replay are one crash-consistent core.",
        ]);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        FormatId,
        "unsupported or damaged Btrfs native profile",
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
        "Btrfs mounted writes require CoW tree publication, delayed refs, checksum/free-space updates, transaction generations and log-tree recovery; offline archive mutation is not sufficient.");
    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("Btrfs image is not accepted by the native mounted profile: " + string.Join("; ", profile.Limitations));
    return new BtrfsReadOnlyFilesystemSession(image, profile, options.LeaveOpen);
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
      blockers.Add("Implement fs/root/chunk/extent/checksum/free-space tree insert/delete/split/merge as copy-on-write path updates rather than in-place archive edits.");
      blockers.Add("Implement extent refs/backrefs, delayed refs, block-group accounting, free-space tree/cache and data checksums for allocation and truncation.");
      blockers.Add("Publish new tree roots under one transaction generation and rotate all superblock mirrors only after the new graph is durable.");
      blockers.Add("Implement log-tree/fsync intent publication and replay so synchronous namespace/data changes survive before the main transaction commits.");
      blockers.Add("Cover subvolumes, snapshots, reflinks/shared extents, compression, RAID/DUP profiles, qgroups, send/receive, device replace/scrub and zoned profiles.");
      blockers.Add("Add inode/dentry/tree locking plus crash fault-injection and btrfs check/kernel-mount interoperability corpora before enabling mounted mutation.");
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

  private static void ValidateNamespace(IReadOnlyList<BtrfsEntry> entries) {
    var paths = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in entries) {
      var path = Normalize(entry.Name);
      if (path.Length == 0 || !paths.Add(path))
        throw new InvalidDataException($"Btrfs namespace contains an empty or duplicate path '{path}'.");
      if (entry.Inode <= 0)
        throw new InvalidDataException($"Btrfs entry '{path}' has invalid inode {entry.Inode}.");
      if (entry.Size < 0)
        throw new InvalidDataException($"Btrfs entry '{path}' has a negative logical size.");
    }
  }

  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}

internal sealed class BtrfsReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public BtrfsReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("Btrfs mounted reads require a readable, seekable image.", nameof(image));
    _image = image;
    _leaveOpen = leaveOpen;
    using var reader = new BtrfsReader(image, leaveOpen: true);
    var records = reader.Entries.ToArray();
    var map = BtrfsNativeFileMap.Read(image, records);
    var (nodes, links) = BuildNamespace(records, map);
    _namespace = new ReadOnlyFilesystemSnapshotSession(profile, new FilesystemNodeId(256, 0), nodes, links);
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
    if (!_leaveOpen) _image.Dispose();
  }

  private (FilesystemSnapshotNode[] Nodes, FilesystemSnapshotDirectoryEntry[] Links) BuildNamespace(
      IReadOnlyList<BtrfsEntry> records,
      BtrfsNativeFileMap.Map map) {
    var aliases = records.GroupBy(r => r.Inode).ToDictionary(g => g.Key, g => checked((uint)g.Count()));
    var rootId = new FilesystemNodeId(256, 0);
    var nodes = new Dictionary<long, FilesystemSnapshotNode> {
      [256] = new(rootId, default, string.Empty, FilesystemNodeKind.Directory, 0, 0),
    };
    var links = new List<FilesystemSnapshotDirectoryEntry>(records.Count);
    var pathToNode = new Dictionary<string, FilesystemNodeId>(StringComparer.Ordinal) { [string.Empty] = rootId };

    foreach (var record in records.OrderBy(r => Depth(r.Name)).ThenBy(r => r.Name, StringComparer.Ordinal)) {
      var path = Normalize(record.Name);
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var name = slash < 0 ? path : path[(slash + 1)..];
      if (!pathToNode.TryGetValue(parentPath, out var parent))
        throw new InvalidDataException($"Btrfs entry '{path}' has no decoded parent '{parentPath}'.");

      var nodeId = new FilesystemNodeId(checked((ulong)record.Inode), 0);
      var kind = record.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile;
      if (!nodes.TryGetValue(record.Inode, out var existing)) {
        Func<IFilesystemFileHandle>? open = null;
        long allocated = 0;
        if (kind == FilesystemNodeKind.RegularFile) {
          if (!map.Files.TryGetValue(record.Inode, out var segments))
            throw new InvalidDataException($"Btrfs inode {record.Inode} has no native file map.");
          allocated = segments.Where(s => !s.IsZero).Sum(s => s.Length);
          var captured = segments;
          open = () => new BtrfsMappedFileHandle(nodeId, _image, _ioGate, record.Size, captured);
        }
        nodes[record.Inode] = new FilesystemSnapshotNode(
          nodeId,
          parent,
          name,
          kind,
          kind == FilesystemNodeKind.Directory ? 0 : record.Size,
          allocated,
          LinkCount: aliases[record.Inode],
          Modified: ToOffset(record.LastModified),
          OpenReadHandle: open);
      } else if (existing.Kind != kind || existing.Size != (kind == FilesystemNodeKind.Directory ? 0 : record.Size)) {
        throw new InvalidDataException($"Btrfs aliases for inode {record.Inode} disagree on object metadata.");
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

internal sealed class BtrfsMappedFileHandle : IFilesystemFileHandle {
  private readonly Stream _image;
  private readonly object _gate;
  private readonly long _length;
  private readonly BtrfsNativeFileMap.Segment[] _segments;
  private bool _disposed;

  public BtrfsMappedFileHandle(
      FilesystemNodeId nodeId,
      Stream image,
      object gate,
      long length,
      BtrfsNativeFileMap.Segment[] segments) {
    NodeId = nodeId;
    _image = image;
    _gate = gate;
    _length = Math.Max(0, length);
    _segments = segments;
  }

  public FilesystemNodeId NodeId { get; }
  public long Length { get { ThrowIfDisposed(); return _length; } }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (destination.Length == 0 || offset >= _length) return 0;

    var totalWanted = checked((int)Math.Min(destination.Length, _length - offset));
    var output = destination[..totalWanted];
    var logical = offset;
    var written = 0;
    var segmentIndex = 0;
    while (segmentIndex < _segments.Length && _segments[segmentIndex].End <= logical) segmentIndex++;

    while (written < totalWanted) {
      var remaining = totalWanted - written;
      if (segmentIndex >= _segments.Length) {
        output.Slice(written, remaining).Clear();
        written += remaining;
        break;
      }

      var segment = _segments[segmentIndex];
      if (logical < segment.FileOffset) {
        var zero = checked((int)Math.Min(remaining, segment.FileOffset - logical));
        output.Slice(written, zero).Clear();
        logical += zero;
        written += zero;
        continue;
      }
      if (logical >= segment.End) {
        segmentIndex++;
        continue;
      }

      var within = logical - segment.FileOffset;
      var take = checked((int)Math.Min(remaining, segment.Length - within));
      if (segment.IsZero) {
        output.Slice(written, take).Clear();
      } else {
        lock (_gate) {
          _image.Position = checked(segment.PhysicalOffset!.Value + within);
          var done = 0;
          while (done < take) {
            var n = _image.Read(output.Slice(written + done, take - done));
            if (n == 0) throw new EndOfStreamException("Btrfs physical extent ended before its decoded logical range.");
            done += n;
          }
        }
      }
      logical += take;
      written += take;
      if (logical >= segment.End) segmentIndex++;
    }
    return written;
  }

  public void Write(long offset, ReadOnlySpan<byte> source) => throw new NotSupportedException("Btrfs native mapped handle is read-only.");
  public void SetLength(long length) => throw new NotSupportedException("Btrfs native mapped handle is read-only.");
  public void Flush() => ThrowIfDisposed();
  public void Dispose() => _disposed = true;
  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
