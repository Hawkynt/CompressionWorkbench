#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Iso;

/// <summary>
/// Native ISO 9660 filesystem sidecar. The archive descriptor remains the
/// offline editor surface; this adapter gives mount backends a stable namespace
/// and positional file handles without extracting entries into temporary files
/// or byte arrays.
/// </summary>
public sealed class IsoFilesystemDriverAdapter : IFilesystemDriverAdapter {
  private const int LogicalBlockSize = 2048;

  public string FormatId => "Iso";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      return Unsupported("ISO mounted reads require a readable, seekable image.");

    var original = image.Position;
    try {
      using var reader = new IsoReader(image, leaveOpen: true);
      ValidateEntries(image, reader.Entries);

      return new FilesystemDriverProfile(
        FormatId,
        "ECMA-119 native extent reader",
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,
        [
          "File handles read directly from decoded ISO file extents; no extraction or whole-file materialization is used.",
          "Node ids are deterministic for the mounted session; ISO 9660 has no inode number and durable identity across remount is not claimed.",
          "Mounted writes remain disabled: the existing offline ISO modifier does not provide complete nested-directory, open-handle, truncate, and durability semantics.",
        ]);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return Unsupported(FirstLine(e.Message));
    } finally {
      image.Position = original;
    }
  }

  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    if (!options.ReadOnly)
      throw new NotSupportedException(
        "ISO mounted writes are not enabled: offline Add/Remove/relayout is not a complete mount-grade mutation model.");

    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("ISO image is not mountable: " + string.Join("; ", profile.Limitations));

    return new IsoReadOnlyFilesystemSession(image, profile, options.LeaveOpen);
  }

  public FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(
    Stream image,
    FilesystemDriverTarget target
  ) {
    var profile = ProbeFilesystem(image);
    const FilesystemDriverReadinessLayer readRequired =
      FilesystemDriverReadinessLayer.ImageValidation |
      FilesystemDriverReadinessLayer.Namespace |
      FilesystemDriverReadinessLayer.SessionStableNodeIds |
      FilesystemDriverReadinessLayer.ReadData |
      FilesystemDriverReadinessLayer.RandomAccessRead;
    const FilesystemDriverReadinessLayer writeRequired =
      readRequired |
      FilesystemDriverReadinessLayer.AllocationMap |
      FilesystemDriverReadinessLayer.WriteData |
      FilesystemDriverReadinessLayer.Truncate |
      FilesystemDriverReadinessLayer.NamespaceMutation |
      FilesystemDriverReadinessLayer.Flush |
      FilesystemDriverReadinessLayer.DurabilityModel |
      FilesystemDriverReadinessLayer.Concurrency;

    var available = profile.CanMount
      ? readRequired | FilesystemDriverReadinessLayer.AllocationMap
      : FilesystemDriverReadinessLayer.None;
    var blockers = new List<string>(profile.Limitations);
    if (target == FilesystemDriverTarget.ReadWrite) {
      blockers.Add("Implement mounted create/unlink/rename for arbitrary ISO directory extents rather than the current root-oriented offline modifier surface.");
      blockers.Add("Implement positional writes and truncate while preserving ECMA-119 extent, directory-record, path-table and volume-space metadata consistently.");
      blockers.Add("Define open-handle behavior when a file extent or directory record is relocated by a mounted mutation.");
      blockers.Add("Define an explicit flush/durability boundary and fault-injection tests for multi-record metadata publication.");
      blockers.Add("Define callback concurrency and cache invalidation before enabling multi-threaded mounted writes.");
    }

    var required = target == FilesystemDriverTarget.ReadOnly ? readRequired : writeRequired;
    return new FilesystemDriverReadinessReport(
      FormatId,
      target,
      available,
      required,
      profile.CanMount && (available & required) == required,
      UsesNativeProvider: true,
      blockers.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static void ValidateEntries(Stream image, IReadOnlyList<IsoEntry> entries) {
    var paths = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in entries) {
      var path = Normalize(entry.Name);
      if (path.Length == 0)
        throw new InvalidDataException("ISO reader returned an empty filesystem entry path.");
      if (!paths.Add(path))
        throw new InvalidDataException($"ISO filesystem contains duplicate decoded path '{path}'.");
      if (entry.IsDirectory)
        continue;
      if (entry.Size < 0 || entry.DataOffset < 0 || entry.DataOffset > image.Length || entry.Size > image.Length - entry.DataOffset)
        throw new InvalidDataException(
          $"ISO file '{path}' extent [{entry.DataOffset}, {entry.DataOffset + Math.Max(0, entry.Size)}) lies outside the image.");
    }
  }

  private static FilesystemDriverProfile Unsupported(string reason)
    => new(
      "Iso",
      "unsupported or damaged ISO 9660 profile",
      FilesystemDriverCapabilities.None,
      FilesystemMutationModel.None,
      CanMount: false,
      CanMountWritable: false,
      [reason]);

  private static string Normalize(string path)
    => path.Replace('\\', '/').Trim('/');

  private static string FirstLine(string message) {
    var index = message.IndexOfAny(['\r', '\n']);
    return index < 0 ? message : message[..index];
  }

  internal static long AllocatedLength(long logicalLength) {
    if (logicalLength <= 0)
      return 0;
    return checked(((logicalLength + LogicalBlockSize - 1) / LogicalBlockSize) * LogicalBlockSize);
  }
}

