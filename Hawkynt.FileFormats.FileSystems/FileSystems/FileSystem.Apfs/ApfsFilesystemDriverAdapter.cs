#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// Native APFS read-only sidecar for the fully decoded single-extent profile.
/// It uses APFS inode/object IDs directly and serves regular-file reads from the
/// physical FILE_EXTENT mapping without passing through archive extraction.
///
/// The sidecar intentionally requires the repository writer's verified
/// container-OMAP physical hint. ApfsReader can also heuristically discover OMAP
/// objects on broader real-world images, but that is not enough evidence to call
/// encrypted, multi-extent, fusion or otherwise advanced profiles mount-grade.
/// Those remain available through the conservative archive projection until the
/// corresponding APFS transaction/object-map semantics are proven.
/// </summary>
public sealed class ApfsFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "Apfs";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var geometry = ApfsDriverGeometry.ParseWriterProfile(image);
      using var reader = new ApfsReader(image, leaveOpen: true);
      ValidateEntries(reader.Entries, reader.BlockSize, image.Length);
      return new FilesystemDriverProfile(
        FormatId,
        "APFS native writer-profile single-extent reader",
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.HardLinks |
        FilesystemDriverCapabilities.SymbolicLinks |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,
        [
          $"Native APFS object IDs are used as path-independent node identities; block size is {geometry.BlockSize:N0} bytes.",
          "Regular files are accepted only when one decoded physical FILE_EXTENT covers the entire logical file; multi-extent/compressed profiles fail closed.",
          "This native path is restricted to the repository writer's verified OMAP-hinted profile; broader discovered real-world profiles continue through the conservative read-only projection.",
          "Writable mounting requires atomic checkpoint/OMAP/spaceman/extent-reference publication plus snapshot/clone/encryption semantics and is not inferred from offline image modification.",
        ]);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        FormatId,
        "unsupported APFS native profile",
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
        "APFS mounted writes require transactional CoW publication through OMAP/spaceman/checkpoints and complete snapshot/clone semantics.");
    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("APFS image is not accepted by the native mounted profile: " + string.Join("; ", profile.Limitations));
    return new ApfsReadOnlyFilesystemSession(image, profile, options.LeaveOpen);
  }

  public FilesystemDriverProfile ProbeFilesystem(IRandomAccessBlockDevice device) {
    ArgumentNullException.ThrowIfNull(device);
    using var stream = new BlockDeviceStream(device, leaveOpen: true);
    return ProbeFilesystem(stream);
  }

  public IFilesystemSession OpenFilesystem(IRandomAccessBlockDevice device, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(device);
    var stream = new BlockDeviceStream(device, leaveOpen: false);
    try {
      return OpenFilesystem(stream, options with { LeaveOpen = false });
    } catch {
      stream.Dispose();
      throw;
    }
  }

  public FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(Stream image, FilesystemDriverTarget target) {
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
      blockers.Add("Implement arbitrary multi-extent, sparse, compressed and clone-aware dstream mapping with extent-reference ownership/refcounts.");
      blockers.Add("Implement inode/DREC/xattr/sibling-link B-tree insert/delete/split/merge with APFS name normalization/hash and hard-link semantics.");
      blockers.Add("Allocate through spaceman/CIB/CAB/free-queue structures and update volume/container object maps without overwriting the active transaction.");
      blockers.Add("Publish new APSB/NXSB checkpoint descriptors/maps and transaction IDs atomically, retaining the old checkpoint until the new graph is durable.");
      blockers.Add("Implement snapshot metadata, clones/reflinks, encrypted crypto states/keybags, sealed/system volume roles and fusion/multi-device mapping before enabling those profiles.");
      blockers.Add("Add crash fault-injection and fsck_apfs/mount interoperability corpora plus concurrent inode/dentry/transaction locking.");
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

  private static void ValidateEntries(IReadOnlyList<ApfsEntry> entries, uint blockSize, long imageLength) {
    var paths = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in entries) {
      var path = Normalize(entry.Name);
      if (path.Length == 0 || !paths.Add(path))
        throw new InvalidDataException($"APFS namespace contains an empty or duplicate path '{path}'.");
      if (entry.ObjectId < APFS_MIN_USER_INO_NUM)
        throw new InvalidDataException($"APFS user namespace exposed reserved object id {entry.ObjectId} as '{path}'.");
      if (entry.Size < 0)
        throw new InvalidDataException($"APFS entry '{path}' has a negative logical size.");
      if (entry.IsSymlink && entry.LinkTarget == null)
        throw new NotSupportedException($"APFS symlink '{path}' has no decoded embedded target.");
      if (entry.IsDirectory || entry.IsSymlink || entry.Size == 0) continue;
      if (entry.FirstBlock == 0 || entry.ExtentLength < entry.Size)
        throw new NotSupportedException(
          $"APFS file '{path}' is not completely covered by the decoded single FILE_EXTENT profile.");
      var offset = checked((long)entry.FirstBlock * blockSize);
      if (offset < 0 || offset > imageLength - entry.Size)
        throw new InvalidDataException($"APFS file '{path}' extent lies outside the image.");
    }
  }

  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}

