#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.ExFat;

/// <summary>
/// Native read-only exFAT namespace over the format reader. Object ids are
/// session-stable and path-independent; file handles issue positional reads
/// directly against cluster allocations and synthesize zeros above
/// ValidDataLength as required by exFAT.
/// </summary>
internal sealed class ExFatFilesystemSession : IFilesystemSession {
  private sealed record NodeState(
    FilesystemNodeId NodeId,
    FilesystemNodeId ParentId,
    string Name,
    FilesystemNodeKind Kind,
    ExFatEntry? Entry);

  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly ExFatReader _reader;
  private readonly Dictionary<FilesystemNodeId, NodeState> _nodes = [];
  private readonly Dictionary<FilesystemNodeId, Dictionary<string, NodeState>> _children = [];
  private bool _disposed;

  public ExFatFilesystemSession(
      Stream image,
      FilesystemDriverProfile profile,
      uint volumeSerial,
      bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("Native exFAT mounting requires a readable, seekable stream.", nameof(image));
    _image = image;
    _leaveOpen = leaveOpen;
    Profile = profile;
    RootNodeId = new FilesystemNodeId(1, volumeSerial);
    _reader = new ExFatReader(image, leaveOpen: true);
    BuildNamespace(volumeSerial);
  }

  public FilesystemDriverProfile Profile { get; }
  public FilesystemNodeId RootNodeId { get; }

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) {
    ThrowIfDisposed();
    var node = RequireNode(nodeId);
    if (node.Entry is not { } entry)
      return new FilesystemNodeInfo(nodeId, FilesystemNodeKind.Directory, 0, 0, 1);
    var modified = entry.LastModified is { } timestamp
      ? new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc))
      : null;
    return new FilesystemNodeInfo(
      node.NodeId,
      node.Kind,
      entry.IsDirectory ? 0 : entry.Size,
      entry.IsDirectory ? 0 : _reader.GetAllocatedSize(entry),
      1,
      Modified: modified);
  }

  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) {
    ArgumentNullException.ThrowIfNull(name);
    ThrowIfDisposed();
    RequireDirectory(parentDirectory);
    return _children.TryGetValue(parentDirectory, out var children) &&
           children.TryGetValue(name, out var child)
      ? child.NodeId
      : null;
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    ThrowIfDisposed();
    RequireDirectory(directory);
    IEnumerable<NodeState> values = _children.TryGetValue(directory, out var children)
      ? children.Values
      : Array.Empty<NodeState>();
    return values
      .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
      .Select(node => new FilesystemDirectoryEntry(node.Name, node.NodeId, node.Kind))
      .ToArray();
  }

  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
    ThrowIfDisposed();
    if (access != FileAccess.Read)
      throw new NotSupportedException("The native exFAT mounted session is read-only.");
    var node = RequireNode(nodeId);
    if (node.Kind != FilesystemNodeKind.RegularFile || node.Entry is null)
      throw new UnauthorizedAccessException($"'{node.Name}' is not a regular file.");
    return new FileHandle(_reader, node.NodeId, node.Entry);
  }

  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public void DeleteFile(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace) => throw ReadOnly();
  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName) => throw new NotSupportedException("exFAT has no native hard-link object model.");
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target) => throw new NotSupportedException("exFAT has no native symbolic-link object type.");
  public string ReadSymbolicLink(FilesystemNodeId nodeId) => throw new NotSupportedException("exFAT has no native symbolic-link object type.");
  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) => throw ReadOnly();

  public void Flush() {
    ThrowIfDisposed();
  }

  public IFilesystemTransaction BeginTransaction()
    => throw new NotSupportedException("The native exFAT read-only session has no write transaction.");

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    _reader.Dispose();
    if (!_leaveOpen) _image.Dispose();
  }

  private void BuildNamespace(uint volumeSerial) {
    var root = new NodeState(RootNodeId, RootNodeId, string.Empty, FilesystemNodeKind.Directory, null);
    _nodes.Add(RootNodeId, root);
    _children.Add(RootNodeId, new Dictionary<string, NodeState>(StringComparer.OrdinalIgnoreCase));

    var directoriesByPath = new Dictionary<string, FilesystemNodeId>(StringComparer.OrdinalIgnoreCase) {
      [string.Empty] = RootNodeId,
    };

    var ordinal = 2u;
    foreach (var entry in _reader.Entries) {
      var normalized = entry.Name.Replace('\\', '/').Trim('/');
      if (normalized.Length == 0)
        throw new InvalidDataException("exFAT reader returned an empty live entry name.");
      var slash = normalized.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : normalized[..slash];
      var name = slash < 0 ? normalized : normalized[(slash + 1)..];
      if (!directoriesByPath.TryGetValue(parentPath, out var parentId))
        throw new InvalidDataException($"exFAT entry '{normalized}' has no decoded parent directory '{parentPath}'.");

      var value = ((ulong)entry.FirstCluster << 32) | ordinal++;
      var nodeId = new FilesystemNodeId(value, volumeSerial);
      var kind = entry.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile;
      var state = new NodeState(nodeId, parentId, name, kind, entry);
      if (!_nodes.TryAdd(nodeId, state))
        throw new InvalidDataException($"exFAT node-id collision for '{normalized}'.");
      var children = _children[parentId];
      if (!children.TryAdd(name, state))
        throw new InvalidDataException($"exFAT directory '{parentPath}' contains case-colliding name '{name}'.");
      if (entry.IsDirectory) {
        _children[nodeId] = new Dictionary<string, NodeState>(StringComparer.OrdinalIgnoreCase);
        if (!directoriesByPath.TryAdd(normalized, nodeId))
          throw new InvalidDataException($"exFAT directory path '{normalized}' appears more than once.");
      }
    }
  }

  private NodeState RequireNode(FilesystemNodeId nodeId)
    => _nodes.TryGetValue(nodeId, out var node)
      ? node
      : throw new FileNotFoundException($"exFAT node {nodeId.Value}:{nodeId.Generation} does not exist in this session.");

  private NodeState RequireDirectory(FilesystemNodeId nodeId) {
    var node = RequireNode(nodeId);
    if (node.Kind != FilesystemNodeKind.Directory)
      throw new DirectoryNotFoundException(node.Name);
    return node;
  }

  private static NotSupportedException ReadOnly()
    => new("The native exFAT mounted session is read-only.");

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

  private sealed class FileHandle : IFilesystemFileHandle {
    private readonly ExFatReader _reader;
    private readonly ExFatEntry _entry;
    private bool _disposed;

    public FileHandle(ExFatReader reader, FilesystemNodeId nodeId, ExFatEntry entry) {
      _reader = reader;
      _entry = entry;
      NodeId = nodeId;
    }

    public FilesystemNodeId NodeId { get; }
    public long Length {
      get {
        ThrowIfDisposed();
        return _entry.Size;
      }
    }

    public int Read(long offset, Span<byte> destination) {
      ThrowIfDisposed();
      return _reader.ReadAt(_entry, offset, destination);
    }

    public void Write(long offset, ReadOnlySpan<byte> source)
      => throw new NotSupportedException("The native exFAT mounted session is read-only.");

    public void SetLength(long length)
      => throw new NotSupportedException("The native exFAT mounted session is read-only.");

    public void Flush() => ThrowIfDisposed();
    public void Dispose() => _disposed = true;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
  }
}