internal sealed class IsoReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public IsoReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ISO mounted reads require a readable, seekable image.", nameof(image));

    _image = image;
    _leaveOpen = leaveOpen;
    using var reader = new IsoReader(image, leaveOpen: true);
    var entries = reader.Entries.ToArray();
    ValidateExtentBounds(entries);

    var root = new FilesystemNodeId(1, 1);
    _namespace = new ReadOnlyFilesystemSnapshotSession(profile, root, BuildNodes(entries, root));
  }

  public FilesystemDriverProfile Profile => _namespace.Profile;
  public FilesystemNodeId RootNodeId => _namespace.RootNodeId;
  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) => _namespace.Stat(nodeId);
  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) => _namespace.Lookup(parentDirectory, name);
  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) => _namespace.Enumerate(directory);
  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) => _namespace.OpenFile(nodeId, access);
  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) => _namespace.CreateFile(parentDirectory, name);
  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) => _namespace.CreateDirectory(parentDirectory, name);
  public void DeleteFile(FilesystemNodeId parentDirectory, string name) => _namespace.DeleteFile(parentDirectory, name);
  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) => _namespace.RemoveDirectory(parentDirectory, name);
  public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace)
    => _namespace.Rename(oldParent, oldName, newParent, newName, replace);
  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName)
    => _namespace.CreateHardLink(existingNode, newParent, newName);
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target)
    => _namespace.CreateSymbolicLink(parentDirectory, name, target);
  public string ReadSymbolicLink(FilesystemNodeId nodeId) => _namespace.ReadSymbolicLink(nodeId);
  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) => _namespace.SetMetadata(nodeId, patch);
  public void Flush() => _namespace.Flush();
  public IFilesystemTransaction BeginTransaction() => _namespace.BeginTransaction();

  public void Dispose() {
    if (_disposed)
      return;
    _disposed = true;
    _namespace.Dispose();
    if (!_leaveOpen)
      _image.Dispose();
  }

  private IReadOnlyList<FilesystemSnapshotNode> BuildNodes(
    IReadOnlyList<IsoEntry> entries,
    FilesystemNodeId rootId
  ) {
    var result = new List<FilesystemSnapshotNode>(entries.Count + 1) {
      new(rootId, default, string.Empty, FilesystemNodeKind.Directory, 0, 0, LinkCount: 2),
    };
    var byPath = new Dictionary<string, FilesystemNodeId>(StringComparer.Ordinal) {
      [string.Empty] = rootId,
    };
    ulong nextId = 2;

    foreach (var entry in entries
      .OrderBy(static entry => Depth(entry.Name))
      .ThenBy(static entry => Normalize(entry.Name), StringComparer.Ordinal)) {
      var path = Normalize(entry.Name);
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var name = slash < 0 ? path : path[(slash + 1)..];
      if (!byPath.TryGetValue(parentPath, out var parent))
        throw new InvalidDataException($"ISO entry '{path}' has no decoded parent directory '{parentPath}'.");
      if (byPath.ContainsKey(path))
        throw new InvalidDataException($"ISO filesystem contains duplicate decoded path '{path}'.");

      var nodeId = new FilesystemNodeId(nextId++, 1);
      byPath.Add(path, nodeId);
      var capturedOffset = entry.DataOffset;
      var capturedLength = entry.Size;
      result.Add(new FilesystemSnapshotNode(
        nodeId,
        parent,
        name,
        entry.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile,
        entry.IsDirectory ? 0 : entry.Size,
        entry.IsDirectory ? 0 : IsoFilesystemDriverAdapter.AllocatedLength(entry.Size),
        LinkCount: entry.IsDirectory ? 2U : 1U,
        Modified: ToOffset(entry.LastModified),
        OpenReadHandle: entry.IsDirectory
          ? null
          : () => new IsoPositionalFileHandle(nodeId, _image, _ioGate, capturedOffset, capturedLength)));
    }

    return result;
  }

  private void ValidateExtentBounds(IEnumerable<IsoEntry> entries) {
    foreach (var entry in entries) {
      if (entry.IsDirectory)
        continue;
      if (entry.Size < 0 || entry.DataOffset < 0 || entry.DataOffset > _image.Length || entry.Size > _image.Length - entry.DataOffset)
        throw new InvalidDataException($"ISO file '{entry.Name}' extent lies outside the image.");
    }
  }

  private static int Depth(string path)
    => Normalize(path).Count(static c => c == '/');

  private static string Normalize(string path)
    => path.Replace('\\', '/').Trim('/');

  private static DateTimeOffset? ToOffset(DateTime? value) {
    if (value is null)
      return null;
    return value.Value.Kind switch {
      DateTimeKind.Utc => new DateTimeOffset(value.Value, TimeSpan.Zero),
      DateTimeKind.Local => new DateTimeOffset(value.Value),
      _ => new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), TimeSpan.Zero),
    };
  }
}

