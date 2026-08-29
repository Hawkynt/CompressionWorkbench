using Compression.Registry;

namespace Compression.Mounting;

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
