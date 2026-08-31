using Compression.Registry;

namespace Compression.Mounting;

public enum MountSupportReasonCode {
  BackendUnavailable,
  BackendDoesNotSupportReadOnly,
  BackendDoesNotSupportReadWrite,
  FilesystemProfileNotMountable,
  FilesystemProfileNotWritable,
  SourceIsReadOnly,
  UnsupportedMutationModel,
  MissingDriverCapabilities,
}

public sealed record MountSupportReason(
  MountSupportReasonCode Code,
  string Message,
  FilesystemDriverCapabilities MissingCapabilities = FilesystemDriverCapabilities.None
);

public sealed record MountBackendProfile(
  string Id,
  string DisplayName,
  bool IsAvailable,
  bool SupportsReadOnly,
  bool SupportsReadWrite,
  FilesystemDriverCapabilities RequiredReadCapabilities,
  FilesystemDriverCapabilities RequiredWriteCapabilities,
  IReadOnlyList<string> Limitations
);

public sealed record MountPlan(
  MountAccessMode AccessMode,
  bool IsSupported,
  FilesystemDriverProfile DriverProfile,
  MountBackendProfile Backend,
  FilesystemDriverCapabilities RequiredCapabilities,
  FilesystemDriverCapabilities MissingCapabilities,
  IReadOnlyList<MountSupportReason> Reasons,
  IReadOnlyList<string> Limitations
) {
  public string BackendId => this.Backend.Id;
}

public sealed record MountAccessOptions(
  IReadOnlyList<MountPlan> ReadOnly,
  IReadOnlyList<MountPlan> ReadWrite
) {
  public bool CanMountReadOnly => this.ReadOnly.Any(static plan => plan.IsSupported);
  public bool CanMountReadWrite => this.ReadWrite.Any(static plan => plan.IsSupported);
}
