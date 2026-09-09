#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.RomFs;

/// <summary>
/// Explicit ROMFS mount driver. Read-only sessions are available for the regular
/// file/directory subset decoded by <see cref="RomFsReader"/>. Writable mounting
/// is deliberately narrower: only flat regular-file images whose complete
/// namespace and metadata can be reproduced by the existing <see cref="RomFsWriter"/>
/// are promoted to the transactional whole-image rebuild session.
/// </summary>
public sealed class RomFsFilesystemDriverAdapter : IFilesystemDriverAdapter {
  private const FilesystemDriverCapabilities ReadCapabilities =
    FilesystemDriverCapabilities.EnumerateDirectories |
    FilesystemDriverCapabilities.ReadData |
    FilesystemDriverCapabilities.RandomAccess |
    FilesystemDriverCapabilities.StableNodeIds |
    FilesystemDriverCapabilities.CaseSensitiveNames |
    FilesystemDriverCapabilities.CasePreservingNames;

  private const FilesystemDriverCapabilities WriteCapabilities =
    FilesystemDriverCapabilities.WriteData |
    FilesystemDriverCapabilities.Truncate |
    FilesystemDriverCapabilities.CreateFile |
    FilesystemDriverCapabilities.DeleteFile |
    FilesystemDriverCapabilities.Rename |
    FilesystemDriverCapabilities.Flush;

  public string FormatId => "RomFs";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      return Unsupported("ROMFS mounted reads require a readable, seekable image.");

