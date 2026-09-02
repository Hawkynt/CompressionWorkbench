using Compression.Registry;

namespace Compression.Mounting;

public static class FilesystemMountCapabilityResolver {
  public const FilesystemDriverCapabilities CoreReadCapabilities =
    FilesystemDriverCapabilities.EnumerateDirectories |
    FilesystemDriverCapabilities.ReadData |
    FilesystemDriverCapabilities.RandomAccess |
    FilesystemDriverCapabilities.StableNodeIds;

  public const FilesystemDriverCapabilities CoreWriteCapabilities =
    FilesystemDriverCapabilities.WriteData |
    FilesystemDriverCapabilities.Truncate |
    FilesystemDriverCapabilities.CreateFile |
    FilesystemDriverCapabilities.DeleteFile |
    FilesystemDriverCapabilities.CreateDirectory |
    FilesystemDriverCapabilities.RemoveDirectory |
    FilesystemDriverCapabilities.Rename |
    FilesystemDriverCapabilities.Flush;

  public static MountPlan Resolve(
    FilesystemDriverProfile driverProfile,
    MountBackendProfile backend,
    MountAccessMode accessMode,
    bool sourceCanWrite
  ) {
    ArgumentNullException.ThrowIfNull(driverProfile);
    ArgumentNullException.ThrowIfNull(backend);

    var reasons = new List<MountSupportReason>();
    var requiredRead = CoreReadCapabilities | backend.RequiredReadCapabilities;
    var required = requiredRead;

    if (!backend.IsAvailable)
      reasons.Add(new(MountSupportReasonCode.BackendUnavailable, $"Mount backend '{backend.DisplayName}' is not available on this host."));

    if (!backend.SupportsReadOnly)
      reasons.Add(new(MountSupportReasonCode.BackendDoesNotSupportReadOnly, $"Mount backend '{backend.DisplayName}' does not support filesystem reads."));

    if (!driverProfile.CanMount)
      reasons.Add(new(MountSupportReasonCode.FilesystemProfileNotMountable, $"Filesystem profile '{driverProfile.ProfileName}' is not mount-grade."));

    var missingRead = requiredRead & ~driverProfile.Capabilities;
    if (missingRead != FilesystemDriverCapabilities.None)
      reasons.Add(MissingCapabilitiesReason(missingRead, "read-only"));

    if (accessMode == MountAccessMode.ReadWrite) {
      required |= CoreWriteCapabilities | backend.RequiredWriteCapabilities;

      if (!backend.SupportsReadWrite)
        reasons.Add(new(MountSupportReasonCode.BackendDoesNotSupportReadWrite, $"Mount backend '{backend.DisplayName}' does not support writable mounts."));

      if (!driverProfile.CanMountWritable)
        reasons.Add(new(MountSupportReasonCode.FilesystemProfileNotWritable, $"Filesystem profile '{driverProfile.ProfileName}' does not advertise writable mounting."));

      if (!sourceCanWrite)
        reasons.Add(new(MountSupportReasonCode.SourceIsReadOnly, "The backing image or an outer container layer is read-only."));

      if (driverProfile.MutationModel is FilesystemMutationModel.None or FilesystemMutationModel.WholeImageRebuild)
        reasons.Add(new(MountSupportReasonCode.UnsupportedMutationModel, $"Mutation model '{driverProfile.MutationModel}' is not suitable for mounted random writes."));

      var missingWrite = (CoreWriteCapabilities | backend.RequiredWriteCapabilities) & ~driverProfile.Capabilities;
      if (missingWrite != FilesystemDriverCapabilities.None)
        reasons.Add(MissingCapabilitiesReason(missingWrite, "read-write"));
    }

    var missing = required & ~driverProfile.Capabilities;
    var limitations = driverProfile.Limitations.Concat(backend.Limitations).Distinct(StringComparer.Ordinal).ToArray();

    return new(
      accessMode,
      reasons.Count == 0,
      driverProfile,
      backend,
      required,
      missing,
      reasons,
      limitations
    );
  }

  private static MountSupportReason MissingCapabilitiesReason(FilesystemDriverCapabilities missing, string mode)
    => new(
      MountSupportReasonCode.MissingDriverCapabilities,
      $"Filesystem profile is missing {mode} mount primitives: {FormatCapabilities(missing)}.",
      missing
    );

  private static string FormatCapabilities(FilesystemDriverCapabilities capabilities)
    => string.Join(", ", Enum.GetValues<FilesystemDriverCapabilities>()
      .Where(value => value != FilesystemDriverCapabilities.None && (value & (value - 1)) == 0 && capabilities.HasFlag(value)));
}
