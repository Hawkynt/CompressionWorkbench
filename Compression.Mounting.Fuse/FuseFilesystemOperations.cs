using System.Collections.Concurrent;
using Compression.Registry;

namespace Compression.Mounting.Fuse;

internal static class FuseErrno {
  public const int Success = 0;
  public const int NoEntry = 2;
  public const int Io = 5;
  public const int BadFileDescriptor = 9;
  public const int AccessDenied = 13;
  public const int Exists = 17;
  public const int NotDirectory = 20;
  public const int IsDirectory = 21;
  public const int InvalidArgument = 22;
  public const int ReadOnlyFileSystem = 30;
  public const int NotImplemented = 38;
  public const int NotEmpty = 39;
  public const int NotSupported = 95;
}

internal readonly record struct FuseNodeSnapshot(ulong Inode, FilesystemNodeInfo Node);

internal readonly record struct FuseDirectoryEntrySnapshot(
  string Name,
  ulong Inode,
  FilesystemNodeInfo Node,
  long NextOffset
);

/// <summary>
/// Backend-local stable inode/handle state. Pathnames are used only for lookup
/// and namespace mutations; once the kernel has an inode or file handle, later
/// I/O remains path-independent across rename and unlink.
/// </summary>
internal sealed class FuseFilesystemOperations : IDisposable {
  public const ulong RootInode = 1;

  private const int OpenAccessMode = 0x3;
  private const int OpenWriteOnly = 0x1;
  private const int OpenReadWrite = 0x2;
  private const int OpenTruncate = 0x200;
  private const int OpenAppend = 0x400;

  private readonly IFilesystemSession _filesystem;
  private readonly bool _readOnly;
  private readonly FuseInodeTable _inodes;
  private readonly ConcurrentDictionary<ulong, FuseOpenFile> _files = [];
  private readonly ConcurrentDictionary<ulong, FuseOpenDirectory> _directories = [];
  private long _nextHandle;
  private int _disposed;

  public FuseFilesystemOperations(IFilesystemSession filesystem, bool readOnly = true) {
    this._filesystem = filesystem ?? throw new ArgumentNullException(nameof(filesystem));
    this._readOnly = readOnly;
    this._inodes = new(filesystem.RootNodeId);
  }

  public FilesystemDriverProfile Profile => this._filesystem.Profile;
  public bool IsReadOnly => this._readOnly;

