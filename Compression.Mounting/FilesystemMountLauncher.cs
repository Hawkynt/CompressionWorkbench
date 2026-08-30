using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// Opens a backing image and filesystem session for one already-selected mount
/// request. Capability policy is re-evaluated against the exact stream and the
/// opened session immediately before the backend receives ownership.
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

    if (!FormatRegistry.FilesystemFormatIds.Contains(formatId, StringComparer.OrdinalIgnoreCase))
      throw new ArgumentException($"Format '{formatId}' is not registered as a filesystem.", nameof(formatId));

    var backend = this._backends.GetBackend(requestedPlan.BackendId);
    FileStream? source = null;
    IFilesystemSession? filesystem = null;
    var ownershipTransferred = false;

    try {
      source = OpenBackingSource(imagePath, requestedPlan.AccessMode);

      var probePosition = source.CanSeek ? source.Position : 0;
      FilesystemDriverProfile probedProfile;
      try {
        probedProfile = FormatRegistry.ProbeFilesystem(formatId, source);
      } finally {
        if (source.CanSeek)
          source.Position = probePosition;
      }

      var resolvedPlan = this._backends.ResolveFilesystem(
        requestedPlan.BackendId,
        probedProfile,
        requestedPlan.AccessMode,
        source.CanWrite
      );
      EnsureSupported(resolvedPlan);

      filesystem = FormatRegistry.OpenFilesystem(
        formatId,
        source,
        new FilesystemOpenOptions(
          ReadOnly: requestedPlan.AccessMode == MountAccessMode.ReadOnly,
          LeaveOpen: false
        )
      );

      // OpenFilesystem is allowed to specialize the profile further than Probe.
      // Re-resolve once more so the backend never receives a session whose exact
      // opened profile is weaker than the plan shown by the probe.
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
      source = null;
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
