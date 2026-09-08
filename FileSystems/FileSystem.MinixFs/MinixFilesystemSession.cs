#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileSystem.MinixFs;

internal sealed class MinixFilesystemSession : IFilesystemSession {
  private const uint RootInode = 1;
  private const ushort TypeMask = 0xF000;

  private readonly Stream _image;
  private readonly bool _readOnly;
  private readonly bool _leaveOpen;
  private readonly object _gate = new();
  private readonly MinixMountedVolume _volume;
  private readonly Dictionary<uint, ulong> _generations = [];
  private readonly Dictionary<uint, int> _openHandles = [];
  private readonly HashSet<uint> _pendingUnlinks = [];
  private bool _disposed;

  public MinixFilesystemSession(
      Stream image,
      FilesystemDriverProfile profile,
      bool readOnly,
      bool leaveOpen) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("MINIX mounted access requires a readable, seekable image.", nameof(image));
    if (!readOnly && !image.CanWrite)
      throw new ArgumentException("Writable MINIX mounting requires a writable image stream.", nameof(image));
    if (!readOnly && !profile.CanMountWritable)
      throw new NotSupportedException("This MINIX profile is not qualified for mounted writes.");

    _image = image;
    _readOnly = readOnly;
    _leaveOpen = leaveOpen;
    Profile = profile;
    var geometry = MinixMountedGeometry.Parse(image);
    _volume = new MinixMountedVolume(image, geometry);
    _volume.ValidateNamespace();
    RootNodeId = NodeId(RootInode);
  }

  public FilesystemDriverProfile Profile { get; }
  public FilesystemNodeId RootNodeId { get; }

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) {
    lock (_gate) {
      EnsureNotDisposed();
      return ToInfo(Resolve(nodeId));
    }
  }

  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      EnsureNotDisposed();
      _volume.ValidateName(name);
      var parent = ResolveDirectory(parentDirectory);
      var entry = _volume.FindDirectoryEntry(parent, name);
      return entry is null ? null : NodeId(entry.Value.Inode);
    }
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    lock (_gate) {
      EnsureNotDisposed();
      var inode = ResolveDirectory(directory);
      var result = new List<FilesystemDirectoryEntry>();
      foreach (var entry in _volume.ReadDirectoryEntries(inode)) {
        if (entry.Name is "." or "..") continue;
        var child = _volume.ReadInode(entry.Inode);
        result.Add(new FilesystemDirectoryEntry(entry.Name, NodeId(child.Number), child.Kind));
      }
      return result;
    }
  }

  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
    lock (_gate) {
      EnsureNotDisposed();
      if ((access & ~(FileAccess.Read | FileAccess.Write)) != 0)
        throw new ArgumentOutOfRangeException(nameof(access));
      if (_readOnly && (access & FileAccess.Write) != 0)
        throw new UnauthorizedAccessException("MINIX session is read-only.");
      var inode = Resolve(nodeId);
      if (inode.Kind != FilesystemNodeKind.RegularFile)
        throw new InvalidOperationException($"MINIX inode {inode.Number} is {inode.Kind}, not a regular file.");
      _openHandles[inode.Number] = _openHandles.GetValueOrDefault(inode.Number) + 1;
      return new FileHandle(this, nodeId, access);
    }
  }

  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      EnsureWritable();
      ValidateUserName(name);
      var parent = ResolveDirectory(parentDirectory);
      EnsureNameAvailable(parent, name);
      var inode = _volume.AllocateInode(FilesystemNodeKind.RegularFile);
      var nodeId = BumpGeneration(inode.Number);
      try {
        _volume.InsertDirectoryEntry(parent, name, inode.Number);
      } catch {
        _volume.FreeInode(inode);
        throw;
      }
      FlushCore();
      return nodeId;
    }
  }

  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      EnsureWritable();
      ValidateUserName(name);
      var parent = ResolveDirectory(parentDirectory);
      EnsureNameAvailable(parent, name);
      if (parent.Links >= _volume.Geometry.MaxLinks)
        throw new IOException($"{_volume.Geometry.VersionName} directory link-count limit reached.");

      var inode = _volume.AllocateInode(FilesystemNodeKind.Directory);
      var nodeId = BumpGeneration(inode.Number);
      try {
        _volume.InitializeDirectory(inode, parent.Number);
        _volume.InsertDirectoryEntry(parent, name, inode.Number);
        parent.Links++;
        MinixMountedVolume.Touch(parent, modify: true, change: true);
        _volume.WriteInode(parent);
      } catch {
        _volume.FreeInode(inode);
        throw;
      }
      FlushCore();
      return nodeId;
    }
  }

  public void DeleteFile(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      EnsureWritable();
      ValidateUserName(name);
      var parent = ResolveDirectory(parentDirectory);
      var entry = _volume.FindDirectoryEntry(parent, name)
        ?? throw new FileNotFoundException($"MINIX entry '{name}' does not exist.");
      var inode = _volume.ReadInode(entry.Inode);
      if (inode.Kind == FilesystemNodeKind.Directory)
        throw new IOException("Use RemoveDirectory for MINIX directories.");
      UnlinkNonDirectory(parent, name, inode);
      FlushCore();
    }
  }

  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) {
    lock (_gate) {
      EnsureWritable();
      ValidateUserName(name);
      var parent = ResolveDirectory(parentDirectory);
      var entry = _volume.FindDirectoryEntry(parent, name)
        ?? throw new DirectoryNotFoundException($"MINIX directory '{name}' does not exist.");
      var directory = _volume.ReadInode(entry.Inode);
      RemoveEmptyDirectory(parent, name, directory);
      FlushCore();
    }
  }

  public void Rename(
      FilesystemNodeId oldParent,
      string oldName,
      FilesystemNodeId newParent,
      string newName,
      bool replace) {
    lock (_gate) {
      EnsureWritable();
      ValidateUserName(oldName);
      ValidateUserName(newName);
      var sourceParent = ResolveDirectory(oldParent);
      var destinationParent = ResolveDirectory(newParent);
      if (sourceParent.Number == destinationParent.Number && string.Equals(oldName, newName, StringComparison.Ordinal))
        return;

      var sourceEntry = _volume.FindDirectoryEntry(sourceParent, oldName)
        ?? throw new FileNotFoundException($"MINIX entry '{oldName}' does not exist.");
      var source = _volume.ReadInode(sourceEntry.Inode);
      var destinationEntry = _volume.FindDirectoryEntry(destinationParent, newName);
      if (destinationEntry is not null && destinationEntry.Value.Inode == source.Number)
        return;
      if (destinationEntry is not null && !replace)
        throw new IOException($"MINIX entry '{newName}' already exists.");

      if (source.Kind == FilesystemNodeKind.Directory && sourceParent.Number != destinationParent.Number)
        EnsureDirectoryMoveDoesNotCycle(source.Number, destinationParent.Number);

      if (destinationEntry is not null) {
        var destination = _volume.ReadInode(destinationEntry.Value.Inode);
        if ((source.Kind == FilesystemNodeKind.Directory) != (destination.Kind == FilesystemNodeKind.Directory))
          throw new IOException("A MINIX directory cannot replace a non-directory, or vice versa.");
        if (destination.Kind == FilesystemNodeKind.Directory)
          RemoveEmptyDirectory(destinationParent, newName, destination);
        else
          UnlinkNonDirectory(destinationParent, newName, destination);

        sourceParent = _volume.ReadInode(sourceParent.Number);
        destinationParent = _volume.ReadInode(destinationParent.Number);
      }

      if (sourceParent.Number == destinationParent.Number) {
        _volume.RenameDirectoryEntry(sourceParent, oldName, newName, source.Number);
        MinixMountedVolume.Touch(source, modify: false, change: true);
        _volume.WriteInode(source);
        FlushCore();
        return;
      }

      _volume.InsertDirectoryEntry(destinationParent, newName, source.Number);
      try {
        _volume.RemoveDirectoryEntry(sourceParent, oldName, source.Number);
      } catch {
        _volume.RemoveDirectoryEntry(destinationParent, newName, source.Number);
        throw;
      }

      if (source.Kind == FilesystemNodeKind.Directory) {
        if (destinationParent.Links >= _volume.Geometry.MaxLinks)
          throw new IOException($"{_volume.Geometry.VersionName} destination directory link-count limit reached.");
        if (sourceParent.Links == 0)
          throw new InvalidDataException($"MINIX directory inode {sourceParent.Number} has invalid zero link count.");
        _volume.RewriteDotDot(source, destinationParent.Number);
        sourceParent.Links--;
        destinationParent.Links++;
        MinixMountedVolume.Touch(sourceParent, modify: true, change: true);
        MinixMountedVolume.Touch(destinationParent, modify: true, change: true);
        _volume.WriteInode(sourceParent);
        _volume.WriteInode(destinationParent);
      }

      MinixMountedVolume.Touch(source, modify: false, change: true);
      _volume.WriteInode(source);
      FlushCore();
    }
  }

  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName) {
    lock (_gate) {
      EnsureWritable();
      ValidateUserName(newName);
      var inode = Resolve(existingNode);
      if (inode.Kind == FilesystemNodeKind.Directory)
        throw new IOException("MINIX does not permit hard links to directories through this mounted API.");
      if (inode.Links >= _volume.Geometry.MaxLinks)
        throw new IOException($"{_volume.Geometry.VersionName} link-count limit reached.");
      var parent = ResolveDirectory(newParent);
      EnsureNameAvailable(parent, newName);
      _volume.InsertDirectoryEntry(parent, newName, inode.Number);
      inode.Links++;
      MinixMountedVolume.Touch(inode, modify: false, change: true);
      _volume.WriteInode(inode);
      FlushCore();
    }
  }

  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target) {
    lock (_gate) {
      EnsureWritable();
      ValidateUserName(name);
      ArgumentNullException.ThrowIfNull(target);
      var parent = ResolveDirectory(parentDirectory);
      EnsureNameAvailable(parent, name);
      var bytes = EncodeSymbolicLink(target);
      var inode = _volume.AllocateInode(FilesystemNodeKind.SymbolicLink);
      var nodeId = BumpGeneration(inode.Number);
      try {
        _volume.WriteFileBytes(inode, 0, bytes);
        _volume.InsertDirectoryEntry(parent, name, inode.Number);
      } catch {
        _volume.FreeInode(inode);
        throw;
      }
      FlushCore();
      return nodeId;
    }
  }

  public string ReadSymbolicLink(FilesystemNodeId nodeId) {
    lock (_gate) {
      EnsureNotDisposed();
      var inode = Resolve(nodeId);
      if (inode.Kind != FilesystemNodeKind.SymbolicLink)
        throw new InvalidOperationException("MINIX node is not a symbolic link.");
      if (inode.Size > int.MaxValue) throw new NotSupportedException("MINIX symbolic-link target is too large.");
      var bytes = new byte[(int)inode.Size];
      _ = _volume.ReadFileBytes(inode, 0, bytes);
      var length = bytes.Length > 0 && bytes[^1] == 0 ? bytes.Length - 1 : bytes.Length;
      return Encoding.Latin1.GetString(bytes, 0, length);
    }
  }

  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) {
    ArgumentNullException.ThrowIfNull(patch);
    lock (_gate) {
      EnsureWritable();
      if (patch.Created is not null)
        throw new NotSupportedException("MINIX inodes do not have a creation-time field.");
      var inode = Resolve(nodeId);
      if (patch.NativeAttributes is { } native) {
        if (native > ushort.MaxValue)
          throw new NotSupportedException("MINIX mounted NativeAttributes currently exposes only the 16-bit mode field.");
        var requestedMode = (ushort)native;
        if ((requestedMode & TypeMask) != (inode.Mode & TypeMask))
          throw new IOException("Changing a MINIX inode object type through metadata is not supported.");
        inode.Mode = requestedMode;
      }
      if (patch.Accessed is { } accessed) inode.AccessTime = ToUnixSeconds(accessed);
      if (patch.Modified is { } modified) inode.ModifyTime = ToUnixSeconds(modified);
      inode.ChangeTime = MinixMountedVolume.NowSeconds();
      _volume.WriteInode(inode);
      FlushCore();
    }
  }

  public void Flush() {
    lock (_gate) {
      EnsureNotDisposed();
      FlushCore();
    }
  }

  public IFilesystemTransaction BeginTransaction()
    => throw new NotSupportedException("MINIX uses direct bitmap/inode updates and exposes no journal transaction boundary.");

  public void Dispose() {
    lock (_gate) {
      if (_disposed) return;
      foreach (var inodeNumber in _pendingUnlinks.ToArray()) {
        if (!_volume.IsInodeAllocated(inodeNumber)) continue;
        var inode = _volume.ReadInode(inodeNumber);
        if (inode.Links == 0) _volume.FreeInode(inode);
      }
      _pendingUnlinks.Clear();
      FlushCore();
      _disposed = true;
      if (!_leaveOpen) _image.Dispose();
    }
  }

  private FilesystemNodeInfo ToInfo(MinixMountedInode inode) {
    DateTimeOffset? modified = FromUnixSeconds(inode.ModifyTime);
    DateTimeOffset? accessed = FromUnixSeconds(inode.AccessTime);
    DateTimeOffset? changed = _volume.Geometry.HasSeparateTimes ? FromUnixSeconds(inode.ChangeTime) : null;
    return new FilesystemNodeInfo(
      NodeId(inode.Number),
      inode.Kind,
      inode.Kind == FilesystemNodeKind.Directory ? 0 : inode.Size,
      _volume.AllocatedBytes(inode),
      inode.Links,
      inode.Mode,
      Created: null,
      Modified: modified,
      Accessed: accessed,
      Changed: changed);
  }

  private MinixMountedInode Resolve(FilesystemNodeId nodeId) {
    if (nodeId.Value is 0 or > uint.MaxValue)
      throw new FileNotFoundException("Invalid MINIX inode identity.");
    var number = (uint)nodeId.Value;
    if (!_volume.IsInodeAllocated(number))
      throw new FileNotFoundException($"MINIX inode {number} is not allocated.");
    if (nodeId.Generation != Generation(number))
      throw new FileNotFoundException($"MINIX inode identity {nodeId.Value}:{nodeId.Generation} is stale.");
    var inode = _volume.ReadInode(number);
    if (inode.Mode == 0)
      throw new FileNotFoundException($"MINIX inode {number} has been cleared.");
    return inode;
  }

  private MinixMountedInode ResolveDirectory(FilesystemNodeId nodeId) {
    var inode = Resolve(nodeId);
    if (inode.Kind != FilesystemNodeKind.Directory)
      throw new DirectoryNotFoundException($"MINIX inode {inode.Number} is not a directory.");
    return inode;
  }

  private void EnsureNameAvailable(MinixMountedInode parent, string name) {
    if (_volume.FindDirectoryEntry(parent, name) is not null)
      throw new IOException($"MINIX entry '{name}' already exists.");
  }

  private void UnlinkNonDirectory(MinixMountedInode parent, string name, MinixMountedInode inode) {
    if (inode.Kind == FilesystemNodeKind.Directory)
      throw new IOException("MINIX directory requires rmdir semantics.");
    if (inode.Links == 0)
      throw new InvalidDataException($"MINIX inode {inode.Number} has zero links before unlink.");
    _volume.RemoveDirectoryEntry(parent, name, inode.Number);
    inode.Links--;
    MinixMountedVolume.Touch(inode, modify: false, change: true);
    _volume.WriteInode(inode);
    if (inode.Links != 0) return;
    if (_openHandles.GetValueOrDefault(inode.Number) > 0)
      _pendingUnlinks.Add(inode.Number);
    else
      _volume.FreeInode(inode);
  }

  private void RemoveEmptyDirectory(MinixMountedInode parent, string name, MinixMountedInode directory) {
    if (directory.Kind != FilesystemNodeKind.Directory)
      throw new IOException("The named MINIX entry is not a directory.");
    if (directory.Number == RootInode)
      throw new IOException("MINIX root directory cannot be removed.");
    if (_volume.ReadDirectoryEntries(directory).Any(e => e.Name is not "." and not ".."))
      throw new IOException("MINIX directory is not empty.");
    if (parent.Links == 0)
      throw new InvalidDataException($"MINIX parent inode {parent.Number} has zero links before rmdir.");
    _volume.RemoveDirectoryEntry(parent, name, directory.Number);
    parent.Links--;
    MinixMountedVolume.Touch(parent, modify: true, change: true);
    _volume.WriteInode(parent);
    directory.Links = 0;
    _volume.WriteInode(directory);
    _volume.FreeInode(directory);
  }

  private void EnsureDirectoryMoveDoesNotCycle(uint movingDirectory, uint newParent) {
    var current = newParent;
    var visited = new HashSet<uint>();
    while (true) {
      if (current == movingDirectory)
        throw new IOException("Cannot move a MINIX directory into its own subtree.");
      if (current == RootInode) return;
      if (!visited.Add(current)) throw new InvalidDataException("MINIX '..' chain contains a cycle.");
      var inode = _volume.ReadInode(current);
      if (inode.Kind != FilesystemNodeKind.Directory)
        throw new InvalidDataException($"MINIX '..' chain reached non-directory inode {current}.");
      current = _volume.FindDirectoryEntry(inode, "..")?.Inode
        ?? throw new InvalidDataException($"MINIX directory inode {current} has no '..' entry.");
    }
  }

  private void ValidateUserName(string name) {
    _volume.ValidateName(name);
    if (name is "." or "..")
      throw new ArgumentException("'.' and '..' are reserved MINIX directory entries.", nameof(name));
  }

  private byte[] EncodeSymbolicLink(string target) {
    foreach (var c in target)
      if (c > byte.MaxValue)
        throw new ArgumentException("Mounted MINIX symbolic-link targets must be representable as Latin-1 bytes.", nameof(target));
    var payload = Encoding.Latin1.GetBytes(target);
    var withNul = new byte[payload.Length + 1];
    payload.CopyTo(withNul, 0);
    return withNul;
  }

  private FilesystemNodeId NodeId(uint inodeNumber) => new(inodeNumber, Generation(inodeNumber));

  private ulong Generation(uint inodeNumber)
    => _generations.TryGetValue(inodeNumber, out var generation) ? generation : 1UL;

  private FilesystemNodeId BumpGeneration(uint inodeNumber) {
    var next = checked(Generation(inodeNumber) + 1);
    _generations[inodeNumber] = next;
    return new FilesystemNodeId(inodeNumber, next);
  }

  private static DateTimeOffset? FromUnixSeconds(uint value) {
    if (value == 0) return null;
    try { return DateTimeOffset.FromUnixTimeSeconds(value); }
    catch (ArgumentOutOfRangeException) { return null; }
  }

  private static uint ToUnixSeconds(DateTimeOffset value) {
    var seconds = value.ToUnixTimeSeconds();
    if (seconds < 0 || seconds > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
    return (uint)seconds;
  }

  private void EnsureWritable() {
    EnsureNotDisposed();
    if (_readOnly) throw new UnauthorizedAccessException("MINIX session is read-only.");
  }

  private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
  private void FlushCore() => _volume.Flush();

  private void CloseHandle(uint inodeNumber) {
    lock (_gate) {
      if (_disposed) return;
      if (!_openHandles.TryGetValue(inodeNumber, out var count) || count <= 0) return;
      if (count == 1) _openHandles.Remove(inodeNumber);
      else _openHandles[inodeNumber] = count - 1;
      if (!_pendingUnlinks.Contains(inodeNumber) || _openHandles.GetValueOrDefault(inodeNumber) != 0) return;
      if (_volume.IsInodeAllocated(inodeNumber)) {
        var inode = _volume.ReadInode(inodeNumber);
        if (inode.Links == 0) _volume.FreeInode(inode);
      }
      _pendingUnlinks.Remove(inodeNumber);
      FlushCore();
    }
  }

  private sealed class FileHandle(
      MinixFilesystemSession session,
      FilesystemNodeId nodeId,
      FileAccess access) : IFilesystemFileHandle {
    private bool _disposed;

    public FilesystemNodeId NodeId => nodeId;

    public long Length {
      get {
        lock (session._gate) {
          ObjectDisposedException.ThrowIf(_disposed, this);
          session.EnsureNotDisposed();
          return session.Resolve(nodeId).Size;
        }
      }
    }

    public int Read(long offset, Span<byte> destination) {
      lock (session._gate) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        session.EnsureNotDisposed();
        if ((access & FileAccess.Read) == 0)
          throw new UnauthorizedAccessException("MINIX file handle was not opened for reading.");
        return session._volume.ReadFileBytes(session.Resolve(nodeId), offset, destination);
      }
    }

    public void Write(long offset, ReadOnlySpan<byte> source) {
      lock (session._gate) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        session.EnsureWritable();
        if ((access & FileAccess.Write) == 0)
          throw new UnauthorizedAccessException("MINIX file handle was not opened for writing.");
        session._volume.WriteFileBytes(session.Resolve(nodeId), offset, source);
      }
    }

    public void SetLength(long length) {
      lock (session._gate) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        session.EnsureWritable();
        if ((access & FileAccess.Write) == 0)
          throw new UnauthorizedAccessException("MINIX file handle was not opened for writing.");
        session._volume.SetFileLength(session.Resolve(nodeId), length);
      }
    }

    public void Flush() {
      lock (session._gate) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        session.EnsureNotDisposed();
        session.FlushCore();
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;
      session.CloseHandle((uint)nodeId.Value);
    }
  }
}