  public int Lookup(ulong parentInode, string name, out FuseNodeSnapshot result) {
    result = default;
    if (Volatile.Read(ref this._disposed) != 0)
      return FuseErrno.Io;
    if (string.IsNullOrEmpty(name))
      return FuseErrno.NoEntry;

    try {
      if (!this._inodes.TryGetNode(parentInode, out var parentNodeId))
        return FuseErrno.NoEntry;
      if (this._filesystem.Stat(parentNodeId).Kind != FilesystemNodeKind.Directory)
        return FuseErrno.NotDirectory;
      var nodeId = this._filesystem.Lookup(parentNodeId, name);
      if (nodeId is null)
        return FuseErrno.NoEntry;

      var inode = this._inodes.RegisterLookup(nodeId.Value, parentInode);
      result = new(inode, this._filesystem.Stat(nodeId.Value));
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int GetAttributes(ulong inode, out FuseNodeSnapshot result) {
    result = default;
    if (Volatile.Read(ref this._disposed) != 0)
      return FuseErrno.Io;
    try {
      if (!this._inodes.TryGetNode(inode, out var nodeId))
        return FuseErrno.NoEntry;
      result = new(inode, this._filesystem.Stat(nodeId));
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int SetAttributes(ulong inode, int toSet, long size, ulong? handleId, out FuseNodeSnapshot result) {
    result = default;
    if (this._readOnly)
      return FuseErrno.ReadOnlyFileSystem;
    if (!this._inodes.TryGetNode(inode, out var nodeId))
      return FuseErrno.NoEntry;

    const int sizeFlag = 1 << 3;
    const int forceFlag = 1 << 9;
    const int killSuidFlag = 1 << 11;
    const int killSgidFlag = 1 << 12;
    const int fileHandleFlag = 1 << 13;
    const int killPrivFlag = 1 << 14;
    const int openFlag = 1 << 15;
    const int ignorableFlags = forceFlag | killSuidFlag | killSgidFlag | fileHandleFlag | killPrivFlag | openFlag;
    if ((toSet & ~(sizeFlag | ignorableFlags)) != 0)
      return FuseErrno.NotSupported;
    if ((toSet & sizeFlag) != 0) {
      if (size < 0)
        return FuseErrno.InvalidArgument;
      if (!Has(FilesystemDriverCapabilities.Truncate))
        return FuseErrno.NotSupported;

      try {
        if (handleId is { } id && this._files.TryGetValue(id, out var opened))
          opened.File.SetLength(size);
        else {
          using var file = this._filesystem.OpenFile(nodeId, FileAccess.ReadWrite);
          file.SetLength(size);
        }
      } catch (Exception ex) {
        return MapException(ex);
      }
    }

    try {
      result = new(inode, this._filesystem.Stat(nodeId));
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int Access(ulong inode, int mask) {
    const int execute = 0x1;
    const int write = 0x2;
    const int read = 0x4;
    const int valid = execute | write | read;

    if (Volatile.Read(ref this._disposed) != 0)
      return FuseErrno.Io;
    if ((mask & ~valid) != 0)
      return FuseErrno.InvalidArgument;

    try {
      if (!this._inodes.TryGetNode(inode, out var nodeId))
        return FuseErrno.NoEntry;
      var node = this._filesystem.Stat(nodeId);
      if ((mask & write) != 0 && this._readOnly)
        return FuseErrno.ReadOnlyFileSystem;
      if ((mask & execute) != 0 && node.Kind is not FilesystemNodeKind.Directory and not FilesystemNodeKind.SymbolicLink)
        return FuseErrno.AccessDenied;
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int ReadSymbolicLink(ulong inode, out string target) {
    target = string.Empty;
    if (Volatile.Read(ref this._disposed) != 0)
      return FuseErrno.Io;
    try {
      if (!this._inodes.TryGetNode(inode, out var nodeId))
        return FuseErrno.NoEntry;
      if (this._filesystem.Stat(nodeId).Kind != FilesystemNodeKind.SymbolicLink)
        return FuseErrno.InvalidArgument;
      if (!Has(FilesystemDriverCapabilities.SymbolicLinks))
        return FuseErrno.NotSupported;
      target = this._filesystem.ReadSymbolicLink(nodeId);
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public void Forget(ulong inode, ulong lookupCount) => this._inodes.Forget(inode, lookupCount);

  public int OpenFile(ulong inode, int flags, out ulong handleId) {
    handleId = 0;
    if (Volatile.Read(ref this._disposed) != 0)
      return FuseErrno.Io;

    var accessMode = flags & OpenAccessMode;
    if (accessMode == OpenAccessMode)
      return FuseErrno.InvalidArgument;
    var writes = accessMode is OpenWriteOnly or OpenReadWrite;
    var truncates = (flags & OpenTruncate) != 0;
    if (this._readOnly && (writes || truncates))
      return FuseErrno.ReadOnlyFileSystem;
    if (writes && !Has(FilesystemDriverCapabilities.WriteData))
      return FuseErrno.NotSupported;
    if (truncates && !Has(FilesystemDriverCapabilities.Truncate))
      return FuseErrno.NotSupported;

    IFilesystemFileHandle? file = null;
    var inodePinned = false;
    var registered = false;
    try {
      if (!this._inodes.TryGetNode(inode, out var nodeId))
        return FuseErrno.NoEntry;
      var node = this._filesystem.Stat(nodeId);
      if (node.Kind == FilesystemNodeKind.Directory)
        return FuseErrno.IsDirectory;
      if (node.Kind != FilesystemNodeKind.RegularFile)
        return FuseErrno.NotSupported;

      var access = accessMode switch {
        OpenWriteOnly => FileAccess.Write,
        OpenReadWrite => FileAccess.ReadWrite,
        _ => FileAccess.Read,
      };
      file = this._filesystem.OpenFile(nodeId, access);
      if (truncates)
        file.SetLength(0);
      this._inodes.Pin(inode);
      inodePinned = true;

      handleId = this.NextHandleId();
      registered = this._files.TryAdd(handleId, new(inode, file, access, (flags & OpenAppend) != 0));
      return registered ? FuseErrno.Success : FuseErrno.Io;
    } catch (Exception ex) {
      handleId = 0;
      return MapException(ex);
    } finally {
      if (!registered) {
        file?.Dispose();
        if (inodePinned)
          this._inodes.Unpin(inode);
      }
    }
  }

  public int ReadFile(ulong handleId, long offset, Span<byte> destination, out int bytesRead) {
    bytesRead = 0;
    if (offset < 0)
      return FuseErrno.InvalidArgument;
    if (!this._files.TryGetValue(handleId, out var opened))
      return FuseErrno.BadFileDescriptor;
    if (opened.Access == FileAccess.Write)
      return FuseErrno.BadFileDescriptor;
    try {
      bytesRead = opened.File.Read(offset, destination);
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int WriteFile(ulong handleId, long offset, ReadOnlySpan<byte> source, out int bytesWritten) {
    bytesWritten = 0;
    if (this._readOnly)
      return FuseErrno.ReadOnlyFileSystem;
    if (offset < 0)
      return FuseErrno.InvalidArgument;
    if (!this._files.TryGetValue(handleId, out var opened))
      return FuseErrno.BadFileDescriptor;
    if (opened.Access == FileAccess.Read)
      return FuseErrno.BadFileDescriptor;
    if (!Has(FilesystemDriverCapabilities.WriteData))
      return FuseErrno.NotSupported;
    try {
      opened.File.Write(opened.Append ? opened.File.Length : offset, source);
      bytesWritten = source.Length;
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int CreateFile(ulong parentInode, string name, int flags, out FuseNodeSnapshot result, out ulong handleId) {
    result = default;
    handleId = 0;
    if (this._readOnly)
      return FuseErrno.ReadOnlyFileSystem;
    if (!Has(FilesystemDriverCapabilities.CreateFile))
      return FuseErrno.NotSupported;

    FilesystemNodeId parentNodeId = default;
    FilesystemNodeId nodeId = default;
    ulong inode = 0;
    var created = false;
    try {
      if (!this._inodes.TryGetNode(parentInode, out parentNodeId))
        return FuseErrno.NoEntry;
      if (this._filesystem.Stat(parentNodeId).Kind != FilesystemNodeKind.Directory)
        return FuseErrno.NotDirectory;
      if (this._filesystem.Lookup(parentNodeId, name) is not null)
        return FuseErrno.Exists;

      nodeId = this._filesystem.CreateFile(parentNodeId, name);
      created = true;
      inode = this._inodes.RegisterLookup(nodeId, parentInode);
      result = new(inode, this._filesystem.Stat(nodeId));
      var openError = this.OpenFile(inode, flags, out handleId);
      if (openError == FuseErrno.Success)
        return FuseErrno.Success;

      this._inodes.Forget(inode, 1);
      this._filesystem.DeleteFile(parentNodeId, name);
      created = false;
      result = default;
      return openError;
    } catch (Exception ex) {
      if (created) {
        try { this._filesystem.DeleteFile(parentNodeId, name); } catch { }
        if (inode != 0) this._inodes.Forget(inode, 1);
      }
      result = default;
      handleId = 0;
      return MapException(ex);
    }
  }

  public int MakeNode(ulong parentInode, string name, uint mode, out FuseNodeSnapshot result) {
    result = default;
    if (this._readOnly)
      return FuseErrno.ReadOnlyFileSystem;
    if (!Has(FilesystemDriverCapabilities.CreateFile))
      return FuseErrno.NotSupported;
    const uint fileTypeMask = 0xF000;
    const uint regularFile = 0x8000;
    if ((mode & fileTypeMask) is not 0 and not regularFile)
      return FuseErrno.NotSupported;

    try {
      if (!this._inodes.TryGetNode(parentInode, out var parentNodeId))
        return FuseErrno.NoEntry;
      if (this._filesystem.Lookup(parentNodeId, name) is not null)
        return FuseErrno.Exists;
      var nodeId = this._filesystem.CreateFile(parentNodeId, name);
      var inode = this._inodes.RegisterLookup(nodeId, parentInode);
      result = new(inode, this._filesystem.Stat(nodeId));
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int CreateDirectory(ulong parentInode, string name, out FuseNodeSnapshot result) {
    result = default;
    if (this._readOnly)
      return FuseErrno.ReadOnlyFileSystem;
    if (!Has(FilesystemDriverCapabilities.CreateDirectory))
      return FuseErrno.NotSupported;
    try {
      if (!this._inodes.TryGetNode(parentInode, out var parentNodeId))
        return FuseErrno.NoEntry;
      if (this._filesystem.Lookup(parentNodeId, name) is not null)
        return FuseErrno.Exists;
      var nodeId = this._filesystem.CreateDirectory(parentNodeId, name);
      var inode = this._inodes.RegisterLookup(nodeId, parentInode);
      result = new(inode, this._filesystem.Stat(nodeId));
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int Unlink(ulong parentInode, string name) {
    if (this._readOnly)
      return FuseErrno.ReadOnlyFileSystem;
    if (!Has(FilesystemDriverCapabilities.DeleteFile))
      return FuseErrno.NotSupported;
    try {
      if (!this._inodes.TryGetNode(parentInode, out var parentNodeId))
        return FuseErrno.NoEntry;
      var nodeId = this._filesystem.Lookup(parentNodeId, name);
      if (nodeId is null)
        return FuseErrno.NoEntry;
      if (this._filesystem.Stat(nodeId.Value).Kind == FilesystemNodeKind.Directory)
        return FuseErrno.IsDirectory;
      this._filesystem.DeleteFile(parentNodeId, name);
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int RemoveDirectory(ulong parentInode, string name) {
    if (this._readOnly)
      return FuseErrno.ReadOnlyFileSystem;
    if (!Has(FilesystemDriverCapabilities.RemoveDirectory))
      return FuseErrno.NotSupported;
    try {
      if (!this._inodes.TryGetNode(parentInode, out var parentNodeId))
        return FuseErrno.NoEntry;
      var nodeId = this._filesystem.Lookup(parentNodeId, name);
      if (nodeId is null)
        return FuseErrno.NoEntry;
      if (this._filesystem.Stat(nodeId.Value).Kind != FilesystemNodeKind.Directory)
        return FuseErrno.NotDirectory;
      if (this._filesystem.Enumerate(nodeId.Value).Count != 0)
        return FuseErrno.NotEmpty;
      this._filesystem.RemoveDirectory(parentNodeId, name);
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int Rename(ulong parentInode, string name, ulong newParentInode, string newName, uint flags) {
    if (this._readOnly)
      return FuseErrno.ReadOnlyFileSystem;
    if (!Has(FilesystemDriverCapabilities.Rename))
      return FuseErrno.NotSupported;
    const uint noReplace = 1;
    if ((flags & ~noReplace) != 0)
      return FuseErrno.NotSupported;

    try {
      if (!this._inodes.TryGetNode(parentInode, out var parentNodeId) ||
          !this._inodes.TryGetNode(newParentInode, out var newParentNodeId))
        return FuseErrno.NoEntry;
      var sourceNodeId = this._filesystem.Lookup(parentNodeId, name);
      if (sourceNodeId is null)
        return FuseErrno.NoEntry;
      if ((flags & noReplace) != 0 && this._filesystem.Lookup(newParentNodeId, newName) is not null)
        return FuseErrno.Exists;

      this._filesystem.Rename(parentNodeId, name, newParentNodeId, newName, replace: (flags & noReplace) == 0);
      this._inodes.UpdateParent(sourceNodeId.Value, newParentInode);
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int CreateSymbolicLink(ulong parentInode, string name, string target, out FuseNodeSnapshot result) {
    result = default;
    if (this._readOnly)
      return FuseErrno.ReadOnlyFileSystem;
    if (!Has(FilesystemDriverCapabilities.SymbolicLinks))
      return FuseErrno.NotSupported;
    try {
      if (!this._inodes.TryGetNode(parentInode, out var parentNodeId))
        return FuseErrno.NoEntry;
      if (this._filesystem.Lookup(parentNodeId, name) is not null)
        return FuseErrno.Exists;
      var nodeId = this._filesystem.CreateSymbolicLink(parentNodeId, name, target);
      var inode = this._inodes.RegisterLookup(nodeId, parentInode);
      result = new(inode, this._filesystem.Stat(nodeId));
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int CreateHardLink(ulong inode, ulong newParentInode, string newName, out FuseNodeSnapshot result) {
    result = default;
    if (this._readOnly)
      return FuseErrno.ReadOnlyFileSystem;
    if (!Has(FilesystemDriverCapabilities.HardLinks))
      return FuseErrno.NotSupported;
    try {
      if (!this._inodes.TryGetNode(inode, out var nodeId) || !this._inodes.TryGetNode(newParentInode, out var parentNodeId))
        return FuseErrno.NoEntry;
      if (this._filesystem.Lookup(parentNodeId, newName) is not null)
        return FuseErrno.Exists;
      this._filesystem.CreateHardLink(nodeId, parentNodeId, newName);
      var linkedInode = this._inodes.RegisterLookup(nodeId, newParentInode);
      result = new(linkedInode, this._filesystem.Stat(nodeId));
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int FlushFile(ulong handleId) {
    if (!this._files.TryGetValue(handleId, out var opened))
      return FuseErrno.BadFileDescriptor;
    if (!Has(FilesystemDriverCapabilities.Flush))
      return FuseErrno.Success;
    try {
      opened.File.Flush();
      this._filesystem.Flush();
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int ReleaseFile(ulong handleId) {
    if (!this._files.TryRemove(handleId, out var opened))
      return FuseErrno.BadFileDescriptor;
    try {
      opened.File.Dispose();
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    } finally {
      this._inodes.Unpin(opened.Inode);
    }
  }

  public int OpenDirectory(ulong inode, out ulong handleId) {
    handleId = 0;
    if (Volatile.Read(ref this._disposed) != 0)
      return FuseErrno.Io;

    List<ulong>? pinnedInodes = null;
    var registered = false;
    try {
      if (!this._inodes.TryGetNode(inode, out var nodeId))
        return FuseErrno.NoEntry;
      var node = this._filesystem.Stat(nodeId);
      if (node.Kind != FilesystemNodeKind.Directory)
        return FuseErrno.NotDirectory;
      if (!this._inodes.TryGetParent(inode, out var parentInode, out var parentNodeId))
        return FuseErrno.Io;

      var sourceEntries = this._filesystem.Enumerate(nodeId)
        .Where(static entry => entry.Name is not "." and not "..")
        .ToArray();
      var snapshot = new FuseDirectoryEntrySnapshot[sourceEntries.Length + 2];
      snapshot[0] = new(".", inode, node, 1);
      snapshot[1] = new("..", parentInode, this._filesystem.Stat(parentNodeId), 2);

      pinnedInodes = new(sourceEntries.Length + 1);
      this._inodes.Pin(inode);
      pinnedInodes.Add(inode);
      for (var i = 0; i < sourceEntries.Length; ++i) {
        var entry = sourceEntries[i];
        var childInode = this._inodes.GetOrAdd(entry.NodeId, inode);
        this._inodes.Pin(childInode);
        pinnedInodes.Add(childInode);
        snapshot[i + 2] = new(entry.Name, childInode, this._filesystem.Stat(entry.NodeId), i + 3L);
      }

      handleId = this.NextHandleId();
      registered = this._directories.TryAdd(handleId, new(inode, snapshot, [.. pinnedInodes]));
      return registered ? FuseErrno.Success : FuseErrno.Io;
    } catch (Exception ex) {
      handleId = 0;
      return MapException(ex);
    } finally {
      if (!registered && pinnedInodes is not null)
        foreach (var pinnedInode in pinnedInodes)
          this._inodes.Unpin(pinnedInode);
    }
  }

  public int ReadDirectory(ulong handleId, long offset, out IReadOnlyList<FuseDirectoryEntrySnapshot> entries) {
    entries = [];
    if (offset < 0)
      return FuseErrno.InvalidArgument;
    if (!this._directories.TryGetValue(handleId, out var opened))
      return FuseErrno.BadFileDescriptor;
    if (offset >= opened.Entries.Count)
      return FuseErrno.Success;
    entries = opened.Entries.Skip(checked((int)offset)).ToArray();
    return FuseErrno.Success;
  }

  public int ReleaseDirectory(ulong handleId) {
    if (!this._directories.TryRemove(handleId, out var opened))
      return FuseErrno.BadFileDescriptor;
    foreach (var inode in opened.PinnedInodes)
      this._inodes.Unpin(inode);
    return FuseErrno.Success;
  }

  public int FlushFilesystem() {
    if (!Has(FilesystemDriverCapabilities.Flush))
      return FuseErrno.Success;
    try {
      this._filesystem.Flush();
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public void Dispose() {
    if (Interlocked.Exchange(ref this._disposed, 1) != 0)
      return;
    foreach (var handleId in this._files.Keys) {
      if (!this._files.TryRemove(handleId, out var opened))
        continue;
      try { opened.File.Dispose(); } finally { this._inodes.Unpin(opened.Inode); }
    }
    foreach (var handleId in this._directories.Keys) {
      if (!this._directories.TryRemove(handleId, out var opened))
        continue;
      foreach (var inode in opened.PinnedInodes)
        this._inodes.Unpin(inode);
    }
  }

  internal ulong LookupCount(ulong inode) => this._inodes.LookupCount(inode);
  internal int TrackedInodeCount => this._inodes.TrackedInodeCount;

  private bool Has(FilesystemDriverCapabilities capability) => this.Profile.Capabilities.HasFlag(capability);

  private ulong NextHandleId() {
    var next = Interlocked.Increment(ref this._nextHandle);
    if (next <= 0)
      throw new InvalidOperationException("FUSE handle id space was exhausted.");
    return checked((ulong)next);
  }

  private static int MapException(Exception exception)
    => exception switch {
      FileNotFoundException or KeyNotFoundException => FuseErrno.NoEntry,
      DirectoryNotFoundException => FuseErrno.NoEntry,
      UnauthorizedAccessException => FuseErrno.AccessDenied,
      ObjectDisposedException => FuseErrno.BadFileDescriptor,
      ArgumentException or ArgumentOutOfRangeException => FuseErrno.InvalidArgument,
      NotSupportedException => FuseErrno.NotSupported,
      IOException => FuseErrno.Io,
      _ => FuseErrno.Io,
    };

  private sealed record FuseOpenFile(ulong Inode, IFilesystemFileHandle File, FileAccess Access, bool Append);
  private sealed record FuseOpenDirectory(ulong Inode, IReadOnlyList<FuseDirectoryEntrySnapshot> Entries, IReadOnlyList<ulong> PinnedInodes);
}

internal sealed class FuseInodeTable {
  private readonly object _sync = new();
  private readonly Dictionary<FilesystemNodeId, ulong> _nodeToInode = [];
  private readonly Dictionary<ulong, InodeState> _inodeToState = [];
  private ulong _nextInode = FuseFilesystemOperations.RootInode + 1;

  public FuseInodeTable(FilesystemNodeId rootNodeId) {
    this._nodeToInode.Add(rootNodeId, FuseFilesystemOperations.RootInode);
    this._inodeToState.Add(FuseFilesystemOperations.RootInode, new(rootNodeId, FuseFilesystemOperations.RootInode, rootNodeId, lookupCount: 1));
  }

  public int TrackedInodeCount { get { lock (this._sync) return this._inodeToState.Count; } }

  public ulong RegisterLookup(FilesystemNodeId nodeId, ulong parentInode) {
    lock (this._sync) {
      var inode = this.GetOrAddCore(nodeId, parentInode);
      this._inodeToState[inode].LookupCount = checked(this._inodeToState[inode].LookupCount + 1);
      return inode;
    }
  }

  public ulong GetOrAdd(FilesystemNodeId nodeId, ulong parentInode) { lock (this._sync) return this.GetOrAddCore(nodeId, parentInode); }

  public bool TryGetNode(ulong inode, out FilesystemNodeId nodeId) {
    lock (this._sync) {
      if (this._inodeToState.TryGetValue(inode, out var state)) { nodeId = state.NodeId; return true; }
    }
    nodeId = default;
    return false;
  }

  public bool TryGetParent(ulong inode, out ulong parentInode, out FilesystemNodeId parentNodeId) {
    lock (this._sync) {
      if (this._inodeToState.TryGetValue(inode, out var state)) {
        parentInode = state.ParentInode;
        parentNodeId = state.ParentNodeId;
        return true;
      }
    }
    parentInode = default;
    parentNodeId = default;
    return false;
  }

  public void UpdateParent(FilesystemNodeId nodeId, ulong parentInode) {
    lock (this._sync) {
      if (!this._nodeToInode.TryGetValue(nodeId, out var inode) || !this._inodeToState.TryGetValue(parentInode, out var parentState))
        return;
      var state = this._inodeToState[inode];
      state.ParentInode = parentInode;
      state.ParentNodeId = parentState.NodeId;
    }
  }

  public void Pin(ulong inode) {
    lock (this._sync) {
      if (!this._inodeToState.TryGetValue(inode, out var state))
        throw new KeyNotFoundException($"Unknown FUSE inode '{inode}'.");
      state.PinCount = checked(state.PinCount + 1);
    }
  }

  public void Unpin(ulong inode) {
    lock (this._sync) {
      if (!this._inodeToState.TryGetValue(inode, out var state)) return;
      if (state.PinCount == 0) throw new InvalidOperationException($"FUSE inode '{inode}' has no pin to release.");
      --state.PinCount;
      this.TryReclaimCore(inode, state);
    }
  }

  public void Forget(ulong inode, ulong count) {
    if (inode == FuseFilesystemOperations.RootInode || count == 0) return;
    lock (this._sync) {
      if (!this._inodeToState.TryGetValue(inode, out var state)) return;
      state.LookupCount = count >= state.LookupCount ? 0 : state.LookupCount - count;
      this.TryReclaimCore(inode, state);
    }
  }

  public ulong LookupCount(ulong inode) { lock (this._sync) return this._inodeToState.TryGetValue(inode, out var state) ? state.LookupCount : 0; }

  private ulong GetOrAddCore(FilesystemNodeId nodeId, ulong parentInode) {
    if (this._nodeToInode.TryGetValue(nodeId, out var existing))
      return existing;
    if (!this._inodeToState.TryGetValue(parentInode, out var parentState))
      throw new KeyNotFoundException($"Unknown parent FUSE inode '{parentInode}'.");
    var inode = this._nextInode++;
    if (inode <= FuseFilesystemOperations.RootInode)
      throw new InvalidOperationException("FUSE inode id space was exhausted.");
    this._nodeToInode.Add(nodeId, inode);
    this._inodeToState.Add(inode, new(nodeId, parentInode, parentState.NodeId));
    return inode;
  }

  private void TryReclaimCore(ulong inode, InodeState state) {
    if (inode == FuseFilesystemOperations.RootInode || state.LookupCount != 0 || state.PinCount != 0)
      return;
    this._inodeToState.Remove(inode);
    if (this._nodeToInode.TryGetValue(state.NodeId, out var mappedInode) && mappedInode == inode)
      this._nodeToInode.Remove(state.NodeId);
  }

  private sealed class InodeState(FilesystemNodeId nodeId, ulong parentInode, FilesystemNodeId parentNodeId, ulong lookupCount = 0) {
    public FilesystemNodeId NodeId { get; } = nodeId;
    public ulong ParentInode { get; set; } = parentInode;
    public FilesystemNodeId ParentNodeId { get; set; } = parentNodeId;
    public ulong LookupCount { get; set; } = lookupCount;
    public ulong PinCount { get; set; }
  }
}
