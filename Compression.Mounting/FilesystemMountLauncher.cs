using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// Opens one arbitrary registered source as a mount-neutral namespace. Archives,
/// filesystems and nested disk/container images are resolved by
/// <see cref="MountNamespaceResolver"/> before the host backend receives the
/// resulting <see cref="IFilesystemSession"/>.
/// </summary>
public sealed class FilesystemMountLauncher(MountBackendRegistry backends) {
  private readonly MountBackendRegistry _backends = backends ?? throw new ArgumentNullException(nameof(backends));

  public async ValueTask<IMountSession> MountAsync(
    string imagePath,
    string formatId,
    MountPlan requestedPlan,
    string target,
    CancellationToken cancellationToken = default
  ) {
    ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
    ArgumentException.ThrowIfNullOrWhiteSpace(formatId);
    ArgumentNullException.ThrowIfNull(requestedPlan);
    ArgumentException.ThrowIfNullOrWhiteSpace(target);
    cancellationToken.ThrowIfCancellationRequested();

    var descriptor = FormatRegistry.GetById(formatId)
      ?? throw new ArgumentException($"Format '{formatId}' is not registered.", nameof(formatId));
    _ = descriptor;

    var backend = this._backends.GetBackend(requestedPlan.BackendId);
    FileStream? source = null;
    IFilesystemSession? filesystem = null;
    var ownershipTransferred = false;

    try {
      source = OpenBackingSource(imagePath, requestedPlan.AccessMode);

      var probe = MountNamespaceResolver.Probe(formatId, source);
      var resolvedPlan = this._backends.ResolveFilesystem(
        requestedPlan.BackendId,
        probe.Profile,
        requestedPlan.AccessMode,
        source.CanWrite
      );
      EnsureSupported(resolvedPlan);

      filesystem = MountNamespaceResolver.Open(
        formatId,
        source,
        new FilesystemOpenOptions(
          ReadOnly: requestedPlan.AccessMode == MountAccessMode.ReadOnly,
          LeaveOpen: false
        )
      );

      // The opened namespace can be more specific than the probe (for example a
      // container can resolve to FAT/NTFS after opening its logical block view).
      // Re-resolve so the backend never receives a weaker exact session than the
      // plan shown before mount.
      resolvedPlan = this._backends.ResolveFilesystem(
        requestedPlan.BackendId,
        filesystem.Profile,
        requestedPlan.AccessMode,
        source.CanWrite
      );
      EnsureSupported(resolvedPlan);

      cancellationToken.ThrowIfCancellationRequested();
      var mounted = await backend.MountAsync(
        new FilesystemMountRequest(filesystem, target, resolvedPlan, OwnsFilesystemSession: true),
        cancellationToken
      ).ConfigureAwait(false);

      ownershipTransferred = true;
      filesystem = null;
      source = null; // namespace session owns it through MountNamespaceResolver
      return mounted;
    } finally {
      if (!ownershipTransferred) {
        filesystem?.Dispose();
        source?.Dispose();
      }
    }
  }

  private static FileStream OpenBackingSource(string path, MountAccessMode accessMode)
    => accessMode switch {
      MountAccessMode.ReadOnly => new(path, FileMode.Open, FileAccess.Read, FileShare.Read),
      MountAccessMode.ReadWrite => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read),
      _ => throw new ArgumentOutOfRangeException(nameof(accessMode), accessMode, "Unknown mount access mode."),
    };

  private static void EnsureSupported(MountPlan plan) {
    if (plan.IsSupported) return;
    throw new FilesystemMountNotSupportedException(plan);
  }
}

public sealed class FilesystemMountNotSupportedException : InvalidOperationException {
  public FilesystemMountNotSupportedException(MountPlan plan)
    : base(CreateMessage(plan))
    => this.Plan = plan ?? throw new ArgumentNullException(nameof(plan));

  public MountPlan Plan { get; }

  private static string CreateMessage(MountPlan? plan) {
    ArgumentNullException.ThrowIfNull(plan);
    var reasons = plan.Reasons.Count == 0
      ? "mount plan is unsupported"
      : string.Join("; ", plan.Reasons.Select(static reason => reason.Message));
    return $"{plan.AccessMode} mount through backend '{plan.BackendId}' is not supported: {reasons}.";
  }
}
