using Compression.Registry;

namespace Compression.Mounting.Dokan;

/// <summary>
/// Dokany 2 backend boundary. The runtime probe is real, but mount-mode support
/// remains disabled until the callback bridge has passed backend conformance.
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
    limitations.Add(
      "The Dokan callback bridge has not been qualified yet; read-only and read-write mounting are intentionally disabled."
    );

    return new(
      Id: "dokan",
      DisplayName: "Dokany 2",
      IsAvailable: this._runtime.IsAvailable,
      SupportsReadOnly: false,
      SupportsReadWrite: false,
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

    if (!this._runtime.IsAvailable)
      throw new PlatformNotSupportedException(
        this._runtime.UnavailableReason ?? "Dokany 2 runtime is unavailable."
      );

    throw new NotSupportedException(
      "The Dokan callback bridge is not implemented yet; this backend deliberately advertises no supported mount mode."
    );
  }
}
