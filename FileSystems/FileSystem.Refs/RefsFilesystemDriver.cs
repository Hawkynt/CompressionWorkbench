#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Refs;

internal static class RefsFilesystemDriver {
  private static readonly FilesystemDriverCapabilities ReadCapabilities =
    FilesystemDriverCapabilities.EnumerateDirectories |
    FilesystemDriverCapabilities.ReadData |
    FilesystemDriverCapabilities.RandomAccess |
    FilesystemDriverCapabilities.CasePreservingNames;

  public static FilesystemDriverProfile Probe(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var metadata = RefsMetadataReader.Open(image);
      _ = new RefsNamespaceReader(metadata).ReadAll();
      return new FilesystemDriverProfile(
        "Refs",
        $"ReFS {metadata.Header.MajorVersion}.{metadata.Header.MinorVersion} native namespace",
        ReadCapabilities,
        FilesystemMutationModel.CopyOnWrite,
        CanMount: true,
        CanMountWritable: false,
        [
          "Native read-only namespace and positional stream I/O are available.",
          "Native CoW B+ page/allocator/MLog/checkpoint publication exists, but mounted writes remain disabled until version-qualified redo payload encoders and recovery application are complete.",
          "Resident type-0x30 rows still need a decoded rename-stable file identity before StableNodeIds can be advertised for every node.",
        ]);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        "Refs",
        "unsupported or damaged ReFS profile",
        FilesystemDriverCapabilities.None,
        FilesystemMutationModel.None,
        CanMount: false,
        CanMountWritable: false,
        [FirstLine(e.Message)]);
    } finally {
      if (image.CanSeek) image.Position = original;
    }
  }

  public static IFilesystemSession Open(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    if (options.ReadOnly == false)
      throw new NotSupportedException(
        "ReFS writable mounting is intentionally fail-closed until native redo payload generation/replay and the complete namespace/stream mutation path are validated.");

    var profile = Probe(image);
    if (!profile.CanMount)
      throw new InvalidDataException("The ReFS image is not mountable by the native reader: " + string.Join("; ", profile.Limitations));
    return new RefsReadOnlyFilesystemSession(image, profile, options.LeaveOpen);
  }

  public static FilesystemDriverReadinessReport Readiness(Stream image, FilesystemDriverTarget target) {
    var profile = Probe(image);
    var requiredRead =
      FilesystemDriverReadinessLayer.ImageValidation |
      FilesystemDriverReadinessLayer.Namespace |
      FilesystemDriverReadinessLayer.SessionStableNodeIds |
      FilesystemDriverReadinessLayer.ReadData |
      FilesystemDriverReadinessLayer.RandomAccessRead;
    var requiredWrite = requiredRead |
      FilesystemDriverReadinessLayer.AllocationMap |
      FilesystemDriverReadinessLayer.WriteData |
      FilesystemDriverReadinessLayer.Truncate |
      FilesystemDriverReadinessLayer.NamespaceMutation |
      FilesystemDriverReadinessLayer.Flush |
      FilesystemDriverReadinessLayer.DurabilityModel |
      FilesystemDriverReadinessLayer.Concurrency;

    var available = profile.CanMount
      ? requiredRead |
        FilesystemDriverReadinessLayer.AllocationMap |
        FilesystemDriverReadinessLayer.DurabilityModel
      : FilesystemDriverReadinessLayer.None;
    var blockers = new List<string>(profile.Limitations);
    if (target == FilesystemDriverTarget.ReadWrite) {
      blockers.Add("Encode version-qualified ReFS MLog redo payloads for every namespace/stream opcode emitted by supported profiles.");
      blockers.Add("Apply supported redo payloads during dirty-volume restart instead of validating the envelope only.");
      blockers.Add("Complete create/unlink/rename/truncate/write allocation semantics including sparse, integrity, refcount, snapshot and ADS ownership cases.");
      blockers.Add("Decode rename-stable native identity for resident filename rows and preserve it across namespace mutations.");
      blockers.Add("Add mounted cache/locking/coherency rules and phase-by-phase crash fault injection.");
    }
    var required = target == FilesystemDriverTarget.ReadOnly ? requiredRead : requiredWrite;
    return new FilesystemDriverReadinessReport(
      "Refs", target, available, required,
      profile.CanMount && (available & required) == required,
      UsesNativeProvider: true,
      blockers.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}

internal sealed class RefsReadOnlyFilesystemSession : IFilesystemSession {
  private const ulong RootDirectoryOid = 0x600;
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public RefsReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ReFS mounted reads require a readable, seekable image.", nameof(image));
    _image = image;
    _leaveOpen = leaveOpen;

    var metadata = RefsMetadataReader.Open(image);
    var records = new RefsNamespaceReader(metadata).ReadAll();
    var rootId = new FilesystemNodeId(RootDirectoryOid, 1);
    var nodes = BuildNodes(metadata, records, rootId);
    _namespace = new ReadOnlyFilesystemSnapshotSession(profile, rootId, nodes);
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
  public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace)
    => _namespace.Rename(oldParent, oldName, newParent, newName, replace);
  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName)
    => _namespace.CreateHardLink(existingNode, newParent, newName);
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target)
    => _namespace.CreateSymbolicLink(parentDirectory, name, target);
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

  private IReadOnlyList<FilesystemSnapshotNode> BuildNodes(
      RefsMetadataReader metadata,
      IReadOnlyList<RefsFileRecord> records,
      FilesystemNodeId rootId) {
    var result = new List<FilesystemSnapshotNode>(records.Count + 1) {
      new(rootId, default, string.Empty, FilesystemNodeKind.Directory, 0, 0),
    };
    var byPath = new Dictionary<string, FilesystemNodeId>(StringComparer.OrdinalIgnoreCase) {
      [string.Empty] = rootId,
    };
    var usedIds = new HashSet<FilesystemNodeId> { rootId };
    ulong synthetic = 0x8000_0000_0000_0000UL;

    foreach (var record in records.OrderBy(r => Depth(r.Path)).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)) {
      var path = Normalize(record.Path);
      if (path.Length == 0) continue;
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var name = slash < 0 ? path : path[(slash + 1)..];
      if (!byPath.TryGetValue(parentPath, out var parentId))
        throw new InvalidDataException($"ReFS namespace record '{path}' has no decoded parent '{parentPath}'.");

      var candidate = TryNativeId(record, out var native)
        ? new FilesystemNodeId(native, 1)
        : new FilesystemNodeId(synthetic++, 1);
      while (!usedIds.Add(candidate)) candidate = new FilesystemNodeId(synthetic++, 1);
      byPath[path] = candidate;

      var captured = record;
      result.Add(new FilesystemSnapshotNode(
        candidate,
        parentId,
        name,
        record.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile,
        record.Size,
        record.AllocatedSize,
        LinkCount: 1,
        NativeAttributes: record.Attributes,
        Modified: ToOffset(record.Modified),
        OpenReadHandle: record.IsDirectory ? null : () => new RefsFileHandle(
          candidate, _image, _ioGate, metadata.ClusterSize, captured)));
    }
    return result;
  }

  private static bool TryNativeId(RefsFileRecord record, out ulong id) {
    if (record.Backing != null && record.Backing.FileId != 0) {
      id = record.Backing.FileId;
      return true;
    }
    id = 0;
    return false;
  }

  private static int Depth(string path) => Normalize(path).Count(c => c == '/');
  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
  private static DateTimeOffset? ToOffset(DateTime? value)
    => value == null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), TimeSpan.Zero);
}

