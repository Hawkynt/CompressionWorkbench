#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Fat;

internal sealed class FatReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public FatReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("FAT mounted reads require a readable, seekable image.", nameof(image));
    _image = image;
    _leaveOpen = leaveOpen;

    var geometry = FatDriverGeometry.Parse(image);
    using var reader = new FatReader(image, leaveOpen: true);
    var records = reader.Entries.ToArray();
    var root = new FilesystemNodeId(1, 1);
    var nodes = BuildNodes(records, geometry, root);
    _namespace = new ReadOnlyFilesystemSnapshotSession(profile, root, nodes);
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
      IReadOnlyList<FatEntry> entries,
      FatDriverGeometry geometry,
      FilesystemNodeId rootId) {
    var result = new List<FilesystemSnapshotNode>(entries.Count + 1) {
      new(rootId, default, string.Empty, FilesystemNodeKind.Directory, 0, 0),
    };
    var byPath = new Dictionary<string, FilesystemNodeId>(StringComparer.OrdinalIgnoreCase) {
      [string.Empty] = rootId,
    };
    ulong next = 2;

    foreach (var entry in entries.OrderBy(e => Depth(e.Name)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)) {
      var path = Normalize(entry.Name);
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var name = slash < 0 ? path : path[(slash + 1)..];
      if (!byPath.TryGetValue(parentPath, out var parent))
        throw new InvalidDataException($"FAT entry '{path}' has no decoded parent directory '{parentPath}'.");

      var nodeId = new FilesystemNodeId(next++, 1);
      byPath[path] = nodeId;
      var captured = entry;
      var allocated = entry.IsDirectory || entry.StartCluster < 2
        ? 0L
        : checked((long)geometry.ReadChain(_image, entry.StartCluster, entry.Name).Count * geometry.ClusterSize);
      result.Add(new FilesystemSnapshotNode(
        nodeId,
        parent,
        name,
        entry.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile,
        entry.Size,
        allocated,
        Modified: ToOffset(entry.LastModified),
        OpenReadHandle: entry.IsDirectory ? null : () => new FatPositionalFileHandle(
          nodeId, _image, _ioGate, geometry, captured)));
    }
    return result;
  }

  private static int Depth(string path) => Normalize(path).Count(c => c == '/');
  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
  private static DateTimeOffset? ToOffset(DateTime? value) {
    if (value == null) return null;
    return value.Value.Kind == DateTimeKind.Local
      ? new DateTimeOffset(value.Value)
      : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), TimeSpan.Zero);
  }
}

internal sealed class FatPositionalFileHandle : IFilesystemFileHandle {
  private readonly Stream _image;
  private readonly object _ioGate;
  private readonly FatDriverGeometry _geometry;
  private readonly FatEntry _entry;
  private readonly IReadOnlyList<int> _chain;
  private bool _disposed;

  public FatPositionalFileHandle(
      FilesystemNodeId nodeId,
      Stream image,
      object ioGate,
      FatDriverGeometry geometry,
      FatEntry entry) {
    NodeId = nodeId;
    _image = image;
    _ioGate = ioGate;
    _geometry = geometry;
    _entry = entry;
    _chain = entry.StartCluster < 2 ? [] : geometry.ReadChain(image, entry.StartCluster, entry.Name);
  }

  public FilesystemNodeId NodeId { get; }
  public long Length {
    get {
      ThrowIfDisposed();
      return Math.Max(0, _entry.Size);
    }
  }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (destination.Length == 0 || offset >= Length) return 0;

    var remaining = checked((int)Math.Min(destination.Length, Length - offset));
    var written = 0;
    var logical = offset;
    while (remaining > 0) {
      var chainIndex = checked((int)(logical / _geometry.ClusterSize));
      if ((uint)chainIndex >= (uint)_chain.Count)
        throw new InvalidDataException($"FAT chain for '{_entry.Name}' ends before logical offset {logical}.");
      var within = checked((int)(logical % _geometry.ClusterSize));
      var take = Math.Min(remaining, _geometry.ClusterSize - within);
      var physical = checked(_geometry.ClusterOffset(_chain[chainIndex]) + within);
      lock (_ioGate) {
        if (physical < 0 || physical > _image.Length - take)
          throw new InvalidDataException($"FAT cluster for '{_entry.Name}' lies outside the image.");
        _image.Position = physical;
        _image.ReadExactly(destination.Slice(written, take));
      }
      logical += take;
      written += take;
      remaining -= take;
    }
    return written;
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("The FAT filesystem session is read-only.");
  public void SetLength(long length)
    => throw new NotSupportedException("The FAT filesystem session is read-only.");
  public void Flush() => ThrowIfDisposed();
  public void Dispose() => _disposed = true;
  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
