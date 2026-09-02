using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// One mount request over an already-open CompressionWorkbench filesystem
/// session. The source archive/image/container has already been detected,
/// decoded and parsed before this request reaches an OS adapter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parser ownership invariant:</b> a mount backend is a host-VFS transport,
/// not a filesystem or disk-image parser. It must translate host requests to
/// <see cref="IFilesystemSession"/> operations only. It must never reopen the
/// source and ask the host OS to mount/attach/interpret it (for example via a
/// native ext4/NTFS/FAT mount, loop device, virtual-disk attach, or another
/// platform filesystem facility), even when that host happens to support the
/// format natively.
/// </para>
/// <para>
/// This keeps identical semantics across operating systems: ext4 on Windows,
/// Linux or macOS is always parsed by the same CompressionWorkbench ext driver;
/// likewise for NTFS, FAT and every other supported filesystem.
/// </para>
/// <para>
/// When <see cref="OwnsFilesystemSession"/> is true, ownership transfers to the
/// returned <see cref="IMountSession"/> only after <see cref="IFilesystemMountBackend.MountAsync"/>
/// completes successfully. If mounting throws or is cancelled, the caller still
/// owns and must dispose the filesystem session.
/// </para>
/// </remarks>
public sealed record FilesystemMountRequest(
  IFilesystemSession Filesystem,
  string Target,
  MountPlan Plan,
  bool OwnsFilesystemSession = false
);

/// <summary>
/// Host-facing adapter (FUSE, Dokany, WinFsp, etc.) for an already parsed
/// <see cref="IFilesystemSession"/>. Implementations must not contain or invoke
/// native filesystem parsing/mounting logic for the source format.
/// </summary>
public interface IFilesystemMountBackend {
  MountBackendProfile GetProfile();
  ValueTask<IMountSession> MountAsync(FilesystemMountRequest request, CancellationToken cancellationToken = default);
}

public interface IMountSession : IAsyncDisposable {
  string BackendId { get; }
  string Target { get; }
  MountAccessMode AccessMode { get; }
  bool IsMounted { get; }
  ValueTask FlushAsync(CancellationToken cancellationToken = default);
  ValueTask UnmountAsync(CancellationToken cancellationToken = default);
}