internal sealed class IsoPositionalFileHandle(
  FilesystemNodeId nodeId,
  Stream image,
  object ioGate,
  long dataOffset,
  long length
) : IFilesystemFileHandle {
  private readonly Stream _image = image ?? throw new ArgumentNullException(nameof(image));
  private readonly object _ioGate = ioGate ?? throw new ArgumentNullException(nameof(ioGate));
  private readonly long _dataOffset = dataOffset;
  private readonly long _length = length;
  private bool _disposed;

  public FilesystemNodeId NodeId { get; } = nodeId;

  public long Length {
    get {
      ThrowIfDisposed();
      return _length;
    }
  }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    if (offset < 0)
      throw new ArgumentOutOfRangeException(nameof(offset));
    if (destination.Length == 0 || offset >= _length)
      return 0;

    var count = checked((int)Math.Min(destination.Length, _length - offset));
    var physical = checked(_dataOffset + offset);
    lock (_ioGate) {
      if (physical < 0 || physical > _image.Length - count)
        throw new InvalidDataException("ISO file extent ends outside the backing image.");
      _image.Position = physical;
      _image.ReadExactly(destination[..count]);
    }
    return count;
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("The ISO filesystem session is read-only.");

  public void SetLength(long length)
    => throw new NotSupportedException("The ISO filesystem session is read-only.");

  public void Flush()
    => ThrowIfDisposed();

  public void Dispose()
    => _disposed = true;

  private void ThrowIfDisposed()
    => ObjectDisposedException.ThrowIf(_disposed, this);
}
