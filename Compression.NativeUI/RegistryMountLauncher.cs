using Compression.Mounting;

namespace Compression.NativeUI;

internal sealed class RegistryMountLauncher(FilesystemMountLauncher launcher) : IMountLauncher {
  private readonly FilesystemMountLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));

  public ValueTask<IMountSession> MountAsync(
    string imagePath,
    string formatId,
    MountPlan plan,
    string target,
    CancellationToken cancellationToken = default
  ) => this._launcher.MountAsync(imagePath, formatId, plan, target, cancellationToken);
}
