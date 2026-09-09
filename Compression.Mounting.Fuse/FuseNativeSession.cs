using System.Runtime.InteropServices;

namespace Compression.Mounting.Fuse;

internal sealed class FuseNativeSession : IDisposable {
  private readonly FuseNativeCallbacks _callbacks;
  private FuseArgs _args;
  private IntPtr _session;
  private Task? _loopTask;
  private string? _mountPoint;
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
      result.Initialize(mountPoint, operations.IsReadOnly);
      return result;
    } catch {
      result.Dispose();
      throw;
    }
  }

  public ValueTask UnmountAsync(CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposed) != 0, this);
    var session = this._session;
    return session == IntPtr.Zero
      ? ValueTask.CompletedTask
      : this.StopSessionAsync(session, cancellationToken);
  }

  public void Dispose() {
    if (Interlocked.Exchange(ref this._disposed, 1) != 0)
      return;
    var session = Interlocked.Exchange(ref this._session, IntPtr.Zero);
    try {
      if (session != IntPtr.Zero) {
        try {
          this.StopSessionAsync(session, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        } catch {
          // Destruction must still release native state if the loop itself failed.
          // StopSessionAsync always unmounts before surfacing that failure.
        } finally {
          LibFuseNative.fuse_session_destroy(session);
        }
      }
    } finally {
      try { LibFuseNative.fuse_opt_free_args(ref this._args); }
      finally { this._callbacks.Dispose(); }
    }
  }

  private void Initialize(string mountPoint, bool readOnly) {
    AddArgument("compressionworkbench");
    if (readOnly)
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

    Volatile.Write(ref this._mountPoint, mountPoint);
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

  private async ValueTask StopSessionAsync(IntPtr session, CancellationToken cancellationToken) {
    Task? wakeTask = null;
    try {
      if (this._loopTask is { } loopTask) {
        if (!loopTask.IsCompleted) {
          LibFuseNative.fuse_session_exit(session);
          wakeTask = Task.Run(this.WakeSingleThreadedLoop);
        }

        await loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
      }
    } finally {
      if (Interlocked.Exchange(ref this._mounted, 0) != 0)
        LibFuseNative.fuse_session_unmount(session);
    }

    if (wakeTask is not null)
      await wakeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
  }

  private void WakeSingleThreadedLoop() {
    var mountPoint = Volatile.Read(ref this._mountPoint);
    if (mountPoint is null)
      return;

    // The legacy fuse_session_loop ABI blocks in read(/dev/fuse) and an external
    // fuse_session_exit only flips the exit flag. A unique child lookup cannot be
    // satisfied from the positive/negative dentry cache, so it reliably wakes the
    // receive loop; the loop handles that one request and then observes the flag.
    var wakePath = Path.Combine(mountPoint, $".cwb-fuse-exit-{Guid.NewGuid():N}");
    _ = File.Exists(wakePath);
  }

  private void AddArgument(string argument) {
    var result = LibFuseNative.fuse_opt_add_arg(ref this._args, argument);
    if (result != 0)
      throw new IOException($"libfuse3 rejected mount argument '{argument}'.");
  }
}

internal sealed class FuseNativeCallbacks : IDisposable {
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LookupCallback(IntPtr request, ulong parent, IntPtr name);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ForgetCallback(IntPtr request, ulong inode, ulong lookupCount);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GetAttrCallback(IntPtr request, ulong inode, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetAttrCallback(IntPtr request, ulong inode, IntPtr attributes, int toSet, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadLinkCallback(IntPtr request, ulong inode);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MakeNodeCallback(IntPtr request, ulong parent, IntPtr name, uint mode, ulong device);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MakeDirectoryCallback(IntPtr request, ulong parent, IntPtr name, uint mode);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NameMutationCallback(IntPtr request, ulong parent, IntPtr name);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SymbolicLinkCallback(IntPtr request, IntPtr target, ulong parent, IntPtr name);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RenameCallback(IntPtr request, ulong parent, IntPtr name, ulong newParent, IntPtr newName, uint flags);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LinkCallback(IntPtr request, ulong inode, ulong newParent, IntPtr newName);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void OpenCallback(IntPtr request, ulong inode, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadCallback(IntPtr request, ulong inode, nuint size, long offset, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WriteCallback(IntPtr request, ulong inode, IntPtr buffer, nuint size, long offset, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void HandleCallback(IntPtr request, ulong inode, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FsyncCallback(IntPtr request, ulong inode, int dataOnly, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadDirectoryCallback(IntPtr request, ulong inode, nuint size, long offset, IntPtr fileInfo);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StatFsCallback(IntPtr request, ulong inode);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AccessCallback(IntPtr request, ulong inode, int mask);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CreateCallback(IntPtr request, ulong parent, IntPtr name, uint mode, IntPtr fileInfo);

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
    SetAttrCallback setAttr = SetAttr;
    ReadLinkCallback readLink = ReadLink;
    MakeNodeCallback makeNode = MakeNode;
    MakeDirectoryCallback makeDirectory = MakeDirectory;
    NameMutationCallback unlink = Unlink;
    NameMutationCallback removeDirectory = RemoveDirectory;
    SymbolicLinkCallback symbolicLink = SymbolicLink;
    RenameCallback rename = Rename;
    LinkCallback link = Link;
    OpenCallback open = Open;
    ReadCallback read = Read;
    WriteCallback write = Write;
    HandleCallback flush = Flush;
    HandleCallback release = Release;
    FsyncCallback fsync = Fsync;
    OpenCallback openDirectory = OpenDirectory;
    ReadDirectoryCallback readDirectory = ReadDirectory;
    HandleCallback releaseDirectory = ReleaseDirectory;
    FsyncCallback fsyncDirectory = FsyncDirectory;
    StatFsCallback statFs = StatFs;
    AccessCallback access = Access;
    CreateCallback create = Create;

    this._delegates = [
      lookup, forget, getAttr, setAttr, readLink, makeNode, makeDirectory,
      unlink, removeDirectory, symbolicLink, rename, link, open, read, write,
      flush, release, fsync, openDirectory, readDirectory, releaseDirectory,
      fsyncDirectory, statFs, access, create,
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
      Create = Pointer(create),
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

  private static IntPtr Pointer(Delegate callback) => Marshal.GetFunctionPointerForDelegate(callback);

  private static FuseNativeCallbacks State(IntPtr request) {
    var userData = LibFuseNative.fuse_req_userdata(request);
    var handle = GCHandle.FromIntPtr(userData);
    return (FuseNativeCallbacks)(handle.Target ?? throw new ObjectDisposedException(nameof(FuseNativeCallbacks)));
  }

  private static void Lookup(IntPtr request, ulong parent, IntPtr namePointer) {
    var state = State(request);
    var error = state._operations.Lookup(parent, Name(namePointer), out var snapshot);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    var entry = FuseStatFactory.CreateEntry(snapshot, !state._operations.IsReadOnly);
    LibFuseNative.fuse_reply_entry(request, ref entry);
  }

  private static void Forget(IntPtr request, ulong inode, ulong lookupCount) {
    State(request)._operations.Forget(inode, lookupCount);
    LibFuseNative.fuse_reply_none(request);
  }

  private static void GetAttr(IntPtr request, ulong inode, IntPtr fileInfo) {
    var state = State(request);
    var error = state._operations.GetAttributes(inode, out var snapshot);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    var attributes = FuseStatFactory.Create(snapshot.Inode, snapshot.Node, !state._operations.IsReadOnly);
    LibFuseNative.fuse_reply_attr(request, ref attributes, 1);
  }

  private static void SetAttr(IntPtr request, ulong inode, IntPtr attributesPointer, int toSet, IntPtr fileInfoPointer) {
    if (attributesPointer == IntPtr.Zero) { ReplyError(request, FuseErrno.InvalidArgument); return; }
    var state = State(request);
    var attributes = Marshal.PtrToStructure<LinuxStat>(attributesPointer);
    ulong? handleId = fileInfoPointer == IntPtr.Zero ? null : Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer).FileHandle;
    var error = state._operations.SetAttributes(inode, toSet, attributes.Size, handleId, out var snapshot);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    var result = FuseStatFactory.Create(snapshot.Inode, snapshot.Node, !state._operations.IsReadOnly);
    LibFuseNative.fuse_reply_attr(request, ref result, 1);
  }

  private static void ReadLink(IntPtr request, ulong inode) {
    var error = State(request)._operations.ReadSymbolicLink(inode, out var target);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    LibFuseNative.fuse_reply_readlink(request, target);
  }

  private static void MakeNode(IntPtr request, ulong parent, IntPtr namePointer, uint mode, ulong device) {
    var state = State(request);
    var error = state._operations.MakeNode(parent, Name(namePointer), mode, out var snapshot);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    var entry = FuseStatFactory.CreateEntry(snapshot, !state._operations.IsReadOnly);
    LibFuseNative.fuse_reply_entry(request, ref entry);
  }

  private static void MakeDirectory(IntPtr request, ulong parent, IntPtr namePointer, uint mode) {
    var state = State(request);
    var error = state._operations.CreateDirectory(parent, Name(namePointer), out var snapshot);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    var entry = FuseStatFactory.CreateEntry(snapshot, !state._operations.IsReadOnly);
    LibFuseNative.fuse_reply_entry(request, ref entry);
  }

  private static void Unlink(IntPtr request, ulong parent, IntPtr namePointer)
    => ReplyError(request, State(request)._operations.Unlink(parent, Name(namePointer)));

  private static void RemoveDirectory(IntPtr request, ulong parent, IntPtr namePointer)
    => ReplyError(request, State(request)._operations.RemoveDirectory(parent, Name(namePointer)));

  private static void SymbolicLink(IntPtr request, IntPtr targetPointer, ulong parent, IntPtr namePointer) {
    var state = State(request);
    var error = state._operations.CreateSymbolicLink(parent, Name(namePointer), Name(targetPointer), out var snapshot);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    var entry = FuseStatFactory.CreateEntry(snapshot, !state._operations.IsReadOnly);
    LibFuseNative.fuse_reply_entry(request, ref entry);
  }

  private static void Rename(IntPtr request, ulong parent, IntPtr namePointer, ulong newParent, IntPtr newNamePointer, uint flags)
    => ReplyError(request, State(request)._operations.Rename(parent, Name(namePointer), newParent, Name(newNamePointer), flags));

  private static void Link(IntPtr request, ulong inode, ulong newParent, IntPtr newNamePointer) {
    var state = State(request);
    var error = state._operations.CreateHardLink(inode, newParent, Name(newNamePointer), out var snapshot);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    var entry = FuseStatFactory.CreateEntry(snapshot, !state._operations.IsReadOnly);
    LibFuseNative.fuse_reply_entry(request, ref entry);
  }

  private static void Open(IntPtr request, ulong inode, IntPtr fileInfoPointer) {
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    var error = State(request)._operations.OpenFile(inode, fileInfo.Flags, out var handleId);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    fileInfo.FileHandle = handleId;
    Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
    LibFuseNative.fuse_reply_open(request, ref fileInfo);
  }

  private static void Create(IntPtr request, ulong parent, IntPtr namePointer, uint mode, IntPtr fileInfoPointer) {
    var state = State(request);
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    var error = state._operations.CreateFile(parent, Name(namePointer), fileInfo.Flags, out var snapshot, out var handleId);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    fileInfo.FileHandle = handleId;
    Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
    var entry = FuseStatFactory.CreateEntry(snapshot, !state._operations.IsReadOnly);
    LibFuseNative.fuse_reply_create(request, ref entry, ref fileInfo);
  }

  private static void Read(IntPtr request, ulong inode, nuint size, long offset, IntPtr fileInfoPointer) {
    if (size > int.MaxValue) { ReplyError(request, FuseErrno.InvalidArgument); return; }
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    var buffer = new byte[(int)size];
    var error = State(request)._operations.ReadFile(fileInfo.FileHandle, offset, buffer, out var bytesRead);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    ReplyBuffer(request, buffer, bytesRead);
  }

  private static void Write(IntPtr request, ulong inode, IntPtr bufferPointer, nuint size, long offset, IntPtr fileInfoPointer) {
    if (size > int.MaxValue) { ReplyError(request, FuseErrno.InvalidArgument); return; }
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    var buffer = new byte[(int)size];
    if (buffer.Length != 0)
      Marshal.Copy(bufferPointer, buffer, 0, buffer.Length);
    var error = State(request)._operations.WriteFile(fileInfo.FileHandle, offset, buffer, out var bytesWritten);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    LibFuseNative.fuse_reply_write(request, checked((nuint)bytesWritten));
  }

  private static void Flush(IntPtr request, ulong inode, IntPtr fileInfoPointer) {
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    ReplyError(request, State(request)._operations.FlushFile(fileInfo.FileHandle));
  }

  private static void Release(IntPtr request, ulong inode, IntPtr fileInfoPointer) {
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    ReplyError(request, State(request)._operations.ReleaseFile(fileInfo.FileHandle));
  }

  private static void Fsync(IntPtr request, ulong inode, int dataOnly, IntPtr fileInfoPointer) => Flush(request, inode, fileInfoPointer);

  private static void OpenDirectory(IntPtr request, ulong inode, IntPtr fileInfoPointer) {
    var error = State(request)._operations.OpenDirectory(inode, out var handleId);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    fileInfo.FileHandle = handleId;
    Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
    LibFuseNative.fuse_reply_open(request, ref fileInfo);
  }

  private static void ReadDirectory(IntPtr request, ulong inode, nuint size, long offset, IntPtr fileInfoPointer) {
    if (size > int.MaxValue) { ReplyError(request, FuseErrno.InvalidArgument); return; }
    var state = State(request);
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    var error = state._operations.ReadDirectory(fileInfo.FileHandle, offset, out var entries);
    if (error != FuseErrno.Success) { ReplyError(request, error); return; }

    var capacity = (int)size;
    if (capacity == 0 || entries.Count == 0) { LibFuseNative.fuse_reply_buf(request, IntPtr.Zero, 0); return; }
    var buffer = Marshal.AllocHGlobal(capacity);
    try {
      var used = 0;
      foreach (var entry in entries) {
        var attributes = FuseStatFactory.Create(entry.Inode, entry.Node, !state._operations.IsReadOnly);
        var remaining = capacity - used;
        var entrySize = LibFuseNative.fuse_add_direntry(
          request,
          IntPtr.Add(buffer, used),
          checked((nuint)remaining),
          entry.Name,
          ref attributes,
          entry.NextOffset
        );
        if (entrySize > checked((nuint)remaining)) break;
        used = checked(used + (int)entrySize);
      }
      LibFuseNative.fuse_reply_buf(request, buffer, checked((nuint)used));
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  private static void ReleaseDirectory(IntPtr request, ulong inode, IntPtr fileInfoPointer) {
    var fileInfo = Marshal.PtrToStructure<FuseFileInfo>(fileInfoPointer);
    ReplyError(request, State(request)._operations.ReleaseDirectory(fileInfo.FileHandle));
  }

  private static void FsyncDirectory(IntPtr request, ulong inode, int dataOnly, IntPtr fileInfo)
    => ReplyError(request, State(request)._operations.FlushFilesystem());

  private static void StatFs(IntPtr request, ulong inode) => ReplyError(request, FuseErrno.NotImplemented);
  private static void Access(IntPtr request, ulong inode, int mask) => ReplyError(request, State(request)._operations.Access(inode, mask));

  private static string Name(IntPtr pointer) => Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
  private static void ReplyError(IntPtr request, int error) => LibFuseNative.fuse_reply_err(request, error);

  private static void ReplyBuffer(IntPtr request, byte[] buffer, int length) {
    if (length <= 0) { LibFuseNative.fuse_reply_buf(request, IntPtr.Zero, 0); return; }
    var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
    try { LibFuseNative.fuse_reply_buf(request, pinned.AddrOfPinnedObject(), checked((nuint)length)); }
    finally { pinned.Free(); }
  }
}
