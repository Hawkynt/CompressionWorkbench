#pragma warning disable CS1591

namespace Compression.Registry;

/// <summary>
/// Native filesystem object projected into the common driver contract. Name and
/// parent are a convenient primary-link description for simple filesystems; use
/// explicit <see cref="FilesystemSnapshotDirectoryEntry"/> values when one node
/// has multiple directory entries (hard links).
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

public sealed record FilesystemSnapshotDirectoryEntry(
  FilesystemNodeId ParentNodeId,
  string Name,
  FilesystemNodeId NodeId
);

public sealed class ReadOnlyFilesystemSnapshotSession : IFilesystemSession {
  private sealed record Child(string Name, FilesystemSnapshotNode Node);
  private sealed record SnapshotInput(
    FilesystemSnapshotNode[] Nodes,
    FilesystemSnapshotDirectoryEntry[] Entries);

  private readonly Dictionary<FilesystemNodeId, FilesystemSnapshotNode> _nodes;
  private readonly Dictionary<FilesystemNodeId, Child[]> _children;
  private bool _disposed;

  public ReadOnlyFilesystemSnapshotSession(
      FilesystemDriverProfile profile,
      FilesystemNodeId rootNodeId,
      IEnumerable<FilesystemSnapshotNode> nodes)
    : this(profile, rootNodeId, Prepare(nodes, rootNodeId)) { }

  private ReadOnlyFilesystemSnapshotSession(
      FilesystemDriverProfile profile,
      FilesystemNodeId rootNodeId,
      SnapshotInput input)
    : this(profile, rootNodeId, input.Nodes, input.Entries) { }

  /// <summary>
  /// Full constructor with independent object and directory-entry sets. Multiple
  /// entries may target the same node ID; that is how hard links are represented.
  /// </summary>
  public ReadOnlyFilesystemSnapshotSession(
      FilesystemDriverProfile profile,
      FilesystemNodeId rootNodeId,
      IEnumerable<FilesystemSnapshotNode> nodes,
      IEnumerable<FilesystemSnapshotDirectoryEntry> directoryEntries) {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(nodes);
    ArgumentNullException.ThrowIfNull(directoryEntries);
    if (!profile.CanMount)
      throw new ArgumentException("A snapshot session requires a mountable filesystem profile.", nameof(profile));
    if (profile.CanMountWritable)
      throw new ArgumentException("ReadOnlyFilesystemSnapshotSession cannot represent a writable profile.", nameof(profile));

    Profile = profile;
    RootNodeId = rootNodeId;
    _nodes = new Dictionary<FilesystemNodeId, FilesystemSnapshotNode>();
    foreach (var node in nodes) {
      if (!_nodes.TryAdd(node.NodeId, node))
        throw new InvalidDataException(
          $"Filesystem snapshot defines node {node.NodeId.Value}:{node.NodeId.Generation} more than once.");
    }
    if (!_nodes.TryGetValue(rootNodeId, out var root) || root.Kind != FilesystemNodeKind.Directory)
      throw new InvalidDataException("Filesystem snapshot must contain its root directory node.");

    var children = _nodes.Keys.ToDictionary(id => id, _ => new List<Child>());
    foreach (var entry in directoryEntries) {
      ArgumentNullException.ThrowIfNull(entry.Name);
      if (entry.Name.Length == 0 || entry.Name is "." or ".." || entry.Name.Contains('/') || entry.Name.Contains('\\'))
        throw new InvalidDataException($"Filesystem directory entry name '{entry.Name}' is not a single valid path component.");
      if (!_nodes.TryGetValue(entry.ParentNodeId, out var parent) || parent.Kind != FilesystemNodeKind.Directory)
        throw new InvalidDataException(
          $"Filesystem directory entry '{entry.Name}' has no directory parent {entry.ParentNodeId.Value}:{entry.ParentNodeId.Generation}.");
      if (!_nodes.TryGetValue(entry.NodeId, out var target))
        throw new InvalidDataException(
          $"Filesystem directory entry '{entry.Name}' targets missing node {entry.NodeId.Value}:{entry.NodeId.Generation}.");
      var list = children[entry.ParentNodeId];
      if (list.Any(existing => string.Equals(existing.Name, entry.Name, StringComparison.Ordinal)))
        throw new InvalidDataException($"Filesystem directory contains duplicate name '{entry.Name}'.");
      list.Add(new Child(entry.Name, target));
    }

    _children = children.ToDictionary(
      item => item.Key,
      item => item.Value.OrderBy(child => child.Name, StringComparer.Ordinal).ToArray());
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
    if (exact != null) return exact.Node.NodeId;
    if ((Profile.Capabilities & FilesystemDriverCapabilities.CaseSensitiveNames) != 0)
      return null;
    var folded = children.Where(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
    return folded.Length == 1 ? folded[0].Node.NodeId : null;
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    ThrowIfDisposed();
    var node = RequireNode(directory);
    if (node.Kind != FilesystemNodeKind.Directory) throw new DirectoryNotFoundException(node.Name);
    return (_children.TryGetValue(directory, out var children) ? children : [])
      .Select(child => new FilesystemDirectoryEntry(child.Name, child.Node.NodeId, child.Node.Kind))
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

  private static SnapshotInput Prepare(
      IEnumerable<FilesystemSnapshotNode> nodes,
      FilesystemNodeId rootNodeId) {
    ArgumentNullException.ThrowIfNull(nodes);
    var materialized = nodes.ToArray();
    var entries = materialized
      .Where(node => node.NodeId != rootNodeId)
      .Select(node => new FilesystemSnapshotDirectoryEntry(node.ParentNodeId, node.Name, node.NodeId))
      .ToArray();
    return new SnapshotInput(materialized, entries);
  }

  private static NotSupportedException ReadOnly()
    => new("This native filesystem snapshot session is read-only.");

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
