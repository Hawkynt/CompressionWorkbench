#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Ext;

internal sealed class ExtReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly ExtReader _reader;
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public ExtReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ext mounted reads require a readable, seekable image.", nameof(image));
    _image = image;
    _leaveOpen = leaveOpen;
    var super = ExtDriverSuperblock.Parse(image);
    _reader = new ExtReader(image, leaveOpen: true);
    var records = _reader.Entries.ToArray();

    var rootInode = super.ReadInode(image, 2);
    var rootId = new FilesystemNodeId(2, rootInode.Generation);
    var (nodes, links) = BuildNamespace(super, records, rootId, rootInode);
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
    _reader.Dispose();
    if (!_leaveOpen) _image.Dispose();
  }

  private (FilesystemSnapshotNode[] Nodes, FilesystemSnapshotDirectoryEntry[] Links) BuildNamespace(
      ExtDriverSuperblock super,
      IReadOnlyList<ExtEntry> records,
      FilesystemNodeId rootId,
      ExtDriverInode rootInode) {
    var nodesByInode = new Dictionary<uint, FilesystemSnapshotNode>();
    var pathToNode = new Dictionary<string, FilesystemNodeId>(StringComparer.Ordinal) {
      [string.Empty] = rootId,
    };
    var links = new List<FilesystemSnapshotDirectoryEntry>(records.Count);

    nodesByInode[2] = MakeNode(rootId, rootInode, string.Empty, default, null);

    foreach (var record in records.OrderBy(r => Depth(r.Name)).ThenBy(r => r.Name, StringComparer.Ordinal)) {
      var path = Normalize(record.Name);
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var name = slash < 0 ? path : path[(slash + 1)..];
      if (!pathToNode.TryGetValue(parentPath, out var parentId))
        throw new InvalidDataException($"ext entry '{path}' has no decoded parent '{parentPath}'.");

      var inode = super.ReadInode(_image, record.Inode);
      var nodeId = new FilesystemNodeId(record.Inode, inode.Generation);
      if (!nodesByInode.TryGetValue(record.Inode, out var existing)) {
        var captured = record;
        nodesByInode[record.Inode] = MakeNode(nodeId, inode, name, parentId, captured);
      } else if (existing.NodeId != nodeId || existing.Kind != inode.Kind || existing.Size != inode.Size) {
        throw new InvalidDataException($"ext hard-link aliases for inode {record.Inode} disagree on object metadata.");
      }

      links.Add(new FilesystemSnapshotDirectoryEntry(parentId, name, nodeId));
      if (inode.Kind == FilesystemNodeKind.Directory)
        pathToNode[path] = nodeId;
    }

    return (nodesByInode.Values.ToArray(), links.ToArray());
  }

  private FilesystemSnapshotNode MakeNode(
      FilesystemNodeId nodeId,
      ExtDriverInode inode,
      string name,
      FilesystemNodeId parent,
      ExtEntry? record) {
    Func<IFilesystemFileHandle>? open = null;
    if (inode.Kind == FilesystemNodeKind.RegularFile && record != null) {
      var captured = record;
      open = () => SpoolingReadOnlyFileHandle.Create(
        nodeId,
        inode.Size,
        output => {
          lock (_ioGate) _reader.ExtractTo(captured, output);
        });
    }

    return new FilesystemSnapshotNode(
      nodeId,
      parent,
      name,
      inode.Kind,
      inode.Kind == FilesystemNodeKind.Directory ? 0 : inode.Size,
      inode.AllocatedBytes,
      LinkCount: inode.LinkCount,
      NativeAttributes: ((ulong)inode.Flags << 32) | inode.Mode,
      Modified: inode.Modified,
      SymbolicLinkTarget: record?.LinkTarget,
      OpenReadHandle: open);
  }

  private static int Depth(string path) => Normalize(path).Count(c => c == '/');
  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
