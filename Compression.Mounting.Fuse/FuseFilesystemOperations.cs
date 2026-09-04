using System.Collections.Concurrent;
using Compression.Registry;

namespace Compression.Mounting.Fuse;

internal static class FuseErrno {
  public const int Success = 0;
  public const int NoEntry = 2;
  public const int Io = 5;
  public const int BadFileDescriptor = 9;
  public const int AccessDenied = 13;
  public const int NotDirectory = 20;
  public const int IsDirectory = 21;
  public const int InvalidArgument = 22;
  public const int ReadOnlyFileSystem = 30;
  public const int NotImplemented = 38;
  public const int NotSupported = 95;
}

internal readonly record struct FuseNodeSnapshot(
  ulong Inode,
  FilesystemNodeInfo Node
);

internal readonly record struct FuseDirectoryEntrySnapshot(
  string Name,
  ulong Inode,
  FilesystemNodeInfo Node,
  long NextOffset
);

/// <summary>
/// Backend-local stable inode/handle state. Pathnames are used only for lookup;
/// once the kernel has an inode or file handle, all later I/O is path-independent.
/// </summary>
internal sealed class FuseFilesystemOperations(IFilesystemSession filesystem) : IDisposable {
  public const ulong RootInode = 1;

  private readonly IFilesystemSession _filesystem = filesystem ?? throw new ArgumentNullException(nameof(filesystem));
  private readonly FuseInodeTable _inodes = new(filesystem.RootNodeId);
  private readonly ConcurrentDictionary<ulong, IFilesystemFileHandle> _files = [];
  private readonly ConcurrentDictionary<ulong, IReadOnlyList<FuseDirectoryEntrySnapshot>> _directories = [];
  private long _nextHandle;
  private int _disposed;

  public FilesystemDriverProfile Profile => this._filesystem.Profile;

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

      var inode = this._inodes.RegisterLookup(nodeId.Value);
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

