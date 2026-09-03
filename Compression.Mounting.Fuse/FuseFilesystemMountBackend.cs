using Compression.Registry;

namespace Compression.Mounting.Fuse;

/// <summary>
/// Linux FUSE3 low-level backend. The first qualified slice is deliberately
/// read-only and single-threaded; all source parsing remains inside
/// CompressionWorkbench and libfuse only transports the parsed namespace.
/// </summary>
public sealed class FuseFilesystemMountBackend : IFilesystemMountBackend {
  private readonly FuseRuntimeStatus _runtime;

  public FuseFilesystemMountBackend()
    : this(FuseRuntimeProbe.Probe()) { }

  public FuseFilesystemMountBackend(FuseRuntimeStatus runtime)
    => this._runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

  public FuseRuntimeStatus RuntimeStatus => this._runtime;

  public MountBackendProfile GetProfile() {
    var limitations = new List<string>();
    if (!this._runtime.IsAvailable)
      limitations.Add(this._runtime.UnavailableReason ?? "FUSE3 is unavailable on this host.");
    limitations.Add("Read-write FUSE mounting remains disabled until mutation and durability conformance pass.");
    limitations.Add("The initial FUSE3 session loop is single-threaded while callback concurrency semantics are qualified.");
    limitations.Add("The native libfuse ABI layout is currently qualified for Linux x64.");

    return new(
      Id: "fuse3",
      DisplayName: "FUSE3",
      IsAvailable: this._runtime.IsAvailable,
      SupportsReadOnly: true,
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

    if (!string.Equals(request.Plan.BackendId, "fuse3", StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException($"Mount plan targets backend '{request.Plan.BackendId}', not FUSE3.", nameof(request));
    if (request.Plan.AccessMode != MountAccessMode.ReadOnly || !request.Plan.IsSupported)
      throw new FilesystemMountNotSupportedException(request.Plan);
    if (!this._runtime.IsAvailable)
      throw new PlatformNotSupportedException(this._runtime.UnavailableReason ?? "FUSE3 is unavailable on this host.");
    if (!OperatingSystem.IsLinux())
      throw new PlatformNotSupportedException("FUSE3 mounting is available only on Linux.");

    var operations = new FuseFilesystemOperations(request.Filesystem);
    try {
      var nativeSession = FuseNativeSession.Mount(operations, request.Target);
      IMountSession session = new FuseMountSession(
        nativeSession,
        operations,
        request.Filesystem,
        request.Target,
        request.OwnsFilesystemSession
      );
      return ValueTask.FromResult(session);
    } catch {
      operations.Dispose();
      throw;
    }
  }
}
