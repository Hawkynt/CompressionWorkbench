#pragma warning disable CS1591
using System.Runtime.ExceptionServices;

namespace Compression.Registry;

/// <summary>
/// One namespace object consumed or produced by a whole-image filesystem
/// rebuilder. Paths always use '/' separators and are relative to the root.
/// Directory entries carry empty data.
/// </summary>
public sealed record RebuildFilesystemEntry(
  string Path,
  FilesystemNodeKind Kind,
  ReadOnlyMemory<byte> Data
) {
  public static RebuildFilesystemEntry File(string path, ReadOnlyMemory<byte> data)
    => new(path, FilesystemNodeKind.RegularFile, data);

  public static RebuildFilesystemEntry Directory(string path)
    => new(path, FilesystemNodeKind.Directory, ReadOnlyMemory<byte>.Empty);
}

/// <summary>
/// One file or directory in a streaming whole-image rebuild. Each file provides
/// an independently owned read stream, so its length is not constrained by
/// managed byte-array indexing or the aggregate size of the namespace.
/// </summary>
internal sealed record SpoolingRebuildFilesystemEntry(
  string Path,
  FilesystemNodeKind Kind,
  long Length,
  Func<Stream>? OpenRead
) {
  public static SpoolingRebuildFilesystemEntry File(string path, long length, Func<Stream> openRead)
    => new(path, FilesystemNodeKind.RegularFile, length, openRead);

  public static SpoolingRebuildFilesystemEntry Directory(string path)
    => new(path, FilesystemNodeKind.Directory, 0, null);
}

/// <summary>
/// Performs a verified whole-image rebuild and publishes it to a mutable image
/// stream only after the candidate image has been built successfully. The old
/// image is retained in a temporary stream and restored if publication fails.
///
/// This is an in-process transactional replacement for arbitrary seekable
/// streams. It deliberately does not claim crash-atomic host-filesystem rename
/// semantics: a process or machine crash during publication can still leave a
/// path-backed stream partially written unless its owner provides a stronger
/// outer transaction.
/// </summary>
public static class WholeImageRebuildCommitter {
  public static void Replace(
      Stream image,
      Action<Stream> buildCandidate,
      Action<Stream>? validateCandidate = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(buildCandidate);
    RequireReplaceable(image);

    var originalPosition = image.Position;
    using var candidate = CreateTemporaryStream();
    buildCandidate(candidate);
    candidate.Flush();
    candidate.Position = 0;
    validateCandidate?.Invoke(candidate);
    candidate.Position = 0;

    using var backup = CreateTemporaryStream();
    image.Position = 0;
    image.CopyTo(backup);
    backup.Flush();
    backup.Position = 0;

    Exception? publicationFailure = null;
    try {
      image.Position = 0;
      candidate.CopyTo(image);
      image.SetLength(candidate.Length);
      FlushDurably(image);
    } catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or ObjectDisposedException) {
      publicationFailure = e;
    }

    if (publicationFailure is not null) {
      try {
        image.Position = 0;
        backup.CopyTo(image);
        image.SetLength(backup.Length);
        FlushDurably(image);
      } catch (Exception rollbackFailure) when (rollbackFailure is IOException or NotSupportedException or UnauthorizedAccessException or ObjectDisposedException) {
        throw new AggregateException(
          "Whole-image publication failed and restoring the original image also failed.",
          publicationFailure,
          rollbackFailure);
      } finally {
        RestorePosition(image, originalPosition);
      }

      ExceptionDispatchInfo.Capture(publicationFailure).Throw();
    }

    RestorePosition(image, originalPosition);
  }

  public static void FlushDurably(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image is FileStream file)
      file.Flush(flushToDisk: true);
    else
      image.Flush();
  }

  private static void RequireReplaceable(Stream image) {
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException(
        "Transactional whole-image replacement requires a readable, writable, seekable stream.",
        nameof(image));
  }

  private static FileStream CreateTemporaryStream() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb-rebuild-{Guid.NewGuid():N}.tmp");
    return new FileStream(
      path,
      FileMode.CreateNew,
      FileAccess.ReadWrite,
      FileShare.None,
      128 * 1024,
      FileOptions.DeleteOnClose | FileOptions.SequentialScan);
  }

  private static void RestorePosition(Stream image, long originalPosition) {
    if (!image.CanSeek)
      return;
    image.Position = Math.Min(Math.Max(0, originalPosition), image.Length);
  }
}

