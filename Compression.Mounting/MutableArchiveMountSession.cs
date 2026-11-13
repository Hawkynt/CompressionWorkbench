using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// A deliberately qualified, bounded write-back bridge for ordinary ZIP, 7z
/// and RAR archives. The existing format editor performs all container writes;
/// the mounted namespace never rewrites compressed blocks itself.
/// </summary>
/// <remarks>
/// Only unencrypted, regular-file/directory profiles that fit in a bounded
/// in-memory namespace qualify. Every flush starts from an immutable source
/// snapshot, stages editor operations off the live image and verifies every
/// resulting file before transactional publication. Unqualified inputs retain
/// their generic read-only projection.
/// </remarks>
internal static class MutableArchiveMountSession {
  private const long MaxWritableContentBytes = 64L * 1024 * 1024;
  private static readonly HashSet<string> QualifiedFormats = new(StringComparer.OrdinalIgnoreCase) {
    "Zip", "SevenZip", "Rar",
  };

  public static FilesystemDriverProfile Probe(
      IFormatDescriptor descriptor,
      Stream source,
      string? password) {
    var basic = FilesystemDriverDerivation.Probe(descriptor, source, password);
    if (!basic.CanMount || !Eligible(descriptor, password))
      return basic;

    var position = source.Position;
    try {
      source.Position = 0;
      var entries = ((IArchiveFormatOperations)descriptor).List(source, password);
      if (!QualifiesEntries(entries))
        return basic with {
          Limitations = basic.Limitations.Append(
            "Writable archive mounts require unencrypted, non-link entries with unique, safe paths and at most 64 MiB of logical content.").ToArray(),
        };

      return new FilesystemDriverProfile(
        descriptor.Id,
        "qualified mutable archive write-back",
        FilesystemMountCapabilityResolver.CoreReadCapabilities |
        FilesystemMountCapabilityResolver.CoreWriteCapabilities |
        FilesystemDriverCapabilities.CreateDirectory |
        FilesystemDriverCapabilities.RemoveDirectory |
        FilesystemDriverCapabilities.CaseSensitiveNames |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.WholeImageRebuild,
        CanMount: true,
        CanMountWritable: true,
        [
          "Writes are staged in memory and published as a verified whole-image transaction at flush/unmount.",
          "Only regular files and directories are writable; encryption, links, and archive-specific metadata mutations are not emulated.",
          "An interrupted host write-back is not crash-atomic; avoid using the archive concurrently in another application.",
        ]);
    } catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException) {
      return basic;
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
    var modifier = (IArchiveModifiable)descriptor;
    var sourceSnapshot = MountSourceSnapshot.Capture(source);
    try {
      var original = ReadEntries(archive, sourceSnapshot, password);
      var initial = original.ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
      var explicitlyStored = ReadStoredNames(archive, sourceSnapshot, password);

      MutableRebuildFilesystemSession.RebuildImage rebuild = (candidate, current) => {
        var final = current.ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
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
            // Parents of files are implied by archive paths; only explicitly
            // empty directories need an entry when absent from the input.
            if (!initial.ContainsKey(entry.Path) &&
                !current.Any(item => item.Path.StartsWith(entry.Path + "/", StringComparison.Ordinal)))
              additions.Add(new(entry.Path, entry.Path + "/", IsDirectory: true));
            continue;
          }

          if (!initial.TryGetValue(entry.Path, out var prior) ||
              prior.Kind != entry.Kind || !prior.Data.Span.SequenceEqual(entry.Data.Span))
            additions.Add(ArchiveInputInfo.InMemory(entry.Path, entry.Data.ToArray()));
        }

        if (additions.Count != 0)
          modifier.Add(candidate, additions);

        candidate.Position = 0;
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
        }

        foreach (var directory in current.Where(static entry => entry.Kind == FilesystemNodeKind.Directory)) {
          if (current.Any(item => item.Path.StartsWith(directory.Path + "/", StringComparison.Ordinal)))
            continue;
          if (!actual.Any(item => item.IsDirectory && NormalizePath(item.Name) == directory.Path))
            throw new InvalidDataException($"Archive write-back discarded empty directory '{directory.Path}'.");
        }
      };

      var session = new MutableRebuildFilesystemSession(
        profile, source, original, rebuild, validate, leaveOpen: true);
      return new OwnedFilesystemSession(session, sourceSnapshot);
    } catch {
      sourceSnapshot.Dispose();
      throw;
    }
  }

  private static bool Eligible(IFormatDescriptor descriptor, string? password)
    => QualifiedFormats.Contains(descriptor.Id)
      && string.IsNullOrEmpty(password)
      && descriptor is IArchiveFormatOperations
      && descriptor is IArchiveModifiable;

  private static bool QualifiesEntries(IReadOnlyList<ArchiveEntryInfo> entries) {
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long total = 0;
    foreach (var entry in entries) {
      if (entry.IsEncrypted || entry.IsSymlink || entry.LinkTarget is not null ||
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
