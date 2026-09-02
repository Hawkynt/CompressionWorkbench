using System.Runtime.Versioning;
using Compression.Registry;
using DokanNet;

namespace Compression.Mounting.Dokan;

[SupportedOSPlatform("windows")]
internal sealed class DokanMountSession(
  DokanNet.Dokan dokan,
  DokanInstance instance,
  DokanFilesystemOperations operations,
  IFilesystemSession filesystem,
  string requestedTarget,
  bool ownsFilesystem
) : IMountSession {
  private readonly SemaphoreSlim _lifecycle = new(1, 1);
  private readonly DokanNet.Dokan _dokan = dokan ?? throw new ArgumentNullException(nameof(dokan));
  private readonly DokanInstance _instance = instance ?? throw new ArgumentNullException(nameof(instance));
  private readonly DokanFilesystemOperations _operations = operations ?? throw new ArgumentNullException(nameof(operations));
  private readonly IFilesystemSession _filesystem = filesystem ?? throw new ArgumentNullException(nameof(filesystem));
  private readonly string _requestedTarget = requestedTarget ?? throw new ArgumentNullException(nameof(requestedTarget));
  private readonly bool _ownsFilesystem = ownsFilesystem;
  private bool _disposed;

  public string BackendId => "dokan";
  public string Target => this._operations.MountedTarget ?? this._requestedTarget;
  public MountAccessMode AccessMode => MountAccessMode.ReadOnly;
  public bool IsMounted => !this._disposed && this._instance.IsFileSystemRunning();

  public ValueTask FlushAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (this._filesystem.Profile.Capabilities.HasFlag(FilesystemDriverCapabilities.Flush))
      this._filesystem.Flush();

    return ValueTask.CompletedTask;
  }

  public async ValueTask UnmountAsync(CancellationToken cancellationToken = default) {
    await this._lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      if (this._disposed)
        return;

      if (this._instance.IsFileSystemRunning()) {
        var removalRequested = this._dokan.RemoveMountPoint(this.Target);
        if (!removalRequested && this._instance.IsFileSystemRunning())
          throw new IOException($"Dokan refused to unmount '{this.Target}'.");

        while (this._instance.IsFileSystemRunning()) {
          cancellationToken.ThrowIfCancellationRequested();
          if (await this._instance.WaitForFileSystemClosedAsync(100).ConfigureAwait(false))
            break;
        }
      }

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

      if (this._instance.IsFileSystemRunning())
        this._dokan.RemoveMountPoint(this.Target);

      this.DisposeResources();
    } finally {
      this._lifecycle.Release();
    }
  }

  private void DisposeResources() {
    if (this._disposed)
      return;

    try {
      this._instance.Dispose();
    } finally {
      try {
        this._dokan.Dispose();
      } finally {
        if (this._ownsFilesystem)
          this._filesystem.Dispose();
        this._disposed = true;
      }
    }
  }
}
