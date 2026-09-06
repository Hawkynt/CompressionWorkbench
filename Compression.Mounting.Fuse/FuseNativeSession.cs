using System.Runtime.InteropServices;

namespace Compression.Mounting.Fuse;

internal sealed class FuseNativeSession : IDisposable {
  private readonly FuseNativeCallbacks _callbacks;
  private FuseArgs _args;
  private IntPtr _session;
  private Task? _loopTask;
  private int _mounted;
  private int _disposed;

  private FuseNativeSession(FuseFilesystemOperations operations)
    => this._callbacks = new(operations ?? throw new ArgumentNullException(nameof(operations)));

  public bool IsMounted
    => Volatile.Read(ref this._disposed) == 0
       && Volatile.Read(ref this._mounted) != 0
       && this._session != IntPtr.Zero
       && this._loopTask is { IsCompleted: false };

  public static FuseNativeSession Mount(FuseFilesystemOperations operations, string target) {
    ArgumentNullException.ThrowIfNull(operations);
    ArgumentException.ThrowIfNullOrWhiteSpace(target);

    var mountPoint = Path.GetFullPath(target);
    if (!Directory.Exists(mountPoint))
      throw new DirectoryNotFoundException($"FUSE mountpoint '{mountPoint}' does not exist.");

    var result = new FuseNativeSession(operations);
    try {
      result.Initialize(mountPoint);
      return result;
    } catch {
      result.Dispose();
      throw;
    }
  }

  public async ValueTask UnmountAsync(CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposed) != 0, this);

    var session = this._session;
    if (session == IntPtr.Zero)
      return;

    LibFuseNative.fuse_session_exit(session);
    if (Interlocked.Exchange(ref this._mounted, 0) != 0)
      LibFuseNative.fuse_session_unmount(session);

    if (this._loopTask is { } loopTask)
      await loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
  }

  public void Dispose() {
    if (Interlocked.Exchange(ref this._disposed, 1) != 0)
      return;

    var session = Interlocked.Exchange(ref this._session, IntPtr.Zero);
    try {
      if (session != IntPtr.Zero) {
        LibFuseNative.fuse_session_exit(session);
        if (Interlocked.Exchange(ref this._mounted, 0) != 0)
          LibFuseNative.fuse_session_unmount(session);

        try {
          this._loopTask?.Wait(TimeSpan.FromSeconds(5));
        } catch (AggregateException) {
          // Session teardown still has to release native state even if the loop
          // already failed; the mount session reports loop failures separately.
        }

        LibFuseNative.fuse_session_destroy(session);
      }
    } finally {
      try {
        LibFuseNative.fuse_opt_free_args(ref this._args);
      } finally {
        this._callbacks.Dispose();
      }
    }
  }

  private void Initialize(string mountPoint) {
    AddArgument("compressionworkbench");
    AddArgument("-oro");
    AddArgument("-odefault_permissions");
    AddArgument("-ofsname=CompressionWorkbench");
    AddArgument("-osubtype=cwbfs");

    var nativeOperations = this._callbacks.NativeOperations;
    this._session = LibFuseNative.fuse_session_new(
      ref this._args,
      ref nativeOperations,
      checked((nuint)Marshal.SizeOf<FuseLowLevelOps>()),
      this._callbacks.UserData
    );

    if (this._session == IntPtr.Zero)
      throw new IOException("libfuse3 refused to create the low-level filesystem session.");

    var mountResult = LibFuseNative.fuse_session_mount(this._session, mountPoint);
    if (mountResult != 0)
      throw new IOException($"libfuse3 failed to mount '{mountPoint}' (error {mountResult}).");

    Volatile.Write(ref this._mounted, 1);
    var session = this._session;
    this._loopTask = Task.Factory.StartNew(
      () => {
        var loopResult = LibFuseNative.fuse_session_loop(session);
        if (loopResult != 0)
          throw new IOException($"libfuse3 session loop terminated with error {loopResult}.");
      },
      CancellationToken.None,
      TaskCreationOptions.LongRunning,
      TaskScheduler.Default
    );
  }

  private void AddArgument(string argument) {
    var result = LibFuseNative.fuse_opt_add_arg(ref this._args, argument);
    if (result != 0)
      throw new IOException($"libfuse3 rejected mount argument '{argument}'.");
  }
}