internal sealed class RefsFileHandle : IFilesystemFileHandle {
  private readonly Stream _image;
  private readonly object _ioGate;
  private readonly int _clusterSize;
  private readonly RefsFileRecord _record;
  private bool _disposed;

  public RefsFileHandle(
      FilesystemNodeId nodeId,
      Stream image,
      object ioGate,
      int clusterSize,
      RefsFileRecord record) {
    NodeId = nodeId;
    _image = image;
    _ioGate = ioGate;
    _clusterSize = clusterSize;
    _record = record;
  }

  public FilesystemNodeId NodeId { get; }
  public long Length {
    get {
      ThrowIfDisposed();
      return Math.Max(0, _record.Size);
    }
  }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (destination.Length == 0 || offset >= Length) return 0;
    var requested = checked((int)Math.Min(destination.Length, Length - offset));

    if (_record.IsResident) {
      var content = _record.ResidentContent ?? [];
      var count = Math.Min(requested, Math.Max(0, content.Length - checked((int)offset)));
      if (count > 0) content.AsSpan(checked((int)offset), count).CopyTo(destination);
      return count;
    }

    var written = 0;
    var cursor = offset;
    var remaining = requested;
    foreach (var extent in _record.Extents.OrderBy(e => e.FileVcn)) {
      if (remaining == 0) break;
      var extentStart = checked((long)extent.FileVcn * _clusterSize);
      var extentBytes = checked((long)extent.ClusterCount * _clusterSize);
      var extentEnd = checked(extentStart + extentBytes);
      if (cursor >= extentEnd || cursor + remaining <= extentStart) continue;

      var start = Math.Max(cursor, extentStart);
      var end = Math.Min(cursor + remaining, extentEnd);
      if (start != cursor)
        throw new InvalidDataException("ReFS decoded extents do not continuously cover the requested logical range.");
      var take = checked((int)(end - start));
      if (take <= 0) continue;

      if (extent.IsSparse) {
        destination.Slice(written, take).Clear();
      } else {
        var withinExtent = start - extentStart;
        var physicalOffset = checked((long)extent.PhysicalLcn * _clusterSize + withinExtent);
        lock (_ioGate) {
          if (physicalOffset < 0 || physicalOffset > _image.Length - take)
            throw new InvalidDataException("ReFS data extent points outside the image.");
          _image.Position = physicalOffset;
          _image.ReadExactly(destination.Slice(written, take));
        }
      }
      written += take;
      cursor += take;
      remaining -= take;
    }

    if (remaining != 0)
      throw new InvalidDataException("ReFS decoded extents do not cover the requested logical range.");
    return written;
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("The ReFS filesystem session is read-only.");
  public void SetLength(long length)
    => throw new NotSupportedException("The ReFS filesystem session is read-only.");
  public void Flush() => ThrowIfDisposed();
  public void Dispose() => _disposed = true;
  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
