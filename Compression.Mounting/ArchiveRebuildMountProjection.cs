using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// Qualifies WORM/mutable archives for file-backed mounted write sessions.
/// The caller's source stream is not modified until a validated Flush.
/// </summary>
internal static class ArchiveRebuildMountProjection {
  private const FilesystemDriverCapabilities Writable =
    FilesystemMountCapabilityResolver.CoreReadCapabilities |
    FilesystemMountCapabilityResolver.CoreWriteCapabilities |
    FilesystemDriverCapabilities.CasePreservingNames;

  public static FilesystemDriverProfile? Probe(IFormatDescriptor descriptor, Stream image, string? password) {
    if (descriptor is not (IArchiveFormatOperations and IArchiveCreatable) ||
        !descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries) ||
        !image.CanRead || !image.CanSeek)
      return null;

    try {
      var archive = (IArchiveFormatOperations)descriptor;
      image.Position = 0;
      var entries = archive.List(image, password);
      CheckEntries(entries);
      if (!descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories) &&
          entries.Any(static e => e.IsDirectory))
        return null;

      return new(
        descriptor.Id,
        "file-backed WORM archive rebuild",
        Writable | (descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories)
          ? FilesystemDriverCapabilities.CreateDirectory | FilesystemDriverCapabilities.RemoveDirectory
          : FilesystemDriverCapabilities.None),
        FilesystemMutationModel.WholeImageRebuild,
        CanMount: true,
        CanMountWritable: true,
        [
          "Each mutable entry is staged in a temporary file, with no managed-array size limit.",
          "Flush rebuilds and verifies a full candidate; local path-backed sources use journaled same-directory replacement.",
          "Unencoded format-specific metadata may be lost; directory fsync is unavailable through the portable BCL.",
          "Existing native in-place mutation is not automatically considered mount-grade or crash-atomic.",
        ]);
    } catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or ArgumentException or IOException or OverflowException) {
      return null;
    }
  }

  public static IFilesystemSession Open(IFormatDescriptor descriptor, Stream image, string? password) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanWrite || !image.CanSeek)
      throw new NotSupportedException("Staged writable archive requires a read/write/seekable source.");
    var profile = Probe(descriptor, image, password)
      ?? throw new NotSupportedException("Archive cannot be safely staged for writing.");
    return new FileBackedArchiveFilesystemSession(descriptor, profile, image, imagePath: null, password);
  }

  public static IFilesystemSession OpenPath(IFormatDescriptor descriptor, string path, string? password) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    path = Path.GetFullPath(path);
    // Hold the same exclusive mount lock through crash recovery, probing,
    // staging, all Flush operations and disposal; never recover without it.
    var mountLock = new FileStream(path + ".cwbmount.lock", FileMode.OpenOrCreate,
      FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
    try {
      ArchiveAtomicPublication.Recover(path);
      using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      var profile = Probe(descriptor, image, password)
        ?? throw new NotSupportedException("Archive cannot be safely staged for writing.");
      return new FileBackedArchiveFilesystemSession(descriptor, profile,
        image: null, imagePath: path, password: password, existingMountLock: mountLock);
    } catch {
      mountLock.Dispose();
      throw;
    }
  }

  private static void CheckEntries(IReadOnlyList<ArchiveEntryInfo> entries) {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in entries) {
      var path = entry.Name.Replace('\\', '/').Trim('/');
      if (path.Length == 0 || path.Split('/').Any(static c => c is "" or "." or "..") ||
          path.IndexOfAny([':', '\0']) >= 0 || !names.Add(path))
        throw new NotSupportedException("Archive contains unsafe or ambiguous mount paths.");
      if (entry.IsSymlink || entry.IsEncrypted || entry.LinkTarget is not null)
        throw new NotSupportedException("Encrypted or linked entries require a metadata-preserving writer.");
      if (!entry.IsDirectory && entry.OriginalSize < 0)
        throw new NotSupportedException("Archive has an unknown logical entry size.");
    }
  }
}
