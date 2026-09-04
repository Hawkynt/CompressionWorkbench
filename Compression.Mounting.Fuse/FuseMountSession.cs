using Compression.Registry;

namespace Compression.Mounting.Fuse;

internal sealed class FuseMountSession(
  FuseNativeSession nativeSession,
  FuseFilesystemOperations operations,
  IFilesystemSession filesystem,
  string target,
  bool ownsFilesystem
) : IMountSession {
  private readonly SemaphoreSlim _lifecycle = new(1, 1);
  private readonly FuseNativeSession _nativeSession = nativeSession ?? throw new ArgumentNullException(nameof(nativeSession));
  private readonly FuseFilesystemOperations _operations = operations ?? throw new ArgumentNullException(nameof(operations));
  private readonly IFilesystemSession _filesystem = filesystem ?? throw new ArgumentNullException(nameof(filesystem));
  private readonly string _target = target ?? throw new ArgumentNullException(nameof(target));
  private readonly bool _ownsFilesystem = ownsFilesystem;
  private bool _disposed;

  public string BackendId => "fuse3";
  public string Target => this._target;
  public MountAccessMode AccessMode => MountAccessMode.ReadOnly;
  public bool IsMounted => !this._disposed && this._nativeSession.IsMounted;

  public ValueTask FlushAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    ObjectDisposedException.ThrowIf(this._disposed, this);

    var error = this._operations.FlushFilesystem();
    if (error != FuseErrno.Success)
      throw new IOException($"FUSE filesystem flush failed with errno {error}.");

    return ValueTask.CompletedTask;
  }

  public async ValueTask UnmountAsync(CancellationToken cancellationToken = default) {
    await this._lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      if (this._disposed)
        return;

      await this._nativeSession.UnmountAsync(cancellationToken).ConfigureAwait(false);
      this.DisposeResources();
    } finally {
      this._lifecycle.Release();
    }
  }

  public async ValueTask DisposeAsync() {
    await this._lifecycle.WaitAsync().ConfigureAwait(false);
    try {
      if (this._disposed)
        return;

      if (this._nativeSession.IsMounted)
        await this._nativeSession.UnmountAsync().ConfigureAwait(false);

      this.DisposeResources();
    } finally {
      this._lifecycle.Release();
    }
  }

  private void DisposeResources() {
    if (this._disposed)
      return;

    try {
      this._nativeSession.Dispose();
    } finally {
      try {
        this._operations.Dispose();
      } finally {
        if (this._ownsFilesystem)
          this._filesystem.Dispose();
        this._disposed = true;
      }
    }
  }
}
