using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// Capability-qualified, bounded archive mount bridge. Native modifiers edit
/// a staged copy of the source; WORM-only creators reconstruct a complete
/// candidate archive in a temporary file. Neither mutates the live image
/// until verified publication at Flush.
/// </summary>
/// <remarks>
/// Only unencrypted ordinary archive entries that fit in a bounded in-memory
/// namespace qualify. Creator-only formats additionally require a successful
/// lossless no-op round trip before mounted writes are advertised. Filesystem
/// images and structured pseudo-archives are not promoted by WORM capability
/// alone. Unsafe formats continue to mount read-only.
/// </remarks>
internal static class MutableArchiveMountSession {
  private const long MaxWritableContentBytes = 64L * 1024 * 1024;
  private const string UnsafeProfileMessage =
    "Writable archive mounts require ordinary unencrypted entries with safe unique names, " +
    "no links, and at most 64 MiB of logical content.";

  public static FilesystemDriverProfile Probe(
      IFormatDescriptor descriptor,
      Stream source,
      string? password) {
    var basic = FilesystemDriverDerivation.Probe(descriptor, source, password,
      preserveFileIdentity: source is FileStream && !source.CanWrite);
    if (!basic.CanMount || !Eligible(descriptor, password))
      return basic;

    var position = source.Position;
    try {
      source.Position = 0;
      var entries = ((IArchiveFormatOperations)descriptor).List(source, password);
      if (!QualifiesEntries(entries) ||
          (entries.Any(static entry => entry.IsDirectory) &&
           !descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories)))
        return WithLimitation(basic, UnsafeProfileMessage);

      var hasNativeEditor = HasNativeEditor(descriptor);
      if (!hasNativeEditor) {
        // CanCreate is WORM, not a promise that re-creating an existing image
        // preserves its names, payloads and metadata. Prove that first, using
        // a throwaway candidate. The live archive is not touched.
        using var snapshot = MountSourceSnapshot.Capture(source);
        var original = ReadEntries((IArchiveFormatOperations)descriptor, snapshot, password);
        using var candidate = CreateCandidate();
        RecreateArchive(descriptor, candidate, original, entries);
        VerifyRecreatedSource((IArchiveFormatOperations)descriptor, snapshot, candidate, password);
      }

      var directoryCapabilities = descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories)
        ? FilesystemDriverCapabilities.CreateDirectory | FilesystemDriverCapabilities.RemoveDirectory
        : FilesystemDriverCapabilities.None;

