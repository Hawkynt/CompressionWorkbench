using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// One mount request over an already-open filesystem session.
/// When <see cref="OwnsFilesystemSession"/> is true, ownership transfers to the
/// returned <see cref="IMountSession"/> only after <see cref="IFilesystemMountBackend.MountAsync"/>
/// completes successfully. If mounting throws or is cancelled, the caller still
/// owns and must dispose the filesystem session.
/// </summary>
public sealed record FilesystemMountRequest(
  IFilesystemSession Filesystem,
  string Target,
  MountPlan Plan,
  bool OwnsFilesystemSession = false
);

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