internal sealed class FuseNativeCallbacks : IDisposable {
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void LookupCallback(IntPtr request, ulong parent, IntPtr name);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void ForgetCallback(IntPtr request, ulong inode, ulong lookupCount);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void GetAttrCallback(IntPtr request, ulong inode, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void SetAttrCallback(IntPtr request, ulong inode, IntPtr attributes, int toSet, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void ReadLinkCallback(IntPtr request, ulong inode);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void MakeNodeCallback(IntPtr request, ulong parent, IntPtr name, uint mode, ulong device);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void MakeDirectoryCallback(IntPtr request, ulong parent, IntPtr name, uint mode);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void NameMutationCallback(IntPtr request, ulong parent, IntPtr name);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void SymbolicLinkCallback(IntPtr request, IntPtr target, ulong parent, IntPtr name);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void RenameCallback(IntPtr request, ulong parent, IntPtr name, ulong newParent, IntPtr newName, uint flags);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void LinkCallback(IntPtr request, ulong inode, ulong newParent, IntPtr newName);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void OpenCallback(IntPtr request, ulong inode, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void ReadCallback(IntPtr request, ulong inode, nuint size, long offset, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void WriteCallback(IntPtr request, ulong inode, IntPtr buffer, nuint size, long offset, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void HandleCallback(IntPtr request, ulong inode, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void FsyncCallback(IntPtr request, ulong inode, int dataOnly, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void ReadDirectoryCallback(IntPtr request, ulong inode, nuint size, long offset, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void StatFsCallback(IntPtr request, ulong inode);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void AccessCallback(IntPtr request, ulong inode, int mask);

  private readonly FuseFilesystemOperations _operations;
  private readonly GCHandle _selfHandle;
  private readonly Delegate[] _delegates;
  private int _disposed;

  public FuseNativeCallbacks(FuseFilesystemOperations operations) {
    this._operations = operations ?? throw new ArgumentNullException(nameof(operations));
    this._selfHandle = GCHandle.Alloc(this);

    LookupCallback lookup = Lookup;
    ForgetCallback forget = Forget;
    GetAttrCallback getAttr = GetAttr;
    SetAttrCallback setAttr = ReadOnlySetAttr;
    ReadLinkCallback readLink = ReadLink;
    MakeNodeCallback makeNode = ReadOnlyMakeNode;
    MakeDirectoryCallback makeDirectory = ReadOnlyMakeDirectory;
    NameMutationCallback unlink = ReadOnlyNameMutation;
    NameMutationCallback removeDirectory = ReadOnlyNameMutation;
    SymbolicLinkCallback symbolicLink = ReadOnlySymbolicLink;
    RenameCallback rename = ReadOnlyRename;
    LinkCallback link = ReadOnlyLink;
    OpenCallback open = Open;
    ReadCallback read = Read;
    WriteCallback write = ReadOnlyWrite;
    HandleCallback flush = Flush;
    HandleCallback release = Release;
    FsyncCallback fsync = Fsync;
    OpenCallback openDirectory = OpenDirectory;
    ReadDirectoryCallback readDirectory = ReadDirectory;
    HandleCallback releaseDirectory = ReleaseDirectory;
    FsyncCallback fsyncDirectory = FsyncDirectory;
    StatFsCallback statFs = StatFs;
    AccessCallback access = Access;

    this._delegates = [
      lookup, forget, getAttr, setAttr, readLink, makeNode, makeDirectory,
      unlink, removeDirectory, symbolicLink, rename, link, open, read, write,
      flush, release, fsync, openDirectory, readDirectory, releaseDirectory,
      fsyncDirectory, statFs, access,
    ];

    this.NativeOperations = new() {
      Lookup = Pointer(lookup),
      Forget = Pointer(forget),
      GetAttr = Pointer(getAttr),
      SetAttr = Pointer(setAttr),
      ReadLink = Pointer(readLink),
      MakeNode = Pointer(makeNode),
      MakeDirectory = Pointer(makeDirectory),
      Unlink = Pointer(unlink),
      RemoveDirectory = Pointer(removeDirectory),
      SymbolicLink = Pointer(symbolicLink),
      Rename = Pointer(rename),
      Link = Pointer(link),
      Open = Pointer(open),
      Read = Pointer(read),
      Write = Pointer(write),
      Flush = Pointer(flush),
      Release = Pointer(release),
      Fsync = Pointer(fsync),
      OpenDirectory = Pointer(openDirectory),
      ReadDirectory = Pointer(readDirectory),
      ReleaseDirectory = Pointer(releaseDirectory),
      FsyncDirectory = Pointer(fsyncDirectory),
      StatFs = Pointer(statFs),
      Access = Pointer(access),
    };
  }

  public IntPtr UserData => GCHandle.ToIntPtr(this._selfHandle);
  public FuseLowLevelOps NativeOperations { get; }

  public void Dispose() {
    if (Interlocked.Exchange(ref this._disposed, 1) != 0)
      return;

    GC.KeepAlive(this._delegates);
    if (this._selfHandle.IsAllocated)
      this._selfHandle.Free();
  }

  private static IntPtr Pointer(Delegate callback)
    => Marshal.GetFunctionPointerForDelegate(callback);

  private static FuseNativeCallbacks State(IntPtr request) {
    var userData = LibFuseNative.fuse_req_userdata(request);
    var handle = GCHandle.FromIntPtr(userData);
    return (FuseNativeCallbacks)(handle.Target ?? throw new ObjectDisposedException(nameof(FuseNativeCallbacks)));
  }

  private static void Lookup(IntPtr request, ulong parent, IntPtr namePointer) {
    var state = State(request);
    var name = Marshal.PtrToStringUTF8(namePointer) ?? string.Empty;
    var error = state._operations.Lookup(parent, name, out var snapshot);
    if (error != FuseErrno.Success) {
      LibFuseNative.fuse_reply_err(request, error);
      return;
    }

    var entry = FuseStatFactory.CreateEntry(snapshot);
    LibFuseNative.fuse_reply_entry(request, ref entry);
  }

  private static void Forget(IntPtr request, ulong inode, ulong lookupCount) {
    State(request)._operations.Forget(inode, lookupCount);
    LibFuseNative.fuse_reply_none(request);
  }

  private static void GetAttr(IntPtr request, ulong inode, IntPtr fileInfo) {
    var error = State(request)._operations.GetAttributes(inode, out var snapshot);
    if (error != FuseErrno.Success) {
      LibFuseNative.fuse_reply_err(request, error);
      return;
    }

    var attributes = FuseStatFactory.Create(snapshot.Inode, snapshot.Node);
    LibFuseNative.fuse_reply_attr(request, ref attributes, 1);
  }

  private static void ReadOnlySetAttr(IntPtr request, ulong inode, IntPtr attributes, int toSet, IntPtr fileInfo)
    => LibFuseNative.fuse_reply_err(request, FuseErrno.ReadOnlyFileSystem);

  private static void ReadLink(IntPtr request, ulong inode) {
    var error = State(request)._operations.ReadSymbolicLink(inode, out var target);
    if (error != FuseErrno.Success) {
      LibFuseNative.fuse_reply_err(request, error);
      return;
    }

    LibFuseNative.fuse_reply_readlink(request, target);
  }

  private static void ReadOnlyMakeNode(IntPtr request, ulong parent, IntPtr name, uint mode, ulong device)
    => LibFuseNative.fuse_reply_err(request, FuseErrno.ReadOnlyFileSystem);

  private static void ReadOnlyMakeDirectory(IntPtr request, ulong parent, IntPtr name, uint mode)
    => LibFuseNative.fuse_reply_err(request, FuseErrno.ReadOnlyFileSystem);

  private static void ReadOnlyNameMutation(IntPtr request, ulong parent, IntPtr name)
    => LibFuseNative.fuse_reply_err(request, FuseErrno.ReadOnlyFileSystem);

  private static void ReadOnlySymbolicLink(IntPtr request, IntPtr target, ulong parent, IntPtr name)
    => LibFuseNative.fuse_reply_err(request, FuseErrno.ReadOnlyFileSystem);

  private static void ReadOnlyRename(IntPtr request, ulong parent, IntPtr name, ulong newParent, IntPtr newName, uint flags)
    => LibFuseNative.fuse_reply_err(request, FuseErrno.ReadOnlyFileSystem);

  private static void ReadOnlyLink(IntPtr request, ulong inode, ulong newParent, IntPtr newName)
    => LibFuseNative.fuse_reply_err(request, FuseErrno.ReadOnlyFileSystem);

  private static void Open(IntPtr request, ulong inode, IntPtr fileInfoPointer) {
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    var error = State(request)._operations.OpenFile(inode, fileInfo.Flags, out var handleId);
    if (error != FuseErrno.Success) {
      LibFuseNative.fuse_reply_err(request, error);
      return;
    }

    fileInfo.FileHandle = handleId;
    Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
    LibFuseNative.fuse_reply_open(request, ref fileInfo);
  }

  private static void Read(IntPtr request, ulong inode, nuint size, long offset, IntPtr fileInfoPointer) {
    if (size > int.MaxValue) {
      LibFuseNative.fuse_reply_err(request, FuseErrno.InvalidArgument);
      return;
    }

    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    var buffer = new byte[(int)size];
    var error = State(request)._operations.ReadFile(fileInfo.FileHandle, offset, buffer, out var bytesRead);
    if (error != FuseErrno.Success) {
      LibFuseNative.fuse_reply_err(request, error);
      return;
    }

    ReplyBuffer(request, buffer, bytesRead);
  }

  private static void ReadOnlyWrite(IntPtr request, ulong inode, IntPtr buffer, nuint size, long offset, IntPtr fileInfo)
    => LibFuseNative.fuse_reply_err(request, FuseErrno.ReadOnlyFileSystem);

  private static void Flush(IntPtr request, ulong inode, IntPtr fileInfoPointer) {
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    var error = State(request)._operations.FlushFile(fileInfo.FileHandle);
    LibFuseNative.fuse_reply_err(request, error);
  }

  private static void Release(IntPtr request, ulong inode, IntPtr fileInfoPointer) {
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    var error = State(request)._operations.ReleaseFile(fileInfo.FileHandle);
    LibFuseNative.fuse_reply_err(request, error);
  }

  private static void Fsync(IntPtr request, ulong inode, int dataOnly, IntPtr fileInfoPointer)
    => Flush(request, inode, fileInfoPointer);

  private static void OpenDirectory(IntPtr request, ulong inode, IntPtr fileInfoPointer) {
    var error = State(request)._operations.OpenDirectory(inode, out var handleId);
    if (error != FuseErrno.Success) {
      LibFuseNative.fuse_reply_err(request, error);
      return;
    }

    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    fileInfo.FileHandle = handleId;
    Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
    LibFuseNative.fuse_reply_open(request, ref fileInfo);
  }

  private static void ReadDirectory(IntPtr request, ulong inode, nuint size, long offset, IntPtr fileInfoPointer) {
    if (size > int.MaxValue) {
      LibFuseNative.fuse_reply_err(request, FuseErrno.InvalidArgument);
      return;
    }

    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    var error = State(request)._operations.ReadDirectory(fileInfo.FileHandle, offset, out var entries);
    if (error != FuseErrno.Success) {
      LibFuseNative.fuse_reply_err(request, error);
      return;
    }

    var capacity = (int)size;
    if (capacity == 0 || entries.Count == 0) {
      LibFuseNative.fuse_reply_buf(request, IntPtr.Zero, 0);
      return;
    }

    var buffer = Marshal.AllocHGlobal(capacity);
    try {
      var used = 0;
      foreach (var entry in entries) {
        var attributes = FuseStatFactory.Create(entry.Inode, entry.Node);
        var remaining = capacity - used;
        var entrySize = LibFuseNative.fuse_add_direntry(
          request,
          IntPtr.Add(buffer, used),
          checked((nuint)remaining),
          entry.Name,
          ref attributes,
          entry.NextOffset
        );

        if (entrySize > checked((nuint)remaining))
          break;

        used = checked(used + (int)entrySize);
      }

      LibFuseNative.fuse_reply_buf(request, buffer, checked((nuint)used));
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  private static void ReleaseDirectory(IntPtr request, ulong inode, IntPtr fileInfoPointer) {
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    var error = State(request)._operations.ReleaseDirectory(fileInfo.FileHandle);
    LibFuseNative.fuse_reply_err(request, error);
  }

  private static void FsyncDirectory(IntPtr request, ulong inode, int dataOnly, IntPtr fileInfo)
    => LibFuseNative.fuse_reply_err(request, State(request)._operations.FlushFilesystem());

  private static void StatFs(IntPtr request, ulong inode)
    => LibFuseNative.fuse_reply_err(request, FuseErrno.NotImplemented);

  private static void Access(IntPtr request, ulong inode, int mask)
    => LibFuseNative.fuse_reply_err(request, State(request)._operations.Access(inode, mask));

  private static void ReplyBuffer(IntPtr request, byte[] buffer, int length) {
    if (length <= 0) {
      LibFuseNative.fuse_reply_buf(request, IntPtr.Zero, 0);
      return;
    }

    var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
    try {
      LibFuseNative.fuse_reply_buf(request, pinned.AddrOfPinnedObject(), checked((nuint)length));
    } finally {
      pinned.Free();
    }
  }
}