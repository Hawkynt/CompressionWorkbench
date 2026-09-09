using Compression.Registry;
using DokanNet;
using DokanNet.Logging;

namespace Compression.Mounting.Dokan;

/// <summary>
/// Dokany 2 userspace backend over the stable-node filesystem contract. Writable
/// mounts are admitted only when the resolved filesystem profile provides the
/// mount-grade mutation primitives required by <see cref="FilesystemMountCapabilityResolver"/>.
/// </summary>
public sealed class DokanFilesystemMountBackend : IFilesystemMountBackend {
  private readonly DokanRuntimeStatus _runtime;

  public DokanFilesystemMountBackend()
    : this(DokanRuntimeProbe.Probe()) { }

  public DokanFilesystemMountBackend(DokanRuntimeStatus runtime)
    => this._runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

  public DokanRuntimeStatus RuntimeStatus => this._runtime;

  public MountBackendProfile GetProfile() {
    var limitations = new List<string>();
    if (!this._runtime.IsAvailable)
      limitations.Add(this._runtime.UnavailableReason ?? "Dokany 2 runtime is unavailable.");
    limitations.Add("Optional namespace mutations are exposed only when the filesystem profile advertises them.");

    return new(
      Id: "dokan",
      DisplayName: "Dokany 2",
      IsAvailable: this._runtime.IsAvailable,
      SupportsReadOnly: true,
      SupportsReadWrite: true,
      RequiredReadCapabilities: FilesystemDriverCapabilities.None,
      RequiredWriteCapabilities: FilesystemDriverCapabilities.None,
      Limitations: limitations
    );
  }

  public ValueTask<IMountSession> MountAsync(
    FilesystemMountRequest request,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(request);
    cancellationToken.ThrowIfCancellationRequested();

    if (!string.Equals(request.Plan.BackendId, "dokan", StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException($"Mount plan targets backend '{request.Plan.BackendId}', not Dokan.", nameof(request));
    if (!request.Plan.IsSupported)
      throw new FilesystemMountNotSupportedException(request.Plan);
    if (!this._runtime.IsAvailable)
      throw new PlatformNotSupportedException(this._runtime.UnavailableReason ?? "Dokany 2 runtime is unavailable.");
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Dokan is a Windows-only mount backend.");

    var readOnly = request.Plan.AccessMode == MountAccessMode.ReadOnly;
    var operations = new DokanFilesystemOperations(request.Filesystem, readOnly);
    // DokanOptions is a flag set without a zero-named member; a writable mount simply
    // clears WriteProtection.
    var options = readOnly ? DokanOptions.WriteProtection : default;
    if (request.Filesystem.Profile.Capabilities.HasFlag(FilesystemDriverCapabilities.CaseSensitiveNames))
      options |= DokanOptions.CaseSensitive;

    var dokan = new DokanNet.Dokan(new NullLogger());
    try {
      var instance = new DokanInstanceBuilder(dokan)
        .ConfigureOptions(dokanOptions => {
          dokanOptions.Options = options;
          dokanOptions.MountPoint = request.Target;
        })
        .Build(operations);

      if (!instance.IsFileSystemRunning()) {
        instance.Dispose();
        throw new IOException($"Dokan created mount '{request.Target}', but the filesystem is not running.");
      }

      IMountSession session = new DokanMountSession(
        dokan,
        instance,
        operations,
        request.Filesystem,
        request.Target,
        request.Plan.AccessMode,
        request.OwnsFilesystemSession
      );
      return ValueTask.FromResult(session);
    } catch {
      dokan.Dispose();
      throw;
    }
  }
}
