using System.Runtime.InteropServices;
using Compression.Registry;

namespace Compression.Mounting.Fuse;

/// <summary>
/// Narrow libfuse3 ABI surface derived from the public low-level API headers.
/// The numeric/layout data here is interoperability data; no libfuse
/// implementation code is copied. The first qualified ABI is Linux x86-64.
/// </summary>
internal static class LibFuseNative {
  private const string Library = FuseRuntimeProbe.RuntimeLibrary;

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern int fuse_opt_add_arg(
    ref FuseArgs args,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string arg
  );

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern void fuse_opt_free_args(ref FuseArgs args);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern IntPtr fuse_session_new(
    ref FuseArgs args,
    ref FuseLowLevelOps operations,
    nuint operationSize,
    IntPtr userData
  );

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern int fuse_session_mount(
    IntPtr session,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string mountPoint
  );

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern int fuse_session_loop(IntPtr session);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern void fuse_session_exit(IntPtr session);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern int fuse_session_exited(IntPtr session);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern void fuse_session_unmount(IntPtr session);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern void fuse_session_destroy(IntPtr session);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern IntPtr fuse_req_userdata(IntPtr request);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern int fuse_reply_err(IntPtr request, int error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern void fuse_reply_none(IntPtr request);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern int fuse_reply_entry(IntPtr request, ref FuseEntryParam entry);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern int fuse_reply_attr(IntPtr request, ref LinuxStat attributes, double timeout);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern int fuse_reply_readlink(
    IntPtr request,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string target
  );

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern int fuse_reply_open(IntPtr request, ref FuseFileInfo fileInfo);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern int fuse_reply_buf(IntPtr request, IntPtr buffer, nuint size);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern nuint fuse_add_direntry(
    IntPtr request,
    IntPtr buffer,
    nuint bufferSize,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
    ref LinuxStat attributes,
    long nextOffset
  );
}

internal static class LibCNative {
  [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
  internal static extern uint geteuid();

  [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
  internal static extern uint getegid();
}

[StructLayout(LayoutKind.Sequential)]
internal struct FuseArgs {
  public int ArgumentCount;
  public IntPtr Arguments;
  public int Allocated;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FuseLowLevelOps {
  public IntPtr Init;
  public IntPtr Destroy;
  public IntPtr Lookup;
  public IntPtr Forget;
  public IntPtr GetAttr;
  public IntPtr SetAttr;
  public IntPtr ReadLink;
  public IntPtr MakeNode;
  public IntPtr MakeDirectory;
  public IntPtr Unlink;
  public IntPtr RemoveDirectory;
  public IntPtr SymbolicLink;
  public IntPtr Rename;
  public IntPtr Link;
  public IntPtr Open;
  public IntPtr Read;
  public IntPtr Write;
  public IntPtr Flush;
  public IntPtr Release;
  public IntPtr Fsync;
  public IntPtr OpenDirectory;
  public IntPtr ReadDirectory;
  public IntPtr ReleaseDirectory;
  public IntPtr FsyncDirectory;
  public IntPtr StatFs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FuseFileInfo {
  public int Flags;
  public uint BitFlags;
  public uint Padding2;
  public uint Padding3;
  public ulong FileHandle;
  public ulong LockOwner;
  public uint PollEvents;
  public int BackingId;
  public ulong CompatibilityFlags;
  public ulong Reserved0;
  public ulong Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LinuxTimespec {
  public long Seconds;
  public long Nanoseconds;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LinuxStat {
  public ulong Device;
  public ulong Inode;
  public ulong LinkCount;
  public uint Mode;
  public uint UserId;
  public uint GroupId;
  public int Padding0;
  public ulong RawDevice;
  public long Size;
  public long BlockSize;
  public long Blocks;
  public LinuxTimespec AccessTime;
  public LinuxTimespec ModificationTime;
  public LinuxTimespec ChangeTime;
  public long Reserved0;
  public long Reserved1;
  public long Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FuseEntryParam {
  public ulong Inode;
  public ulong Generation;
  public LinuxStat Attributes;
  public double AttributeTimeout;
  public double EntryTimeout;
}

internal static class FuseStatFactory {
  private const uint RegularFile = 0x8000;
  private const uint Directory = 0x4000;
  private const uint SymbolicLink = 0xA000;
  private const uint BlockDevice = 0x6000;
  private const uint CharacterDevice = 0x2000;
  private const uint Fifo = 0x1000;
  private const uint Socket = 0xC000;
  private const uint ReadOnlyFilePermissions = 0x124; // 0444
  private const uint ReadOnlyDirectoryPermissions = 0x16D; // 0555
  private const uint SymbolicLinkPermissions = 0x1FF; // 0777

  public static LinuxStat Create(ulong inode, FilesystemNodeInfo node) {
    var allocated = Math.Max(0, node.AllocatedSize);
    return new() {
      Inode = inode,
      LinkCount = Math.Max(1U, node.LinkCount),
      Mode = ToMode(node.Kind),
      UserId = LibCNative.geteuid(),
      GroupId = LibCNative.getegid(),
      Size = node.Kind == FilesystemNodeKind.RegularFile ? Math.Max(0, node.Size) : 0,
      BlockSize = 4096,
      Blocks = (allocated + 511) / 512,
      AccessTime = ToTimespec(node.Accessed),
      ModificationTime = ToTimespec(node.Modified),
      ChangeTime = ToTimespec(node.Changed ?? node.Modified),
    };
  }

  public static FuseEntryParam CreateEntry(FuseNodeSnapshot snapshot)
    => new() {
      Inode = snapshot.Inode,
      Generation = snapshot.Node.NodeId.Generation,
      Attributes = Create(snapshot.Inode, snapshot.Node),
      AttributeTimeout = 1,
      EntryTimeout = 1,
    };

  private static uint ToMode(FilesystemNodeKind kind)
    => kind switch {
      FilesystemNodeKind.RegularFile => RegularFile | ReadOnlyFilePermissions,
      FilesystemNodeKind.Directory => Directory | ReadOnlyDirectoryPermissions,
      FilesystemNodeKind.SymbolicLink => SymbolicLink | SymbolicLinkPermissions,
      FilesystemNodeKind.BlockDevice => BlockDevice | ReadOnlyFilePermissions,
      FilesystemNodeKind.CharacterDevice => CharacterDevice | ReadOnlyFilePermissions,
      FilesystemNodeKind.Fifo => Fifo | ReadOnlyFilePermissions,
      FilesystemNodeKind.Socket => Socket | ReadOnlyFilePermissions,
      _ => ReadOnlyFilePermissions,
    };

  private static LinuxTimespec ToTimespec(DateTimeOffset? value) {
    if (value is null)
      return default;

    var ticksSinceEpoch = value.Value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks;
    var seconds = Math.DivRem(ticksSinceEpoch, TimeSpan.TicksPerSecond, out var remainder);
    if (remainder < 0) {
      --seconds;
      remainder += TimeSpan.TicksPerSecond;
    }

    return new() {
      Seconds = seconds,
      Nanoseconds = remainder * 100,
    };
  }
}