/// <summary>
/// Reusable mount-grade mutable namespace for formats whose safe mutation model
/// is to regenerate the complete filesystem image. File/node identity is stable
/// for the lifetime of the session, including across rename and unlink, while
/// flush serializes the linked namespace to a verified candidate image and then
/// publishes it through <see cref="WholeImageRebuildCommitter"/>.
/// </summary>
public sealed class MutableRebuildFilesystemSession : IFilesystemSession {
  public delegate void RebuildImage(
    Stream output,
    IReadOnlyList<RebuildFilesystemEntry> entries);

  public delegate void ValidateImage(
    Stream candidate,
    IReadOnlyList<RebuildFilesystemEntry> entries);

  internal delegate void RebuildImageFromStreams(
    Stream output,
    IReadOnlyList<SpoolingRebuildFilesystemEntry> entries);

  internal delegate void ValidateImageFromStreams(
    Stream candidate,
    IReadOnlyList<SpoolingRebuildFilesystemEntry> entries);

  /// <summary>
  /// Optional explicitly qualified direct mutation publisher. The delegate
  /// MUST journal, validate and restore the entire source on failure.
  /// </summary>
  internal delegate void CommitImageInPlace(
    Stream image,
    IReadOnlyList<SpoolingRebuildFilesystemEntry> entries);

  private sealed class Node {
    public required FilesystemNodeId Id;
    public required FilesystemNodeKind Kind;
    public required FilesystemNodeId Parent;
    public required string Name;
    public required byte[] Data;
    public FileStream? Spool;
    public bool Linked = true;

    public long Length => Spool?.Length ?? Data.LongLength;
  }

  private readonly object _gate = new();
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly RebuildImage? _rebuild;
  private readonly ValidateImage? _validate;
  private RebuildImageFromStreams? _streamRebuild;
  private ValidateImageFromStreams? _streamValidate;
  private CommitImageInPlace? _streamInPlace;
  private readonly Dictionary<FilesystemNodeId, Node> _nodes = [];
  private readonly Dictionary<FilesystemNodeId, Dictionary<string, Node>> _children = [];
  private readonly StringComparer _nameComparer;
  private ulong _nextNodeValue;
  private bool _dirty;
  private bool _disposed;
  private readonly long _maxTotalContentBytes;
  private long _allocatedContentBytes;

  public MutableRebuildFilesystemSession(
      FilesystemDriverProfile profile,
      Stream image,
      IEnumerable<RebuildFilesystemEntry> entries,
      RebuildImage? rebuild,
      ValidateImage? validate = null,
      bool leaveOpen = true,
      long maxTotalContentBytes = long.MaxValue) {
    ArgumentOutOfRangeException.ThrowIfNegative(maxTotalContentBytes);
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entries);
    if (!profile.CanMount)
      throw new ArgumentException("A mutable rebuild session requires a mountable profile.", nameof(profile));
    if (profile.CanMountWritable) {
      if (profile.MutationModel != FilesystemMutationModel.WholeImageRebuild)
        throw new ArgumentException("A writable rebuild session requires the WholeImageRebuild mutation model.", nameof(profile));
      if (!image.CanRead || !image.CanWrite || !image.CanSeek)
        throw new ArgumentException("Writable rebuild sessions require a readable, writable, seekable image.", nameof(image));
      ArgumentNullException.ThrowIfNull(rebuild);
    }

    Profile = profile;
    _image = image;
    _leaveOpen = leaveOpen;
    _rebuild = rebuild;
    _validate = validate;
    _nameComparer = (profile.Capabilities & FilesystemDriverCapabilities.CaseSensitiveNames) != 0
      ? StringComparer.Ordinal
      : StringComparer.OrdinalIgnoreCase;
    _maxTotalContentBytes = maxTotalContentBytes;

    RootNodeId = new(1, 1);
    _nextNodeValue = RootNodeId.Value;
    var root = new Node {
      Id = RootNodeId,
      Kind = FilesystemNodeKind.Directory,
      Parent = RootNodeId,
      Name = string.Empty,
      Data = [],
    };
    _nodes.Add(root.Id, root);
    _children.Add(root.Id, NewChildMap());