      return new FilesystemDriverProfile(
        descriptor.Id,
        hasNativeEditor ? "qualified native archive editor" : "qualified WORM copy rebuild",
        FilesystemMountCapabilityResolver.CoreReadCapabilities |
        FilesystemMountCapabilityResolver.CoreWriteCapabilities |
        directoryCapabilities |
        FilesystemDriverCapabilities.CaseSensitiveNames |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.WholeImageRebuild,
        CanMount: true,
        CanMountWritable: true,
        [
          hasNativeEditor
            ? "Native archive edit operations run on a private candidate copy before verified publication."
            : "WORM-only archive creation reconstructs the complete image in a temporary file on every flush.",
          "The mount provides regular-file writes with stable handles; archive links and unsupported metadata remain read-only.",
          "Publication to an ordinary host file is not crash-atomic; do not edit the archive concurrently.",
        ]);
    } catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException or OverflowException or UnauthorizedAccessException) {
      return WithLimitation(basic, $"Write-back qualification refused: {ex.Message}");
    } finally {
      source.Position = position;
    }
  }

  public static IFilesystemSession Open(
      IFormatDescriptor descriptor,
      Stream source,
      string? password) {
    var profile = Probe(descriptor, source, password);
    if (!profile.CanMountWritable)
      throw new NotSupportedException(
        $"Archive '{descriptor.Id}' does not qualify for verified mounted writes: {string.Join("; ", profile.Limitations)}");
    if (!source.CanWrite)
      throw new NotSupportedException("The backing archive stream is not writable.");

    var archive = (IArchiveFormatOperations)descriptor;
    var modifier = HasNativeEditor(descriptor) ? (IArchiveModifiable)descriptor : null;
    var creator = CanRecreate(descriptor) ? (IArchiveCreatable)descriptor : null;
    var sourceSnapshot = MountSourceSnapshot.Capture(source);
    try {
      var original = ReadEntries(archive, sourceSnapshot, password);
      var initial = original.ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
      var explicitlyStored = ReadStoredNames(archive, sourceSnapshot, password);
      List<ArchiveEntryInfo> originalEntries;
      using (var reader = sourceSnapshot.OpenRead())
        originalEntries = archive.List(reader, password);
      var sourceEntryMap = originalEntries.ToDictionary(
        static entry => NormalizePath(entry.Name), StringComparer.Ordinal);

      MutableRebuildFilesystemSession.RebuildImage rebuild = (candidate, current) => {
        if (modifier is null) {
          RecreateArchive(descriptor, candidate, current, originalEntries);
          return;
        }

        try {
          var final = current.ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
          candidate.SetLength(0);
          candidate.Position = 0;
          using (var snapshot = sourceSnapshot.OpenRead())
            snapshot.CopyTo(candidate);
          candidate.Position = 0;

          var removed = explicitlyStored
            .Where(item => !final.ContainsKey(item.Key))
            .Select(static item => item.Value)
            .OrderByDescending(static name => name.Length)
            .ToArray();
          if (removed.Length != 0)
            modifier.Remove(candidate, removed);

          var additions = new List<ArchiveInputInfo>();
          foreach (var entry in current) {
            if (entry.Kind == FilesystemNodeKind.Directory) {
              // Ordinary parent directories are implied by file paths.
              if (!initial.ContainsKey(entry.Path) &&
                  !current.Any(item => item.Path.StartsWith(entry.Path + "/", StringComparison.Ordinal)))
                additions.Add(new(entry.Path, entry.Path + "/", IsDirectory: true));
              continue;
            }

            if (!initial.TryGetValue(entry.Path, out var prior) ||
                prior.Kind != entry.Kind || !prior.Data.Span.SequenceEqual(entry.Data.Span))
              additions.Add(MakeInput(entry, sourceEntryMap.GetValueOrDefault(entry.Path)));
          }

          if (additions.Count != 0)
            modifier.Add(candidate, additions);

          candidate.Position = 0;
        } catch (NotSupportedException) when (creator is not null) {
          // A modifier may support only some on-disk profiles. If a safe
          // creator exists, abandon its partially modified private copy and
          // reconstruct the entire archive instead.
          RecreateArchive(descriptor, candidate, current, originalEntries);
        }
      };

      MutableRebuildFilesystemSession.ValidateImage validate = (candidate, current) => {
        var expected = current
          .Where(static entry => entry.Kind == FilesystemNodeKind.RegularFile)
          .ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        candidate.Position = 0;
        var actual = archive.List(candidate, password);
        var actualFiles = actual.Where(static entry => !entry.IsDirectory).ToArray();
        if (actualFiles.Length != expected.Count)
          throw new InvalidDataException("Archive write-back changed the number of regular files.");

        foreach (var file in actualFiles) {
          var path = NormalizePath(file.Name);
          if (!expected.TryGetValue(path, out var wanted) || file.OriginalSize != wanted.Data.Length)
            throw new InvalidDataException($"Archive write-back changed or unexpectedly added '{path}'.");
          candidate.Position = 0;
          using var extracted = archive.OpenEntry(candidate, file.Name, password);
          if (!EqualsExact(extracted, wanted.Data.Span))
            throw new InvalidDataException($"Archive write-back verification failed for '{path}'.");

          // WORM reconstruction must not silently reset the timestamps of
          // untouched entries. A writer that cannot retain them is rejected
          // before the live file is published.
          if (sourceEntryMap.TryGetValue(path, out var before) && before.LastModified is { } oldTime &&
              initial.TryGetValue(path, out var previous) &&
              previous.Data.Span.SequenceEqual(wanted.Data.Span) &&
              file.LastModified != oldTime)
            throw new InvalidDataException($"Archive write-back changed the timestamp of untouched '{path}'.");
        }

        foreach (var directory in current.Where(static entry => entry.Kind == FilesystemNodeKind.Directory)) {
          var required = explicitlyStored.ContainsKey(directory.Path) ||
            !current.Any(item => item.Path.StartsWith(directory.Path + "/", StringComparison.Ordinal));
          if (required && !actual.Any(item => item.IsDirectory && NormalizePath(item.Name) == directory.Path))
            throw new InvalidDataException($"Archive write-back discarded directory '{directory.Path}'.");
        }

        if (archive is IArchiveSemanticMetadataProvider semantic) {
          IReadOnlyDictionary<string, string> expectedProperties;
          using (var reader = sourceSnapshot.OpenRead())
            expectedProperties = semantic.GetContainerProperties(reader);
          candidate.Position = 0;
          var publishedProperties = semantic.GetContainerProperties(candidate);
          if (expectedProperties.Count != publishedProperties.Count ||
              expectedProperties.Any(item =>
                !publishedProperties.TryGetValue(item.Key, out var value) || value != item.Value))
            throw new InvalidDataException("Archive write-back changed unrelated container metadata.");
        }
      };

      var session = new MutableRebuildFilesystemSession(
        profile, source, original, rebuild, validate, leaveOpen: true,
        maxTotalContentBytes: MaxWritableContentBytes);
      return new OwnedFilesystemSession(session, sourceSnapshot);
    } catch {
      sourceSnapshot.Dispose();
      throw;
    }
  }

  private static bool Eligible(IFormatDescriptor descriptor, string? password)
    => (descriptor.Category is FormatCategory.Archive or FormatCategory.CompoundTar)
      && descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries)
      && string.IsNullOrEmpty(password)
      && descriptor is IArchiveFormatOperations
      && descriptor is not IRandomAccessBlockDeviceProvider
      && (HasNativeEditor(descriptor) || CanRecreate(descriptor));

  private static bool HasNativeEditor(IFormatDescriptor descriptor)
    => descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify)
      && descriptor is IArchiveModifiable;

  private static bool CanRecreate(IFormatDescriptor descriptor)
    => descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate)
      && descriptor is IArchiveCreatable;

  private static FilesystemDriverProfile WithLimitation(FilesystemDriverProfile baseProfile, string reason)
    => baseProfile with { Limitations = baseProfile.Limitations.Append(reason).ToArray() };

  private static bool QualifiesEntries(IReadOnlyList<ArchiveEntryInfo> entries) {
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long total = 0;
    foreach (var entry in entries) {
      if (entry.IsEncrypted || entry.IsSymlink || entry.LinkTarget is not null ||
          entry.Kind is not null ||
          entry.OriginalSize < 0 || entry.OriginalSize > MaxWritableContentBytes)
        return false;

      string path;
      try { path = NormalizePath(entry.Name); }
      catch (InvalidDataException) { return false; }

      if (!seen.Add(path))
        return false;
      if (!entry.IsDirectory) {
        if (total > MaxWritableContentBytes - entry.OriginalSize)
          return false;
        total += entry.OriginalSize;
      }
    }
    return true;
  }


  private static FileStream CreateCandidate() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb-mount-probe-{Guid.NewGuid():N}.tmp");
    return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
      FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);
  }

  private static ArchiveInputInfo MakeInput(RebuildFilesystemEntry entry, ArchiveEntryInfo? original) {
    ArchiveEntryMetadata? metadata = original?.LastModified is { } modified
      ? new(LastWriteTimeUtc: new DateTimeOffset(
        modified.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(modified, DateTimeKind.Utc) : modified.ToUniversalTime()))
      : null;
    return entry.Kind == FilesystemNodeKind.Directory
      ? new ArchiveInputInfo(entry.Path, entry.Path + "/", IsDirectory: true, Metadata: metadata)
      : ArchiveInputInfo.InMemory(entry.Path, entry.Data.ToArray(), metadata);
  }

  private static void RecreateArchive(
      IFormatDescriptor descriptor,
      Stream candidate,
      IReadOnlyList<RebuildFilesystemEntry> entries,
      IReadOnlyList<ArchiveEntryInfo> originalEntries) {
    if (descriptor is not IArchiveCreatable creator)
      throw new NotSupportedException("This format cannot recreate an archive from files.");

    var sourceNames = originalEntries.ToDictionary(
      static entry => NormalizePath(entry.Name), StringComparer.Ordinal);
    var supportsDirectories = descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories);
    var inputs = new List<ArchiveInputInfo>(entries.Count);
    long total = 0;

    foreach (var entry in entries) {
      var original = sourceNames.GetValueOrDefault(entry.Path);
      if (entry.Kind == FilesystemNodeKind.Directory) {
        var explicitlyStored = original?.IsDirectory == true;
        var empty = !entries.Any(other => other.Path.StartsWith(entry.Path + "/", StringComparison.Ordinal));
        if (!explicitlyStored && !empty)
          continue; // Parent exists implicitly through its descendants.
        if (!supportsDirectories)
          throw new NotSupportedException($"Archive '{descriptor.Id}' cannot store an empty directory.");
      } else if (entry.Kind != FilesystemNodeKind.RegularFile) {
        throw new NotSupportedException("Archive recreation only supports ordinary files and directories.");
      } else {
        total = checked(total + entry.Data.Length);
      }

      var input = MakeInput(entry, original);
      if (descriptor is IArchiveWriteConstraints constraints &&
          !constraints.CanAccept(input, out var reason))
        throw new NotSupportedException(
          $"Archive '{descriptor.Id}' refuses '{entry.Path}': {reason ?? constraints.AcceptedInputsDescription}");
      inputs.Add(input);
    }

    if (descriptor is IArchiveWriteConstraints limits &&
        limits.MaxTotalArchiveSize is { } max && total > max)
      throw new NotSupportedException($"Archive '{descriptor.Id}' exceeds its {max:N0}-byte input limit.");

    candidate.SetLength(0);
    candidate.Position = 0;
    creator.Create(candidate, inputs, new FormatCreateOptions());
    candidate.Position = 0;
  }

  private static void VerifyRecreatedSource(
      IArchiveFormatOperations archive,
      MountSourceSnapshot source,
      Stream candidate,
      string? password) {
    using var original = source.OpenRead();
    var before = ArchiveSemanticManifest.Capture(original, archive, password);
    candidate.Position = 0;
    var after = ArchiveSemanticManifest.Capture(candidate, archive, password);
    before.RequireSameAs(after, "WORM archive mount qualification");
    candidate.Position = 0;
  }

  private static List<RebuildFilesystemEntry> ReadEntries(
      IArchiveFormatOperations archive,
      MountSourceSnapshot snapshot,
      string? password) {
    List<ArchiveEntryInfo> listed;
    using (var reader = snapshot.OpenRead())
      listed = archive.List(reader, password);
    if (!QualifiesEntries(listed))
      throw new NotSupportedException("This archive cannot be safely materialized for writable mounting.");

    var results = new List<RebuildFilesystemEntry>(listed.Count);
    foreach (var entry in listed) {
      var name = NormalizePath(entry.Name);
      if (entry.IsDirectory) {
        results.Add(RebuildFilesystemEntry.Directory(name));
        continue;
      }
      using var reader = snapshot.OpenRead();
      using var contents = archive.OpenEntry(reader, entry.Name, password);
      using var output = new MemoryStream();
      contents.CopyTo(output);
      if (output.Length != entry.OriginalSize)
        throw new InvalidDataException($"Archive entry '{name}' has a size different from its directory record.");
      results.Add(RebuildFilesystemEntry.File(name, output.ToArray()));
    }
    return results;
  }

  private static Dictionary<string, string> ReadStoredNames(
      IArchiveFormatOperations archive,
      MountSourceSnapshot snapshot,
      string? password) {
    using var reader = snapshot.OpenRead();
    return archive.List(reader, password)
      .ToDictionary(static entry => NormalizePath(entry.Name),
        static entry => entry.Name, StringComparer.Ordinal);
  }

  private static string NormalizePath(string name) {
    if (string.IsNullOrWhiteSpace(name) || name[0] is '/' or '\\')
      throw new InvalidDataException("Archive entries must use relative paths.");
    var path = name.Replace('\\', '/').TrimEnd('/');
    var segments = path.Split('/');
    if (segments.Any(static segment => segment is "" or "." or ".."))
      throw new InvalidDataException($"Archive entry '{name}' contains an unsafe path component.");
    return path;
  }

  private static bool EqualsExact(Stream actual, ReadOnlySpan<byte> wanted) {
    Span<byte> buffer = stackalloc byte[8192];
    while (!wanted.IsEmpty) {
      var count = actual.Read(buffer[..Math.Min(buffer.Length, wanted.Length)]);
      if (count <= 0 || !buffer[..count].SequenceEqual(wanted[..count]))
        return false;
      wanted = wanted[count..];
    }
    return actual.ReadByte() == -1;
  }
}
