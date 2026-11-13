using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Mounting;

/// <summary>
/// Archive mount adapter that keeps every mutable file in an independent
/// disk-backed inode. Changes are replayed on a private output candidate
/// rather than requiring an archive-sized managed byte array.
/// </summary>
internal static class SpoolingArchiveMountSession {
  public static IFilesystemSession Open(
      IFormatDescriptor descriptor, Stream source, string? password,
      FilesystemDriverProfile profile) {
    if (descriptor is not IArchiveFormatOperations archive)
      throw new NotSupportedException("Disk-spooled archive mounts require an entry reader.");

    var modifier = descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify)
      ? descriptor as IArchiveModifiable : null;
    var creator = descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate)
      ? descriptor as IArchiveCreatable : null;
    if (modifier is null && creator is null)
      throw new NotSupportedException("Archive has neither an editor nor a creator.");

    var snapshot = MountSourceSnapshot.Capture(source);
    try {
      List<ArchiveEntryInfo> originals;
      using (var reader = snapshot.OpenRead())
        originals = archive.List(reader, password);

      var stored = originals.ToDictionary(
        static e => Normalize(e.Name), StringComparer.Ordinal);
      var entries = originals.Select(item => item.IsDirectory
        ? SpoolingRebuildFilesystemEntry.Directory(Normalize(item.Name))
        : SpoolingRebuildFilesystemEntry.File(Normalize(item.Name), item.OriginalSize,
            () => OpenEntry(archive, snapshot, item.Name, password))).ToArray();

      void ApplyNativeEdits(Stream candidate,
          IReadOnlyList<SpoolingRebuildFilesystemEntry> current, bool copyOriginal) {
        var after = current.ToDictionary(static e => e.Path, StringComparer.Ordinal);

        if (modifier is null) {
          Recreate(candidate, current, originals, creator!);
          return;
        }

        List<ArchiveEntryInfo> baseline;
        if (copyOriginal) {
          candidate.SetLength(0);
          candidate.Position = 0;
          using var original = snapshot.OpenRead();
          original.CopyTo(candidate);
          baseline = originals;
        } else {
          candidate.Position = 0;
          baseline = archive.List(candidate, password);
        }
        candidate.Position = 0;
        var baselineMap = baseline.ToDictionary(
          static entry => Normalize(entry.Name), StringComparer.Ordinal);

        var removed = baseline
          .Where(e => !after.TryGetValue(Normalize(e.Name), out var now) ||
                      (e.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile) != now.Kind)
          .Select(static e => e.Name)
          .OrderByDescending(static e => e.Length)
          .ToArray();
        var staged = new List<FileStream>();
        try {
          if (removed.Length > 0)
            modifier.Remove(candidate, removed);

          var additions = new List<ArchiveInputInfo>();
          foreach (var entry in current) {
            if (entry.Kind == FilesystemNodeKind.Directory) {
              var present = baselineMap.TryGetValue(entry.Path, out var old) && old.IsDirectory;
              var empty = !current.Any(other => other.Path.StartsWith(entry.Path + "/", StringComparison.Ordinal));
              if (!present && empty)
                additions.Add(new(entry.Path, entry.Path + "/", IsDirectory: true));
              continue;
            }
            var changed = !baselineMap.TryGetValue(entry.Path, out var before) || before.IsDirectory ||
                          before.OriginalSize != entry.Length;
            if (!changed) {
              using var oldReader = copyOriginal
                ? OpenEntry(archive, snapshot, before!.Name, password)
                : OpenLiveEntry(archive, (FileStream)candidate, before!.Name, password);
              using var newReader = entry.OpenRead!();
              changed = !SameBytes(oldReader, newReader);
            }
            if (!changed)
              continue;

            var temporary = CreateTemp();
            staged.Add(temporary);
            using (var data = entry.OpenRead!())
              data.CopyTo(temporary);
            if (temporary.Length != entry.Length)
              throw new InvalidDataException($"Changed entry '{entry.Path}' has inconsistent length.");
            temporary.Flush();
            additions.Add(new(
              temporary.Name,
              entry.Path,
              IsDirectory: false,
              Metadata: Metadata(baselineMap.GetValueOrDefault(entry.Path))));
          }

          if (additions.Count > 0)
            modifier.Add(candidate, additions);
        } catch (NotSupportedException) when (creator is not null) {
          Recreate(candidate, current, originals, creator);
        } finally {
          foreach (var temp in staged)
            temp.Dispose();
        }
        candidate.Position = 0;
      }

      MutableRebuildFilesystemSession.RebuildImageFromStreams rebuild =
        (candidate, current) => ApplyNativeEdits(candidate, current, copyOriginal: true);

      MutableRebuildFilesystemSession.ValidateImageFromStreams validate = (candidate, current) => {
        candidate.Position = 0;
        var actual = archive.List(candidate, password);
        var expected = current
          .Where(static e => e.Kind == FilesystemNodeKind.RegularFile)
          .ToDictionary(static e => e.Path, StringComparer.Ordinal);
        if (actual.Count(static e => !e.IsDirectory) != expected.Count)
          throw new InvalidDataException("Rebuilt archive changed the number of files.");

        foreach (var item in actual.Where(static e => !e.IsDirectory)) {
          var path = Normalize(item.Name);
          if (!expected.TryGetValue(path, out var file) || item.OriginalSize != file.Length)
            throw new InvalidDataException($"Unexpected file or length in rebuilt archive: '{path}'.");
          candidate.Position = 0;
          using var written = archive.OpenEntry(candidate, item.Name, password);
          using var staged = file.OpenRead!();
          if (!SameBytes(written, staged))
            throw new InvalidDataException($"Rebuilt archive changed the bytes of '{path}'.");

          if (stored.TryGetValue(path, out var before) && before.LastModified is { } timestamp &&
              before.OriginalSize == file.Length) {
            using var original = OpenEntry(archive, snapshot, before.Name, password);
            using var unchanged = file.OpenRead!();
            if (SameBytes(original, unchanged) && item.LastModified != timestamp)
              throw new InvalidDataException($"Rebuilt archive lost the timestamp of unchanged '{path}'.");
          }
        }

        foreach (var directory in current.Where(static entry => entry.Kind == FilesystemNodeKind.Directory)) {
          var required = (stored.TryGetValue(directory.Path, out var before) && before.IsDirectory) ||
            !current.Any(item => item.Path.StartsWith(directory.Path + "/", StringComparison.Ordinal));
          if (required && !actual.Any(item => item.IsDirectory && Normalize(item.Name) == directory.Path))
            throw new InvalidDataException($"Rebuilt archive lost directory '{directory.Path}'.");
        }

        if (archive is IArchiveSemanticMetadataProvider metadata) {
          IReadOnlyDictionary<string, string> previous;
          using (var original = snapshot.OpenRead())
            previous = metadata.GetContainerProperties(original);
          candidate.Position = 0;
          var latest = metadata.GetContainerProperties(candidate);
          if (latest.Count != previous.Count || previous.Any(item =>
              !latest.TryGetValue(item.Key, out var value) || value != item.Value))
            throw new InvalidDataException("Rebuilt archive changed unrelated container properties.");
        }
      };

      // Native central-directory edits are the only qualified live path.
      // Journal + rollback are mandatory, even when a format supports direct
      // seek writes. A creator-only or memory-backed archive uses the ordinary
      // immutable candidate path instead.
      var canEditLive = modifier is not null && source is FileStream &&
        descriptor is IArchiveInPlaceMountWriter inPlaceProfile &&
        inPlaceProfile.CanEditInPlace(source);
      MutableRebuildFilesystemSession.CommitImageInPlace? journalled = canEditLive
        ? (live, current) => JournaledArchiveWriteback.Apply((FileStream)live,
          image => ApplyNativeEdits(image, current, copyOriginal: false),
          image => validate(image, current))
        : null;

      var session = MutableRebuildFilesystemSession.OpenSpooling(
        profile, source, entries, rebuild, validate, leaveOpen: true,
        journalledInPlaceCommit: journalled);
      return new OwnedFilesystemSession(session, snapshot);
    } catch {
      snapshot.Dispose();
      throw;
    }
  }

  private static void Recreate(Stream candidate,
      IReadOnlyList<SpoolingRebuildFilesystemEntry> current,
      IReadOnlyList<ArchiveEntryInfo> originals, IArchiveCreatable creator) {
    var before = originals.ToDictionary(
      static item => Normalize(item.Name), StringComparer.Ordinal);
    var descriptor = (IFormatDescriptor)creator;
    var entries = new List<StreamingArchiveInput>(current.Count);
    long total = 0;
    foreach (var entry in current) {
      var directory = entry.Kind == FilesystemNodeKind.Directory;
      if (!directory && entry.Kind != FilesystemNodeKind.RegularFile)
        throw new NotSupportedException("Archive reconstruction requires ordinary files.");
      if (directory && !descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories))
        throw new NotSupportedException("The archive cannot represent an explicit directory.");
      if (!directory)
        total = checked(total + entry.Length);
      var metadata = Metadata(before.GetValueOrDefault(entry.Path));
      var input = new StreamingArchiveInput(
        directory ? entry.Path + "/" : entry.Path,
        directory ? 0 : entry.Length,
        directory,
        entry.OpenRead ?? (static () => Stream.Null),
        metadata);
      if (descriptor is IArchiveWriteConstraints restrictions) {
        var check = new ArchiveInputInfo(input.Name, input.Name, directory,
          Metadata: input.Metadata);
        if (!restrictions.CanAccept(check, out var reason))
          throw new NotSupportedException($"Archive writer rejects '{input.Name}': {reason}");
      }
      entries.Add(input);
    }
    if (descriptor is IArchiveWriteConstraints limits &&
        limits.MaxTotalArchiveSize is { } cap && total > cap)
      throw new NotSupportedException($"Archive writer limits total payload to {cap:N0} bytes.");

    candidate.SetLength(0);
    candidate.Position = 0;
    creator.CreateFromStreams(candidate, entries, new FormatCreateOptions());
    candidate.Position = 0;
  }

  private static ArchiveEntryMetadata? Metadata(ArchiveEntryInfo? entry) {
    if (entry?.LastModified is not { } date)
      return null;
    var utc = date.Kind == DateTimeKind.Unspecified
      ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime();
    return new(LastWriteTimeUtc: new DateTimeOffset(utc));
  }

  private static Stream OpenLiveEntry(IArchiveFormatOperations archive,
      FileStream live, string name, string? password) {
    var reader = new FileStream(live.Name, FileMode.Open, FileAccess.Read,
      FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
    try {
      return new OwnedReadStream(archive.OpenEntry(reader, name, password), reader);
    } catch {
      reader.Dispose();
      throw;
    }
  }

  private static Stream OpenEntry(IArchiveFormatOperations ops,
      MountSourceSnapshot snapshot, string name, string? password) {
    var source = snapshot.OpenRead();
    try {
      return new OwnedReadStream(ops.OpenEntry(source, name, password), source);
    } catch {
      source.Dispose();
      throw;
    }
  }

  private static string Normalize(string name) {
    if (string.IsNullOrWhiteSpace(name) || name[0] is '/' or '\\')
      throw new InvalidDataException($"Invalid archive member name '{name}'.");
    var path = name.Replace('\\', '/').TrimEnd('/');
    if (path.Split('/').Any(static p => p is "" or "." or ".."))
      throw new InvalidDataException($"Unsafe archive member path '{name}'.");
    return path;
  }

  private static bool SameBytes(Stream first, Stream second) {
    byte[] left = new byte[64 * 1024], right = new byte[left.Length];
    while (true) {
      var a = first.Read(left);
      if (a == 0)
        return second.ReadByte() < 0;
      var read = 0;
      while (read < a) {
        var next = second.Read(right.AsSpan(read, a - read));
        if (next == 0)
          return false;
        read += next;
      }
      if (!left.AsSpan(0, a).SequenceEqual(right.AsSpan(0, a)))
        return false;
    }
  }

  private static FileStream CreateTemp() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb-mount-input-{Guid.NewGuid():N}.tmp");
    return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
      FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.DeleteOnClose);
  }

  private sealed class OwnedReadStream(Stream content, Stream source) : Stream {
    public override bool CanRead => content.CanRead;
    public override bool CanSeek => content.CanSeek;
    public override bool CanWrite => false;
    public override long Length => content.Length;
    public override long Position { get => content.Position; set => content.Position = value; }
    public override void Flush() => content.Flush();
    public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => content.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => content.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
      if (disposing) {
        try { content.Dispose(); }
        finally { source.Dispose(); }
      }
      base.Dispose(disposing);
    }
  }
}