  public int ReadSymbolicLink(ulong inode, out string target) {
    target = string.Empty;
    if (Volatile.Read(ref this._disposed) != 0)
      return FuseErrno.Io;

    try {
      if (!this._inodes.TryGetNode(inode, out var nodeId))
        return FuseErrno.NoEntry;
      if (this._filesystem.Stat(nodeId).Kind != FilesystemNodeKind.SymbolicLink)
        return FuseErrno.InvalidArgument;
      if (!this.Profile.Capabilities.HasFlag(FilesystemDriverCapabilities.SymbolicLinks))
        return FuseErrno.NotSupported;

      target = this._filesystem.ReadSymbolicLink(nodeId);
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public void Forget(ulong inode, ulong lookupCount)
    => this._inodes.Forget(inode, lookupCount);

  public int OpenFile(ulong inode, int flags, out ulong handleId) {
    handleId = 0;
    if (Volatile.Read(ref this._disposed) != 0)
      return FuseErrno.Io;

    const int openAccessMode = 0x3;
    const int openWriteOnly = 0x1;
    const int openReadWrite = 0x2;
    const int openTruncate = 0x200;

    var accessMode = flags & openAccessMode;
    if (accessMode is openWriteOnly or openReadWrite || (flags & openTruncate) != 0)
      return FuseErrno.ReadOnlyFileSystem;

    try {
      if (!this._inodes.TryGetNode(inode, out var nodeId))
        return FuseErrno.NoEntry;

      var node = this._filesystem.Stat(nodeId);
      if (node.Kind == FilesystemNodeKind.Directory)
        return FuseErrno.IsDirectory;
      if (node.Kind != FilesystemNodeKind.RegularFile)
        return FuseErrno.NotSupported;

      var handle = this._filesystem.OpenFile(nodeId, FileAccess.Read);
      handleId = this.NextHandleId();
      if (!this._files.TryAdd(handleId, handle)) {
        handle.Dispose();
        return FuseErrno.Io;
      }

      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int ReadFile(ulong handleId, long offset, Span<byte> destination, out int bytesRead) {
    bytesRead = 0;
    if (offset < 0)
      return FuseErrno.InvalidArgument;
    if (!this._files.TryGetValue(handleId, out var handle))
      return FuseErrno.BadFileDescriptor;

    try {
      bytesRead = handle.Read(offset, destination);
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int FlushFile(ulong handleId) {
    if (!this._files.TryGetValue(handleId, out var handle))
      return FuseErrno.BadFileDescriptor;

    if (!this.Profile.Capabilities.HasFlag(FilesystemDriverCapabilities.Flush))
      return FuseErrno.Success;

    try {
      handle.Flush();
      this._filesystem.Flush();
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int ReleaseFile(ulong handleId) {
    if (!this._files.TryRemove(handleId, out var handle))
      return FuseErrno.BadFileDescriptor;

    try {
      handle.Dispose();
      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int OpenDirectory(ulong inode, out ulong handleId) {
    handleId = 0;
    if (Volatile.Read(ref this._disposed) != 0)
      return FuseErrno.Io;

    try {
      if (!this._inodes.TryGetNode(inode, out var nodeId))
        return FuseErrno.NoEntry;
      if (this._filesystem.Stat(nodeId).Kind != FilesystemNodeKind.Directory)
        return FuseErrno.NotDirectory;

      var entries = this._filesystem.Enumerate(nodeId);
      var snapshot = new FuseDirectoryEntrySnapshot[entries.Count];
      for (var i = 0; i < entries.Count; ++i) {
        var entry = entries[i];
        var childInode = this._inodes.GetOrAdd(entry.NodeId);
        snapshot[i] = new(entry.Name, childInode, this._filesystem.Stat(entry.NodeId), i + 1L);
      }

      handleId = this.NextHandleId();
      if (!this._directories.TryAdd(handleId, snapshot))
        return FuseErrno.Io;

      return FuseErrno.Success;
    } catch (Exception ex) {
      return MapException(ex);
    }
  }

  public int ReadDirectory(
    ulong handleId,
    long offset,
    out IReadOnlyList<FuseDirectoryEntrySnapshot> entries
  ) {
    entries = [];
    if (offset < 0)
      return FuseErrno.InvalidArgument;
    if (!this._directories.TryGetValue(handleId, out var snapshot))
      return FuseErrno.BadFileDescriptor;
    if (offset >= snapshot.Count)
      return FuseErrno.Success;

    entries = snapshot.Skip(checked((int)offset)).ToArray();
    return FuseErrno.Success;
  }

  public int ReleaseDirectory(ulong handleId)
    => this._directories.TryRemove(handleId, out _)
      ? FuseErrno.Success
      : FuseErrno.BadFileDescriptor;

  public int FlushFilesystem() {
    if (!this.Profile.Capabilities.HasFlag(FilesystemDriverCapabilities.Flush))
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

    foreach (var pair in this._files)
      pair.Value.Dispose();

    this._files.Clear();
    this._directories.Clear();
  }

  internal ulong LookupCount(ulong inode)
    => this._inodes.LookupCount(inode);

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
}

internal sealed class FuseInodeTable {
  private readonly object _sync = new();
  private readonly Dictionary<FilesystemNodeId, ulong> _nodeToInode = [];
  private readonly Dictionary<ulong, InodeState> _inodeToState = [];
  private ulong _nextInode = FuseFilesystemOperations.RootInode + 1;

  public FuseInodeTable(FilesystemNodeId rootNodeId) {
    this._nodeToInode.Add(rootNodeId, FuseFilesystemOperations.RootInode);
    this._inodeToState.Add(FuseFilesystemOperations.RootInode, new(rootNodeId, 1));
  }

  public ulong RegisterLookup(FilesystemNodeId nodeId) {
    lock (this._sync) {
      var inode = this.GetOrAddCore(nodeId);
      var state = this._inodeToState[inode];
      state.LookupCount = checked(state.LookupCount + 1);
      return inode;
    }
  }

  public ulong GetOrAdd(FilesystemNodeId nodeId) {
    lock (this._sync)
      return this.GetOrAddCore(nodeId);
  }

  public bool TryGetNode(ulong inode, out FilesystemNodeId nodeId) {
    lock (this._sync) {
      if (this._inodeToState.TryGetValue(inode, out var state)) {
        nodeId = state.NodeId;
        return true;
      }
    }

    nodeId = default;
    return false;
  }

  public void Forget(ulong inode, ulong count) {
    if (inode == FuseFilesystemOperations.RootInode || count == 0)
      return;

    lock (this._sync) {
      if (!this._inodeToState.TryGetValue(inode, out var state))
        return;
      state.LookupCount = count >= state.LookupCount ? 0 : state.LookupCount - count;
    }
  }

  public ulong LookupCount(ulong inode) {
    lock (this._sync)
      return this._inodeToState.TryGetValue(inode, out var state) ? state.LookupCount : 0;
  }

  private ulong GetOrAddCore(FilesystemNodeId nodeId) {
    if (this._nodeToInode.TryGetValue(nodeId, out var existing))
      return existing;

    var inode = this._nextInode++;
    if (inode <= FuseFilesystemOperations.RootInode)
      throw new InvalidOperationException("FUSE inode id space was exhausted.");

    this._nodeToInode.Add(nodeId, inode);
    this._inodeToState.Add(inode, new(nodeId, 0));
    return inode;
  }

  private sealed class InodeState(FilesystemNodeId nodeId, ulong lookupCount) {
    public FilesystemNodeId NodeId { get; } = nodeId;
    public ulong LookupCount { get; set; } = lookupCount;
  }
}
