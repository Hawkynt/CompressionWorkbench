#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Udf;

/// <summary>
/// Native UDF filesystem sidecar. The archive descriptor remains the offline
/// editor surface; mount backends receive a parsed stable namespace plus
/// positional file handles over the decoded allocation-descriptor map.
/// </summary>
public sealed class UdfFilesystemDriverAdapter : IFilesystemDriverAdapter {
  public string FormatId => "Udf";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      return Unsupported("UDF mounted reads require a readable, seekable image.");

    var original = image.Position;
    try {
      using var reader = new UdfReader(image, leaveOpen: true);
      var limitations = reader.Entries
        .Where(static entry => !entry.IsDirectory && entry.MountLimitation is not null)
        .Select(static entry => $"{entry.Name}: {entry.MountLimitation}")
        .Distinct(StringComparer.Ordinal)
        .Take(16)
        .ToList();
      if (limitations.Count != 0)
        return Unsupported(string.Join("; ", limitations));

      ValidateNamespace(reader.Entries);
      return new FilesystemDriverProfile(
        FormatId,
        "ECMA-167/UDF native allocation-descriptor reader",
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.CasePreservingNames |
        FilesystemDriverCapabilities.SparseFiles,
        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,
        [
          "Regular-file handles read directly across decoded short_ad/long_ad extents and embedded data; unrecorded extents are exposed as zero-filled ranges.",
          "Continuation allocation-descriptor chains and non-primary partition-map references fail closed until their address-space semantics are implemented.",
          "Mounted writes remain disabled: existing UDF modification/defragmentation APIs do not provide complete open-handle, arbitrary-directory, truncate, and durability semantics.",
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
        "UDF mounted writes are disabled until namespace mutation, truncate, durability and open-handle semantics are qualified.");

    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("UDF image is not mountable: " + string.Join("; ", profile.Limitations));

    return new UdfReadOnlyFilesystemSession(image, profile, options.LeaveOpen);
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
      blockers.Add("Implement mounted create/unlink/rename for arbitrary directory File Entries and FID extents rather than the current offline modifier surface.");
      blockers.Add("Implement positional writes/truncate across allocation descriptors, including extent allocation/free-space publication and embedded-to-extent transitions.");
      blockers.Add("Define stable open-handle behavior when File Entries or allocation descriptors are relocated by mounted mutations.");
      blockers.Add("Define a crash-consistent flush/durability boundary for File Entry, FID, allocation-space, VDS/LVID and descriptor CRC publication.");
      blockers.Add("Qualify callback concurrency and cache invalidation before enabling multi-threaded mounted writes.");
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

  private static void ValidateNamespace(IReadOnlyList<UdfEntry> entries) {
    var paths = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in entries) {
      var path = Normalize(entry.Name);
      if (path.Length == 0)
        throw new InvalidDataException("UDF reader returned an empty filesystem entry path.");
      if (!paths.Add(path))
        throw new InvalidDataException($"UDF filesystem contains duplicate decoded path '{path}'.");
    }
  }

  private static FilesystemDriverProfile Unsupported(string reason)
    => new(
      "Udf",
      "unsupported or damaged UDF profile",
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
}

internal sealed class UdfReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public UdfReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("UDF mounted reads require a readable, seekable image.", nameof(image));

    _image = image;
    _leaveOpen = leaveOpen;
    using var reader = new UdfReader(image, leaveOpen: true);
    var entries = reader.Entries.ToArray();
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
    IReadOnlyList<UdfEntry> entries,
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
        throw new InvalidDataException($"UDF entry '{path}' has no decoded parent directory '{parentPath}'.");
      if (byPath.ContainsKey(path))
        throw new InvalidDataException($"UDF filesystem contains duplicate decoded path '{path}'.");
      if (entry.MountLimitation is { } limitation)
        throw new NotSupportedException($"UDF entry '{path}' is not mount-readable: {limitation}");

      var nodeId = new FilesystemNodeId(nextId++, 1);
      byPath.Add(path, nodeId);
      var segments = entry.DataSegments.ToArray();
      var allocated = segments.Where(static segment => !segment.ZeroFill).Sum(static segment => segment.Length);
      result.Add(new FilesystemSnapshotNode(
        nodeId,
        parent,
        name,
        entry.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile,
        entry.IsDirectory ? 0 : entry.Size,
        entry.IsDirectory ? 0 : allocated,
        LinkCount: entry.IsDirectory ? 2U : 1U,
        Modified: ToOffset(entry.LastModified),
        OpenReadHandle: entry.IsDirectory
          ? null
          : () => new UdfPositionalFileHandle(nodeId, _image, _ioGate, entry.Size, segments)));
    }

    return result;
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

internal sealed class UdfPositionalFileHandle : IFilesystemFileHandle {
  private readonly Stream _image;
  private readonly object _ioGate;
  private readonly long _length;
  private readonly UdfDataSegment[] _segments;
  private bool _disposed;

  public UdfPositionalFileHandle(
    FilesystemNodeId nodeId,
    Stream image,
    object ioGate,
    long length,
    UdfDataSegment[] segments
  ) {
    NodeId = nodeId;
    _image = image ?? throw new ArgumentNullException(nameof(image));
    _ioGate = ioGate ?? throw new ArgumentNullException(nameof(ioGate));
    _length = length;
    _segments = segments ?? throw new ArgumentNullException(nameof(segments));
    ValidateSegments();
  }

  public FilesystemNodeId NodeId { get; }

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

    var wanted = checked((int)Math.Min(destination.Length, _length - offset));
    var written = 0;
    var logical = offset;
    foreach (var segment in _segments) {
      var segmentEnd = checked(segment.LogicalOffset + segment.Length);
      if (logical >= segmentEnd)
        continue;
      if (logical < segment.LogicalOffset)
        throw new InvalidDataException("UDF file segment map contains a logical gap.");

      var within = logical - segment.LogicalOffset;
      var take = checked((int)Math.Min(wanted - written, segment.Length - within));
      var target = destination.Slice(written, take);
      if (segment.ZeroFill) {
        target.Clear();
      } else {
        var physical = checked(segment.PhysicalOffset + within);
        lock (_ioGate) {
          if (physical < 0 || physical > _image.Length - take)
            throw new InvalidDataException("UDF file extent ends outside the backing image.");
          _image.Position = physical;
          _image.ReadExactly(target);
        }
      }

      logical += take;
      written += take;
      if (written == wanted)
        break;
    }

    if (written != wanted)
      throw new InvalidDataException("UDF file segment map ended before the logical file length.");
    return written;
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("The UDF filesystem session is read-only.");

  public void SetLength(long length)
    => throw new NotSupportedException("The UDF filesystem session is read-only.");

  public void Flush()
    => ThrowIfDisposed();

  public void Dispose()
    => _disposed = true;

  private void ValidateSegments() {
    long expected = 0;
    foreach (var segment in _segments) {
      if (segment.LogicalOffset != expected || segment.Length < 0)
        throw new InvalidDataException("UDF file segment map is discontinuous or invalid.");
      expected = checked(expected + segment.Length);
    }
    if (expected != _length)
      throw new InvalidDataException($"UDF file segment map covers {expected} of {_length} logical bytes.");
  }

  private void ThrowIfDisposed()
    => ObjectDisposedException.ThrowIf(_disposed, this);
}
