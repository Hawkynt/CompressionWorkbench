#pragma warning disable CS1591

namespace Compression.Registry;

/// <summary>
/// Native filesystem node projected into the common driver contract. Filesystem
/// implementations keep ownership of parsing and positional I/O; this record is
/// only the stable namespace/metadata hand-off to a frontend-neutral session.
/// </summary>
public sealed record FilesystemSnapshotNode(
  FilesystemNodeId NodeId,
  FilesystemNodeId ParentNodeId,
  string Name,
  FilesystemNodeKind Kind,
  long Size,
  long AllocatedSize,
  uint LinkCount = 1,
  ulong NativeAttributes = 0,
  DateTimeOffset? Created = null,
  DateTimeOffset? Modified = null,
  DateTimeOffset? Accessed = null,
  DateTimeOffset? Changed = null,
  string? SymbolicLinkTarget = null,
  Func<IFilesystemFileHandle>? OpenReadHandle = null
);

/// <summary>
/// Reusable frontend-neutral session for a filesystem parser that already has
/// native stable node ids and positional read handles. It intentionally contains
/// no archive emulation and no write fallback. A filesystem can use this as its
/// first native driver milestone, then replace it with a mutable session while
/// retaining the same public contract.
/// </summary>
public sealed class ReadOnlyFilesystemSnapshotSession : IFilesystemSession {
  private readonly Dictionary<FilesystemNodeId, FilesystemSnapshotNode> _nodes;
  private readonly Dictionary<FilesystemNodeId, FilesystemSnapshotNode[]> _children;
  private bool _disposed;

  public ReadOnlyFilesystemSnapshotSession(
      FilesystemDriverProfile profile,
      FilesystemNodeId rootNodeId,
      IEnumerable<FilesystemSnapshotNode> nodes) {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(nodes);
    if (!profile.CanMount)
      throw new ArgumentException("A snapshot session requires a mountable filesystem profile.", nameof(profile));
    if (profile.CanMountWritable)
      throw new ArgumentException("ReadOnlyFilesystemSnapshotSession cannot represent a writable profile.", nameof(profile));

    Profile = profile;
    RootNodeId = rootNodeId;
    _nodes = nodes.ToDictionary(node => node.NodeId);
    if (!_nodes.TryGetValue(rootNodeId, out var root) || root.Kind != FilesystemNodeKind.Directory)
      throw new InvalidDataException("Filesystem snapshot must contain its root directory node.");

    var children = new Dictionary<FilesystemNodeId, List<FilesystemSnapshotNode>>();
    foreach (var node in _nodes.Values) {
      children.TryAdd(node.NodeId, []);
      if (node.NodeId == rootNodeId) continue;
      if (!_nodes.TryGetValue(node.ParentNodeId, out var parent) || parent.Kind != FilesystemNodeKind.Directory)
        throw new InvalidDataException($"Filesystem node {node.NodeId.Value}:{node.NodeId.Generation} has no directory parent.");
      if (!children.TryGetValue(parent.NodeId, out var list)) children[parent.NodeId] = list = [];
      if (list.Any(existing => string.Equals(existing.Name, node.Name, StringComparison.Ordinal)))
        throw new InvalidDataException($"Filesystem directory contains duplicate name '{node.Name}'.");
      list.Add(node);
    }
    _children = children.ToDictionary(
      item => item.Key,
      item => item.Value.OrderBy(node => node.Name, StringComparer.Ordinal).ToArray());
  }

  public FilesystemDriverProfile Profile { get; }
  public FilesystemNodeId RootNodeId { get; }

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) {
    ThrowIfDisposed();
    var node = RequireNode(nodeId);
    return new FilesystemNodeInfo(
      node.NodeId,
      node.Kind,
      Math.Max(0, node.Size),
      Math.Max(0, node.AllocatedSize),
      node.LinkCount,
      node.NativeAttributes,
      node.Created,
      node.Modified,
      node.Accessed,
      node.Changed);
  }

  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) {
    ArgumentNullException.ThrowIfNull(name);
    ThrowIfDisposed();
    var parent = RequireNode(parentDirectory);
    if (parent.Kind != FilesystemNodeKind.Directory) throw new DirectoryNotFoundException(parent.Name);
    if (!_children.TryGetValue(parentDirectory, out var children)) return null;
    var exact = children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.Ordinal));
    if (exact != null) return exact.NodeId;
    var folded = children.Where(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
    return folded.Length == 1 ? folded[0].NodeId : null;
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    ThrowIfDisposed();
    var node = RequireNode(directory);
    if (node.Kind != FilesystemNodeKind.Directory) throw new DirectoryNotFoundException(node.Name);
    return (_children.TryGetValue(directory, out var children) ? children : [])
      .Select(child => new FilesystemDirectoryEntry(child.Name, child.NodeId, child.Kind))
      .ToArray();
  }

  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
    ThrowIfDisposed();
    if (access != FileAccess.Read) throw ReadOnly();
    var node = RequireNode(nodeId);
    if (node.Kind != FilesystemNodeKind.RegularFile)
      throw new UnauthorizedAccessException($"'{node.Name}' is not a regular file.");
    return node.OpenReadHandle?.Invoke()
      ?? throw new NotSupportedException($"Filesystem node '{node.Name}' has no native data handle.");
  }

  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public void DeleteFile(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) => throw ReadOnly();
  public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace) => throw ReadOnly();
  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName) => throw ReadOnly();
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target) => throw ReadOnly();

  public string ReadSymbolicLink(FilesystemNodeId nodeId) {
    ThrowIfDisposed();
    var node = RequireNode(nodeId);
    if (node.Kind != FilesystemNodeKind.SymbolicLink || node.SymbolicLinkTarget == null)
      throw new InvalidOperationException($"'{node.Name}' is not a decoded symbolic link.");
    return node.SymbolicLinkTarget;
  }

  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) => throw ReadOnly();
  public void Flush() => ThrowIfDisposed();
  public IFilesystemTransaction BeginTransaction()
    => throw new NotSupportedException("The native snapshot session is read-only and has no write transaction.");
  public void Dispose() => _disposed = true;

  private FilesystemSnapshotNode RequireNode(FilesystemNodeId nodeId)
    => _nodes.TryGetValue(nodeId, out var node)
      ? node
      : throw new FileNotFoundException($"Filesystem node {nodeId.Value}:{nodeId.Generation} does not exist in this session.");

  private static NotSupportedException ReadOnly()
    => new("This native filesystem snapshot session is read-only.");

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