    var original = image.Position;
    try {
      var reader = new RomFsReader(image);
      ValidateDecodedEntries(image, reader);
      if (reader.HasUnsupportedNodeTypes)
        return Unsupported(
          "ROMFS contains hard links, symbolic links, devices, sockets, or FIFOs that the current reader/writer pair cannot reproduce completely.");

      var limitations = new List<string> {
        "Node ids are stable for this mounted session; ROMFS has no durable inode identity across a rebuild/remount.",
      };

      var flatRegularFiles = reader.Entries.All(static entry => !entry.IsDirectory && !entry.Name.Contains('/'));
      var writableStream = image.CanWrite;
      var canMountWritable = flatRegularFiles && writableStream;
      if (!flatRegularFiles)
        limitations.Add(
          "Writable mounting is restricted to flat regular-file ROMFS images because the existing writer cannot preserve an empty directory after its last child is removed.");
      if (!writableStream)
        limitations.Add("The backing image stream is not writable.");

      if (canMountWritable)
        limitations.Add(
          "Flush rebuilds and validates the complete ROMFS image, then publishes it with rollback on in-process write failure; this is not claimed to be host-crash-atomic.");

      return new FilesystemDriverProfile(
        FormatId,
        canMountWritable ? "ROMFS transactional rebuild" : "ROMFS decoded read-only namespace",
        ReadCapabilities | (canMountWritable ? WriteCapabilities : FilesystemDriverCapabilities.None),
        canMountWritable ? FilesystemMutationModel.WholeImageRebuild : FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: canMountWritable,
        limitations);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return Unsupported(FirstLine(e.Message));
    } finally {
      image.Position = original;
    }
  }

  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);

    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("ROMFS image is not mountable: " + string.Join("; ", profile.Limitations));
    if (!options.ReadOnly && !profile.CanMountWritable)
      throw new NotSupportedException(
        "This ROMFS image is not qualified for writable mounting: " + string.Join("; ", profile.Limitations));

    image.Position = 0;
    var reader = new RomFsReader(image);
    var volumeName = reader.VolumeName;
    var entries = reader.Entries
      .Select(entry => entry.IsDirectory
        ? RebuildFilesystemEntry.Directory(entry.Name)
        : RebuildFilesystemEntry.File(entry.Name, reader.Extract(entry)))
      .ToArray();

    var sessionProfile = options.ReadOnly ? ReadOnlyProfile(profile) : profile;
    return new MutableRebuildFilesystemSession(
      sessionProfile,
      image,
      entries,
      options.ReadOnly ? null : (output, snapshot) => Rebuild(output, snapshot, volumeName),
      options.ReadOnly ? null : (candidate, snapshot) => ValidateRebuild(candidate, snapshot, volumeName),
      options.LeaveOpen);
  }

  public FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(
      Stream image,
      FilesystemDriverTarget target) {
    var profile = ProbeFilesystem(image);
    const FilesystemDriverReadinessLayer readRequired =
      FilesystemDriverReadinessLayer.ImageValidation |
      FilesystemDriverReadinessLayer.Namespace |
      FilesystemDriverReadinessLayer.SessionStableNodeIds |
      FilesystemDriverReadinessLayer.ReadData |
      FilesystemDriverReadinessLayer.RandomAccessRead;
    const FilesystemDriverReadinessLayer writeRequired =
      readRequired |
      FilesystemDriverReadinessLayer.WriteData |
      FilesystemDriverReadinessLayer.Truncate |
      FilesystemDriverReadinessLayer.NamespaceMutation |
      FilesystemDriverReadinessLayer.Flush |
      FilesystemDriverReadinessLayer.DurabilityModel |
      FilesystemDriverReadinessLayer.Concurrency;

    var available = profile.CanMount ? readRequired : FilesystemDriverReadinessLayer.None;
    if (profile.CanMountWritable)
      available |= FilesystemDriverReadinessLayer.WriteData |
                   FilesystemDriverReadinessLayer.Truncate |
                   FilesystemDriverReadinessLayer.NamespaceMutation |
                   FilesystemDriverReadinessLayer.Flush |
                   FilesystemDriverReadinessLayer.DurabilityModel |
                   FilesystemDriverReadinessLayer.Concurrency;

    var required = target == FilesystemDriverTarget.ReadOnly ? readRequired : writeRequired;
    var derivable = profile.CanMount && (available & required) == required;
    return new FilesystemDriverReadinessReport(
      FormatId,
      target,
      available,
      required,
      derivable,
      UsesNativeProvider: true,
      profile.Limitations);
  }

  private static void Rebuild(
      Stream output,
      IReadOnlyList<RebuildFilesystemEntry> entries,
      string volumeName) {
    using var writer = new RomFsWriter(output, leaveOpen: true);
    foreach (var entry in entries) {
      if (entry.Kind == FilesystemNodeKind.Directory)
        throw new InvalidDataException(
          "Qualified writable ROMFS profiles are flat; an unexpected directory cannot be reproduced safely.");
      if (entry.Kind != FilesystemNodeKind.RegularFile)
        throw new InvalidDataException($"ROMFS rebuild cannot reproduce node kind '{entry.Kind}'.");
      if (entry.Path.Contains('/'))
        throw new InvalidDataException("Qualified writable ROMFS profiles cannot introduce nested paths.");
      writer.AddFile(entry.Path, entry.Data.ToArray());
    }
    writer.Finish(volumeName);
  }

  private static void ValidateRebuild(
      Stream candidate,
      IReadOnlyList<RebuildFilesystemEntry> expectedEntries,
      string expectedVolumeName) {
    candidate.Position = 0;
    var reader = new RomFsReader(candidate);
    if (reader.HasUnsupportedNodeTypes)
      throw new InvalidDataException("Rebuilt ROMFS unexpectedly contains an unsupported node kind.");
    if (!string.Equals(reader.VolumeName, expectedVolumeName, StringComparison.Ordinal))
      throw new InvalidDataException(
        $"Rebuilt ROMFS volume label changed from '{expectedVolumeName}' to '{reader.VolumeName}'.");
    if (reader.Entries.Any(static entry => entry.IsDirectory))
      throw new InvalidDataException("Rebuilt qualified flat ROMFS unexpectedly contains a directory.");

    var expected = expectedEntries
      .Where(static entry => entry.Kind == FilesystemNodeKind.RegularFile)
      .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
      .ToArray();
    var actual = reader.Entries
      .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
      .ToArray();
    if (expected.Length != actual.Length)
      throw new InvalidDataException(
        $"Rebuilt ROMFS contains {actual.Length} files; expected {expected.Length}.");

    for (var i = 0; i < expected.Length; ++i) {
      if (!string.Equals(expected[i].Path, actual[i].Name, StringComparison.Ordinal))
        throw new InvalidDataException(
          $"Rebuilt ROMFS path mismatch: expected '{expected[i].Path}', found '{actual[i].Name}'.");
      var actualData = reader.Extract(actual[i]);
      if (!expected[i].Data.Span.SequenceEqual(actualData))
        throw new InvalidDataException($"Rebuilt ROMFS payload for '{expected[i].Path}' differs from the mutable session.");
    }
  }

  private static void ValidateDecodedEntries(Stream image, RomFsReader reader) {
    var paths = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in reader.Entries) {
      if (string.IsNullOrEmpty(entry.Name) || entry.Name.StartsWith('/') || entry.Name.Contains('\\'))
        throw new InvalidDataException($"ROMFS reader returned invalid relative path '{entry.Name}'.");
      if (!paths.Add(entry.Name))
        throw new InvalidDataException($"ROMFS contains duplicate decoded path '{entry.Name}'.");
      if (entry.IsDirectory)
        continue;
      if (entry.Size < 0 || entry.DataOffset < 0 || entry.DataOffset > image.Length || entry.Size > image.Length - entry.DataOffset)
        throw new InvalidDataException(
          $"ROMFS file '{entry.Name}' extent [{entry.DataOffset}, {entry.DataOffset + Math.Max(0, entry.Size)}) lies outside the image.");
    }
  }

  private static FilesystemDriverProfile ReadOnlyProfile(FilesystemDriverProfile probed)
    => probed with {
      ProfileName = "ROMFS decoded read-only namespace",
      Capabilities = probed.Capabilities & ~WriteCapabilities,
      MutationModel = FilesystemMutationModel.None,
      CanMountWritable = false,
    };

  private static FilesystemDriverProfile Unsupported(string reason)
    => new(
      "RomFs",
      "unsupported or damaged ROMFS profile",
      FilesystemDriverCapabilities.None,
      FilesystemMutationModel.None,
      CanMount: false,
      CanMountWritable: false,
      [reason]);

  private static string FirstLine(string message) {
    var index = message.IndexOfAny(['\r', '\n']);
    return index < 0 ? message : message[..index];
  }
}