    BuildNamespace(entries);
    foreach (var node in _nodes.Values.Where(static node => node.Kind == FilesystemNodeKind.RegularFile)) {
      if (node.Data.LongLength > _maxTotalContentBytes - _allocatedContentBytes)
        throw new NotSupportedException($"Mutable namespace exceeds the {_maxTotalContentBytes:N0}-byte session content budget.");
      _allocatedContentBytes += node.Data.LongLength;
    }
  }

  /// <summary>
  /// Opens the same stable-inode mutable namespace with all regular-file
  /// payloads on disk. Streaming serializers consume independently opened
  /// streams; even a multi-gigabyte member never becomes one managed array.
  /// </summary>
  internal static MutableRebuildFilesystemSession OpenSpooling(
      FilesystemDriverProfile profile,
      Stream image,
      IEnumerable<SpoolingRebuildFilesystemEntry> entries,
      RebuildImageFromStreams rebuild,
      ValidateImageFromStreams? validate = null,
      bool leaveOpen = true,
      CommitImageInPlace? journalledInPlaceCommit = null) {
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(rebuild);

    var originals = entries.ToArray();
    var filesystem = new MutableRebuildFilesystemSession(
      profile,
      image,
      originals.Select(static entry => new RebuildFilesystemEntry(
        entry.Path, entry.Kind, ReadOnlyMemory<byte>.Empty)),
      static (_, _) => throw new NotSupportedException("The session uses streaming rebuild delegates."),
      leaveOpen: leaveOpen);

    try {
      lock (filesystem._gate) {
        foreach (var original in originals.Where(static entry => entry.Kind == FilesystemNodeKind.RegularFile)) {
          ArgumentOutOfRangeException.ThrowIfNegative(original.Length);
          var path = NormalizePath(original.Path);
          var node = filesystem._nodes.Values.Single(node =>
            node.Kind == FilesystemNodeKind.RegularFile && filesystem.BuildPath(node) == path);
          using var reader = (original.OpenRead ?? throw new InvalidDataException(
            $"Stream source for '{path}' was not supplied."))();
          var spool = CreateSpool();
          try {
            var bytesLeft = original.Length;
            var buffer = new byte[64 * 1024];
            while (bytesLeft > 0) {
              var read = reader.Read(buffer.AsSpan(0, checked((int)Math.Min(bytesLeft, buffer.Length))));
              if (read <= 0)
                throw new InvalidDataException($"Entry '{path}' ends before its declared {original.Length} bytes.");
              spool.Write(buffer.AsSpan(0, read));
              bytesLeft -= read;
            }
            if (reader.ReadByte() >= 0)
              throw new InvalidDataException($"Entry '{path}' exceeds its declared {original.Length} bytes.");
            spool.Flush();
            node.Spool = spool;
            filesystem._allocatedContentBytes = checked(filesystem._allocatedContentBytes + original.Length);
          } catch {
            spool.Dispose();
            throw;
          }
        }
        filesystem._streamRebuild = rebuild;
        filesystem._streamValidate = validate;
        filesystem._streamInPlace = journalledInPlaceCommit;
      }
      return filesystem;
    } catch {
      filesystem._dirty = false;
      filesystem.Dispose();
      throw;
    }
  }

  public FilesystemDriverProfile Profile { get; }
  public FilesystemNodeId RootNodeId { get; }

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) {
    lock (_gate) {
      ThrowIfDisposed();
      var node = RequireNode(nodeId);
      var size = node.Kind == FilesystemNodeKind.RegularFile ? node.Length : 0;
      return new(node.Id, node.Kind, size, size, LinkCount: node.Linked ? 1U : 0U);
    }
  }

  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) {
    ArgumentNullException.ThrowIfNull(name);
    lock (_gate) {
      ThrowIfDisposed();
      ValidateComponent(name);
      var parent = RequireDirectory(parentDirectory);
      return _children[parent.Id].TryGetValue(name, out var child) && child.Linked
        ? child.Id
        : null;
    }
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    lock (_gate) {
      ThrowIfDisposed();
      var parent = RequireDirectory(directory);
      return _children[parent.Id].Values
        .Where(static child => child.Linked)
        .OrderBy(static child => child.Name, StringComparer.Ordinal)
        .Select(static child => new FilesystemDirectoryEntry(child.Name, child.Id, child.Kind))
        .ToArray();
    }
  }

  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
    lock (_gate) {
      ThrowIfDisposed();
      var node = RequireNode(nodeId);
      if (node.Kind != FilesystemNodeKind.RegularFile)
        throw new UnauthorizedAccessException($"'{node.Name}' is not a regular file.");
      if ((access & FileAccess.Read) != 0)
        RequireCapability(FilesystemDriverCapabilities.ReadData, "read file data");
      if ((access & FileAccess.Write) != 0)
        RequireCapability(FilesystemDriverCapabilities.WriteData, "write file data");
      return new FileHandle(this, node, access);
    }
  }

  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      ThrowIfDisposed();
      RequireCapability(FilesystemDriverCapabilities.CreateFile, "create files");
      ValidateComponent(name);
      var parent = RequireDirectory(parentDirectory);
      var children = _children[parent.Id];
      if (children.ContainsKey(name))
        throw new IOException($"A filesystem entry named '{name}' already exists.");

      var node = NewNode(parent.Id, name, FilesystemNodeKind.RegularFile, []);
      children.Add(name, node);
      MarkDirty();
      return node.Id;
    }
  }

  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      ThrowIfDisposed();
      RequireCapability(FilesystemDriverCapabilities.CreateDirectory, "create directories");
      ValidateComponent(name);
      var parent = RequireDirectory(parentDirectory);
      var children = _children[parent.Id];
      if (children.ContainsKey(name))
        throw new IOException($"A filesystem entry named '{name}' already exists.");

      var node = NewNode(parent.Id, name, FilesystemNodeKind.Directory, []);
      children.Add(name, node);
      _children.Add(node.Id, NewChildMap());
      MarkDirty();
      return node.Id;
    }
  }

  public void DeleteFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      ThrowIfDisposed();
      RequireCapability(FilesystemDriverCapabilities.DeleteFile, "delete files");
      var parent = RequireDirectory(parentDirectory);
      var node = RequireChild(parent, name);
      if (node.Kind != FilesystemNodeKind.RegularFile)
        throw new UnauthorizedAccessException($"'{name}' is not a regular file.");
      Unlink(parent, node);
    }
  }

  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      ThrowIfDisposed();
      RequireCapability(FilesystemDriverCapabilities.RemoveDirectory, "remove directories");
      var parent = RequireDirectory(parentDirectory);
      var node = RequireChild(parent, name);
      if (node.Kind != FilesystemNodeKind.Directory)
        throw new DirectoryNotFoundException(name);
      if (_children[node.Id].Values.Any(static child => child.Linked))
        throw new IOException($"Directory '{name}' is not empty.");
      Unlink(parent, node);
    }
  }

  public void Rename(
      FilesystemNodeId oldParent,
      string oldName,
      FilesystemNodeId newParent,
      string newName,
      bool replace) {
    lock (_gate) {
      ThrowIfDisposed();
      RequireCapability(FilesystemDriverCapabilities.Rename, "rename filesystem entries");
      ValidateComponent(oldName);
      ValidateComponent(newName);
      var sourceParent = RequireDirectory(oldParent);
      var destinationParent = RequireDirectory(newParent);
      var source = RequireChild(sourceParent, oldName);
      if (source.Id == RootNodeId)
        throw new IOException("The filesystem root cannot be renamed.");
      if (source.Kind == FilesystemNodeKind.Directory && IsDescendant(destinationParent, source))
        throw new IOException("A directory cannot be moved below itself.");

      var destinationChildren = _children[destinationParent.Id];
      if (destinationChildren.TryGetValue(newName, out var destination)) {
        if (destination.Id == source.Id)
          return;
        if (!replace)
          throw new IOException($"A filesystem entry named '{newName}' already exists.");
        if (destination.Kind != source.Kind)
          throw new IOException("Rename replacement requires matching filesystem object kinds.");
        if (destination.Kind == FilesystemNodeKind.Directory &&
            _children[destination.Id].Values.Any(static child => child.Linked))
          throw new IOException($"Destination directory '{newName}' is not empty.");
        Unlink(destinationParent, destination, markDirty: false);
      }

      _children[sourceParent.Id].Remove(source.Name);
      source.Parent = destinationParent.Id;
      source.Name = newName;
      destinationChildren[newName] = source;
      MarkDirty();
    }
  }

  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName)
    => throw new NotSupportedException("The rebuild session does not emulate hard links.");

  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target)
    => throw new NotSupportedException("The rebuild session does not emulate symbolic links.");

  public string ReadSymbolicLink(FilesystemNodeId nodeId)
    => throw new NotSupportedException("The rebuild session does not expose symbolic links.");

  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch)
    => throw new NotSupportedException("The rebuild session does not emulate filesystem metadata fields.");

  public void Flush() {
    lock (_gate) {
      ThrowIfDisposed();
      if (!_dirty) {
        WholeImageRebuildCommitter.FlushDurably(_image);
        return;
      }
      RequireCapability(FilesystemDriverCapabilities.Flush, "flush filesystem changes");
      if (_rebuild is null)
        throw new NotSupportedException("This filesystem session has no complete-image rebuilder.");

      if (_streamRebuild is { } streaming) {
        var staged = CaptureLinkedStreamingEntries();
        if (_streamInPlace is { } journalled)
          journalled(_image, staged);
        else
          WholeImageRebuildCommitter.Replace(_image,
            candidate => streaming(candidate, staged),
            _streamValidate is null ? null : candidate => _streamValidate(candidate, staged));
      } else {
        var snapshot = CaptureLinkedEntries();
        WholeImageRebuildCommitter.Replace(
          _image,
          candidate => _rebuild(candidate, snapshot),
          _validate is null ? null : candidate => _validate(candidate, snapshot));
      }
      _dirty = false;
    }
  }

  public IFilesystemTransaction BeginTransaction()
    => throw new NotSupportedException("Whole-image rebuild sessions currently expose flush as their transaction boundary.");

  public void Dispose() {
    lock (_gate) {
      if (_disposed)
        return;
      try {
        if (_dirty)
          Flush();
      } finally {
        _disposed = true;
        foreach (var node in _nodes.Values)
          node.Spool?.Dispose();
        if (!_leaveOpen)
          _image.Dispose();
      }
    }
  }

  private void BuildNamespace(IEnumerable<RebuildFilesystemEntry> entries) {
    foreach (var entry in entries.OrderBy(static entry => PathDepth(entry.Path)).ThenBy(static entry => entry.Path, StringComparer.Ordinal)) {
      if (entry.Kind is not (FilesystemNodeKind.RegularFile or FilesystemNodeKind.Directory))
        throw new NotSupportedException($"Mutable rebuild sessions do not support node kind '{entry.Kind}'.");
      var normalized = NormalizePath(entry.Path);
      var components = normalized.Split('/');
      var parent = _nodes[RootNodeId];
      for (var i = 0; i < components.Length - 1; ++i) {
        var name = components[i];
        var children = _children[parent.Id];
        if (children.TryGetValue(name, out var existing)) {
          if (existing.Kind != FilesystemNodeKind.Directory)
            throw new InvalidDataException($"Filesystem path '{normalized}' crosses regular file '{name}'.");
          parent = existing;
          continue;
        }

        var directory = NewNode(parent.Id, name, FilesystemNodeKind.Directory, []);
        children.Add(name, directory);
        _children.Add(directory.Id, NewChildMap());
        parent = directory;
      }

      var leaf = components[^1];
      var parentChildren = _children[parent.Id];
      if (parentChildren.TryGetValue(leaf, out var present)) {
        if (entry.Kind == FilesystemNodeKind.Directory && present.Kind == FilesystemNodeKind.Directory)
          continue;
        throw new InvalidDataException($"Filesystem snapshot contains duplicate path '{normalized}'.");
      }

      var node = NewNode(
        parent.Id,
        leaf,
        entry.Kind,
        entry.Kind == FilesystemNodeKind.RegularFile ? entry.Data.ToArray() : []);
      parentChildren.Add(leaf, node);
      if (entry.Kind == FilesystemNodeKind.Directory)
        _children.Add(node.Id, NewChildMap());
    }
  }

  private IReadOnlyList<RebuildFilesystemEntry> CaptureLinkedEntries()
    => _nodes.Values
      .Where(node => node.Linked && node.Id != RootNodeId)
      .Select(node => node.Kind == FilesystemNodeKind.Directory
        ? RebuildFilesystemEntry.Directory(BuildPath(node))
        : RebuildFilesystemEntry.File(BuildPath(node), node.Data))
      .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
      .ToArray();

  private IReadOnlyList<SpoolingRebuildFilesystemEntry> CaptureLinkedStreamingEntries()
    => _nodes.Values
      .Where(node => node.Linked && node.Id != RootNodeId)
      .Select(node => node.Kind == FilesystemNodeKind.Directory
        ? SpoolingRebuildFilesystemEntry.Directory(BuildPath(node))
        : SpoolingRebuildFilesystemEntry.File(
          BuildPath(node), node.Length, () => OpenNodeRead(node)))
      .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
      .ToArray();

  private static Stream OpenNodeRead(Node node) {
    if (node.Spool is null)
      return new MemoryStream(node.Data, writable: false);
    node.Spool.Flush();
    return new FileStream(node.Spool.Name, FileMode.Open, FileAccess.Read,
      FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
  }

  private static FileStream CreateSpool() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb-node-{Guid.NewGuid():N}.tmp");
    return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
      FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
  }

  private void EnsureSpool(Node node) {
    if (node.Spool is not null)
      return;
    var spool = CreateSpool();
    try {
      if (node.Data.Length != 0)
        spool.Write(node.Data);
      node.Spool = spool;
      node.Data = [];
    } catch {
      spool.Dispose();
      throw;
    }
  }

  private string BuildPath(Node node) {
    var parts = new List<string>();
    for (var current = node; current.Id != RootNodeId; current = RequireNode(current.Parent))
      parts.Add(current.Name);
    parts.Reverse();
    return string.Join('/', parts);
  }

  private Node NewNode(FilesystemNodeId parent, string name, FilesystemNodeKind kind, byte[] data) {
    var id = new FilesystemNodeId(++_nextNodeValue, 1);
    var node = new Node { Id = id, Parent = parent, Name = name, Kind = kind, Data = data };
    _nodes.Add(id, node);
    return node;
  }

  private Node RequireNode(FilesystemNodeId nodeId)
    => _nodes.TryGetValue(nodeId, out var node)
      ? node
      : throw new FileNotFoundException($"Filesystem node {nodeId.Value}:{nodeId.Generation} does not exist in this session.");

  private Node RequireDirectory(FilesystemNodeId nodeId) {
    var node = RequireNode(nodeId);
    if (node.Kind != FilesystemNodeKind.Directory)
      throw new DirectoryNotFoundException(node.Name);
    return node;
  }

  private Node RequireChild(Node parent, string name) {
    ValidateComponent(name);
    return _children[parent.Id].TryGetValue(name, out var node) && node.Linked
      ? node
      : throw new FileNotFoundException($"Filesystem entry '{name}' does not exist.", name);
  }

  private void Unlink(Node parent, Node node, bool markDirty = true) {
    _children[parent.Id].Remove(node.Name);
    node.Linked = false;
    if (markDirty)
      MarkDirty();
  }

  private bool IsDescendant(Node candidate, Node ancestor) {
    for (var current = candidate; current.Id != RootNodeId; current = RequireNode(current.Parent))
      if (current.Id == ancestor.Id)
        return true;
    return false;
  }

  private Dictionary<string, Node> NewChildMap() => new(_nameComparer);

  private void RequireCapability(FilesystemDriverCapabilities capability, string operation) {
    if ((Profile.Capabilities & capability) == 0)
      throw new NotSupportedException($"Filesystem profile '{Profile.ProfileName}' does not support {operation}.");
  }

  private void MarkDirty() => _dirty = true;

  private void EnsureContentBudget(long previousSize, long newSize) {
    // Called only while holding _gate. Deleted but still-open file nodes
    // remain budgeted because this session keeps them alive for stable handles.
    var growth = newSize - previousSize;
    if (growth > 0 && growth > _maxTotalContentBytes - _allocatedContentBytes)
      throw new IOException($"Writable mount exceeds the {_maxTotalContentBytes:N0}-byte content budget.");
  }

  private void AccountContentResize(long previousSize, long newSize)
    => _allocatedContentBytes = checked(_allocatedContentBytes + newSize - previousSize);

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

  private static string NormalizePath(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var normalized = path.Replace('\\', '/').Trim('/');
    if (normalized.Length == 0)
      throw new InvalidDataException("Filesystem snapshot entries must have a non-empty relative path.");
    foreach (var component in normalized.Split('/'))
      ValidateComponent(component);
    return normalized;
  }

  private static int PathDepth(string path)
    => path.Count(static character => character is '/' or '\\');

  private static void ValidateComponent(string name) {
    if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.IndexOf('\0') >= 0)
      throw new ArgumentException($"'{name}' is not a valid single filesystem path component.", nameof(name));
  }

  private sealed class FileHandle(
    MutableRebuildFilesystemSession owner,
    Node node,
    FileAccess access
  ) : IFilesystemFileHandle {
    private bool _disposed;

    public FilesystemNodeId NodeId => node.Id;

    public long Length {
      get {
        lock (owner._gate) {
          ThrowIfDisposed();
          owner.ThrowIfDisposed();
          return node.Length;
        }
      }
    }

    public int Read(long offset, Span<byte> destination) {
      lock (owner._gate) {
        ThrowIfDisposed();
        owner.ThrowIfDisposed();
        if ((access & FileAccess.Read) == 0)
          throw new UnauthorizedAccessException("This file handle was not opened for reading.");
        if (offset < 0)
          throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset >= node.Length || destination.IsEmpty)
          return 0;
        var count = (int)Math.Min(destination.Length, node.Length - offset);
        if (node.Spool is { } spool)
          return RandomAccess.Read(spool.SafeFileHandle, destination[..count], offset);
        node.Data.AsSpan(checked((int)offset), count).CopyTo(destination);
        return count;
      }
    }

    public void Write(long offset, ReadOnlySpan<byte> source) {
      lock (owner._gate) {
        ThrowIfDisposed();
        owner.ThrowIfDisposed();
        if ((access & FileAccess.Write) == 0)
          throw new UnauthorizedAccessException("This file handle was not opened for writing.");
        if (offset < 0)
          throw new ArgumentOutOfRangeException(nameof(offset));
        if (source.IsEmpty)
          return;
        var end = checked(offset + source.Length);
        if (owner._streamRebuild is not null) {
          owner.EnsureSpool(node);
          var oldLength = node.Length;
          owner.EnsureContentBudget(oldLength, Math.Max(oldLength, end));
          try {
            RandomAccess.Write(node.Spool!.SafeFileHandle, source, offset);
          } finally {
            owner.AccountContentResize(oldLength, node.Length);
            owner.MarkDirty();
          }
          return;
        }

        if (end > Array.MaxLength)
          throw new NotSupportedException($"The managed rebuild session currently limits one mutable file to {Array.MaxLength:N0} bytes.");
        if (end > node.Data.LongLength) {
          var previousSize = node.Data.LongLength;
          owner.EnsureContentBudget(previousSize, end);
          Array.Resize(ref node.Data, checked((int)end));
          owner.AccountContentResize(previousSize, end);
        }
        source.CopyTo(node.Data.AsSpan(checked((int)offset)));
        owner.MarkDirty();
      }
    }

    public void SetLength(long length) {
      lock (owner._gate) {
        ThrowIfDisposed();
        owner.ThrowIfDisposed();
        if ((access & FileAccess.Write) == 0)
          throw new UnauthorizedAccessException("This file handle was not opened for writing.");
        owner.RequireCapability(FilesystemDriverCapabilities.Truncate, "truncate files");
        if (length < 0 || (owner._streamRebuild is null && length > Array.MaxLength))
          throw new ArgumentOutOfRangeException(nameof(length));
        if (length == node.Length)
          return;
        var previousSize = node.Length;
        owner.EnsureContentBudget(previousSize, length);
        if (owner._streamRebuild is not null) {
          owner.EnsureSpool(node);
          node.Spool!.SetLength(length);
        } else {
          Array.Resize(ref node.Data, checked((int)length));
        }
        owner.AccountContentResize(previousSize, length);
        owner.MarkDirty();
      }
    }

    public void Flush() {
      ThrowIfDisposed();
      owner.Flush();
    }

    public void Dispose() => _disposed = true;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
  }
}