internal sealed class ApfsReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public ApfsReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    _image = image;
    _leaveOpen = leaveOpen;
    using var reader = new ApfsReader(image, leaveOpen: true);
    var records = reader.Entries.ToArray();
    var blockSize = reader.BlockSize;
    var rootId = new FilesystemNodeId(APFS_ROOT_DIR_INO_NUM, 0);
    var (nodes, links) = BuildNamespace(records, rootId, blockSize);
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
    if (!_leaveOpen) _image.Dispose();
  }

  private (FilesystemSnapshotNode[] Nodes, FilesystemSnapshotDirectoryEntry[] Links) BuildNamespace(
      IReadOnlyList<ApfsEntry> records,
      FilesystemNodeId rootId,
      uint blockSize) {
    var aliases = records.GroupBy(r => r.ObjectId).ToDictionary(g => g.Key, g => checked((uint)g.Count()));
    var nodes = new Dictionary<ulong, FilesystemSnapshotNode> {
      [APFS_ROOT_DIR_INO_NUM] = new(rootId, default, string.Empty, FilesystemNodeKind.Directory, 0, 0),
    };
    var links = new List<FilesystemSnapshotDirectoryEntry>(records.Count);
    var pathToNode = new Dictionary<string, FilesystemNodeId>(StringComparer.Ordinal) { [string.Empty] = rootId };

    foreach (var record in records.OrderBy(r => Depth(r.Name)).ThenBy(r => r.Name, StringComparer.Ordinal)) {
      var path = Normalize(record.Name);
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var name = slash < 0 ? path : path[(slash + 1)..];
      if (!pathToNode.TryGetValue(parentPath, out var parent))
        throw new InvalidDataException($"APFS entry '{path}' has no decoded parent '{parentPath}'.");
      var nodeId = new FilesystemNodeId(record.ObjectId, 0);
      var kind = record.IsDirectory ? FilesystemNodeKind.Directory
        : record.IsSymlink ? FilesystemNodeKind.SymbolicLink
        : FilesystemNodeKind.RegularFile;
      if (!nodes.TryGetValue(record.ObjectId, out var existing)) {
        Func<IFilesystemFileHandle>? open = null;
        if (kind == FilesystemNodeKind.RegularFile)
          open = () => new ApfsExtentFileHandle(nodeId, _image, _ioGate, blockSize, record.FirstBlock, record.Size);
        nodes[record.ObjectId] = new FilesystemSnapshotNode(
          nodeId,
          parent,
          name,
          kind,
          kind == FilesystemNodeKind.Directory ? 0 : record.Size,
          kind == FilesystemNodeKind.RegularFile ? record.ExtentLength : 0,
          LinkCount: aliases[record.ObjectId],
          Modified: ToOffset(record.LastModified),
          SymbolicLinkTarget: record.LinkTarget,
          OpenReadHandle: open);
      } else if (existing.Kind != kind || existing.Size != (kind == FilesystemNodeKind.Directory ? 0 : record.Size)) {
        throw new InvalidDataException($"APFS aliases for object {record.ObjectId} disagree on object metadata.");
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

internal sealed class ApfsExtentFileHandle : IFilesystemFileHandle {
  private readonly Stream _image;
  private readonly object _gate;
  private readonly long _physicalOffset;
  private readonly long _length;
  private bool _disposed;

  public ApfsExtentFileHandle(
      FilesystemNodeId nodeId,
      Stream image,
      object gate,
      uint blockSize,
      ulong firstBlock,
      long length) {
    NodeId = nodeId;
    _image = image;
    _gate = gate;
    _physicalOffset = checked((long)firstBlock * blockSize);
    _length = Math.Max(0, length);
  }

  public FilesystemNodeId NodeId { get; }
  public long Length { get { ThrowIfDisposed(); return _length; } }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (destination.Length == 0 || offset >= _length) return 0;
    var count = checked((int)Math.Min(destination.Length, _length - offset));
    lock (_gate) {
      _image.Position = checked(_physicalOffset + offset);
      var total = 0;
      while (total < count) {
        var read = _image.Read(destination.Slice(total, count - total));
        if (read == 0) throw new EndOfStreamException("APFS file extent ended before its logical size.");
        total += read;
      }
    }
    return count;
  }

  public void Write(long offset, ReadOnlySpan<byte> source) => throw new NotSupportedException("APFS native extent handle is read-only.");
  public void SetLength(long length) => throw new NotSupportedException("APFS native extent handle is read-only.");
  public void Flush() => ThrowIfDisposed();
  public void Dispose() => _disposed = true;
  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal readonly record struct ApfsDriverGeometry(uint BlockSize, ulong OmapHint) {
  public static ApfsDriverGeometry ParseWriterProfile(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("APFS driver probing requires a readable, seekable stream.", nameof(image));
    if (image.Length < 4096) throw new InvalidDataException("APFS image is too small.");
    var original = image.Position;
    Span<byte> header = stackalloc byte[4096];
    try {
      image.Position = 0;
      image.ReadExactly(header);
    } finally {
      image.Position = original;
    }
    if (BinaryPrimitives.ReadUInt32LittleEndian(header[32..36]) != 0x4253584E)
      throw new InvalidDataException("APFS NXSB magic is invalid.");
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[36..40]);
    if (blockSize is < 4096 or > 65536 || (blockSize & (blockSize - 1)) != 0)
      throw new NotSupportedException($"APFS block size {blockSize:N0} is unsupported.");
    var hint = BinaryPrimitives.ReadUInt64LittleEndian(header[3072..3080]);
    if (hint == 0)
      throw new NotSupportedException("APFS native mounted profile currently requires the repository writer's physical OMAP hint; use the derived read-only projection for broader images.");
    var offset = checked((long)hint * blockSize);
    if (offset < 0 || offset > image.Length - blockSize)
      throw new InvalidDataException("APFS OMAP hint lies outside the image.");
    Span<byte> objectHeader = stackalloc byte[32];
    try {
      image.Position = offset;
      image.ReadExactly(objectHeader);
    } finally {
      image.Position = original;
    }
    var type = BinaryPrimitives.ReadUInt32LittleEndian(objectHeader[24..28]) & OBJECT_TYPE_MASK;
    if (type != OBJECT_TYPE_OMAP)
      throw new InvalidDataException("APFS OMAP hint does not reference an OMAP object.");
    return new ApfsDriverGeometry(blockSize, hint);
  }
}
