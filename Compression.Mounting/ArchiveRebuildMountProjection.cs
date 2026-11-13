using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// Mounts WORM-capable archives through the existing whole-image rebuild session.
/// The source is never edited on Write/Truncate/Rename: Flush first creates and
/// validates a complete candidate, then publishes it with rollback on failure.
/// </summary>
internal static class ArchiveRebuildMountProjection {
  // MutableRebuildFilesystemSession currently stores file content as byte[].
  // Reject sources that exceed its in-memory limits instead of advertising a
  // mount that can only fail after the user begins writing.
  private const long MaxTotalBytes = 256L * 1024 * 1024;

  private const FilesystemDriverCapabilities Writable =
    FilesystemMountCapabilityResolver.CoreReadCapabilities |
    FilesystemMountCapabilityResolver.CoreWriteCapabilities |
    FilesystemDriverCapabilities.CasePreservingNames;

  public static FilesystemDriverProfile? Probe(
      IFormatDescriptor descriptor,
      Stream image,
      string? password) {
    if (descriptor is not (IArchiveFormatOperations and IArchiveCreatable) ||
        !descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries))
      return null;

    if (!image.CanRead || !image.CanSeek)
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
        "staged WORM archive rebuild",
        Writable | (descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories)
          ? FilesystemDriverCapabilities.CreateDirectory | FilesystemDriverCapabilities.RemoveDirectory
          : FilesystemDriverCapabilities.None),
        FilesystemMutationModel.WholeImageRebuild,
        CanMount: true,
        CanMountWritable: true,
        [
          "Writes are buffered in a mutable namespace and published as a whole-archive rebuild on Flush.",
          "Current staged writer limits the sum of original entry bytes to 256 MiB and individual files to the managed array limit.",
          "Publication restores the old stream on recoverable I/O failure, but cannot guarantee power-loss atomicity for a path-backed file.",
          "Format-specific metadata not exposed by the archive entry API cannot be promised byte-exact preservation.",
        ]);
    } catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or ArgumentException or IOException or OverflowException) {
      return null;
    }
  }

  public static IFilesystemSession Open(
      IFormatDescriptor descriptor,
      Stream image,
      string? password) {
    ArgumentNullException.ThrowIfNull(descriptor);
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new NotSupportedException("A staged writable archive requires a readable, writable, seekable backing stream.");
    var profile = Probe(descriptor, image, password)
      ?? throw new NotSupportedException($"Archive '{descriptor.Id}' cannot be safely projected for staged writes.");

    var archive = (IArchiveFormatOperations)descriptor;
    var creator = (IArchiveCreatable)descriptor;
    image.Position = 0;
    var listing = archive.List(image, password);
    CheckEntries(listing);

    var originals = new List<RebuildFilesystemEntry>(listing.Count);
    var metadata = new Dictionary<string, ArchiveEntryMetadata>(StringComparer.Ordinal);
    foreach (var entry in listing) {
      var path = entry.Name.Replace('\\', '/').Trim('/');
      if (entry.LastModified is { } lastModified)
        metadata[path] = new(LastWriteTimeUtc: new DateTimeOffset(lastModified.ToUniversalTime(), TimeSpan.Zero));

      if (entry.IsDirectory) {
        originals.Add(RebuildFilesystemEntry.Directory(path));
        continue;
      }

      image.Position = 0;
      using var extracted = archive.OpenEntrySeekable(image, entry.Name, password);
      using var buffer = new MemoryStream();
      extracted.CopyTo(buffer);
      if (buffer.Length != entry.OriginalSize)
        throw new InvalidDataException($"Entry '{entry.Name}' extracted {buffer.Length} bytes, expected {entry.OriginalSize}.");
      originals.Add(RebuildFilesystemEntry.File(path, buffer.ToArray()));
    }

    return new MutableRebuildFilesystemSession(
      profile,
      image,
      originals,
      rebuild: (output, items) => {
        var inputs = items.Select(item => item.Kind == FilesystemNodeKind.Directory
          ? new ArchiveInputInfo(item.Path, item.Path, IsDirectory: true, Metadata: metadata.GetValueOrDefault(item.Path))
          : ArchiveInputInfo.InMemory(item.Path, item.Data.ToArray(), metadata.GetValueOrDefault(item.Path)))
          .ToArray();
        creator.Create(output, inputs, new FormatCreateOptions { Password = password });
      },
      validate: (candidate, items) => {
        candidate.Position = 0;
        var actual = archive.List(candidate, password);
        if (actual.Count != items.Count)
          throw new InvalidDataException("Rebuilt archive lost or added entries.");
        var expected = items.ToDictionary(item => item.Path, StringComparer.Ordinal);
        foreach (var entry in actual) {
          var name = entry.Name.Replace('\\', '/').Trim('/');
          if (!expected.TryGetValue(name, out var original) ||
              entry.IsDirectory != (original.Kind == FilesystemNodeKind.Directory) ||
              (!entry.IsDirectory && entry.OriginalSize != original.Data.Length))
            throw new InvalidDataException($"Rebuilt archive entry '{entry.Name}' differs from the mounted namespace.");
          if (entry.IsDirectory) continue;

          candidate.Position = 0;
          using var reader = archive.OpenEntrySeekable(candidate, entry.Name, password);
          if (!reader.CanRead) throw new InvalidDataException($"Cannot verify rebuilt entry '{entry.Name}'.");
          if (!reader.ReadExactlyMatches(original.Data.Span))
            throw new InvalidDataException($"Rebuilt archive entry '{entry.Name}' has different contents.");
        }
      },
      leaveOpen: true);
  }

  private static void CheckEntries(IReadOnlyList<ArchiveEntryInfo> entries) {
    long total = 0;
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in entries) {
      var path = entry.Name.Replace('\\', '/').Trim('/');
      if (path.Length == 0 || path.Split('/').Any(static c => c is "" or "." or "..") ||
          path.IndexOfAny([':', '\0']) >= 0 || !names.Add(path))
        throw new NotSupportedException("Archive contains ambiguous or unsafe mount paths.");
      if (entry.IsSymlink || entry.IsEncrypted || entry.LinkTarget != null)
        throw new NotSupportedException("Encrypted or linked entries require a metadata-preserving writer.");
      if (!entry.IsDirectory) {
        if (entry.OriginalSize < 0 || entry.OriginalSize > Array.MaxLength ||
            (total = checked(total + entry.OriginalSize)) > MaxTotalBytes)
          throw new NotSupportedException("Archive exceeds the bounded in-memory writable staging limit.");
      }
    }
  }
}

internal static class ArchiveMountComparison {
  public static bool ReadExactlyMatches(this Stream stream, ReadOnlySpan<byte> expected) {
    Span<byte> buffer = stackalloc byte[8192];
    var offset = 0;
    while (offset < expected.Length) {
      var count = Math.Min(buffer.Length, expected.Length - offset);
      var read = stream.Read(buffer[..count]);
      if (read == 0 || !buffer[..read].SequenceEqual(expected.Slice(offset, read)))
        return false;
      offset += read;
    }
    return stream.ReadByte() < 0;
  }
}
