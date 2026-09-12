#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Iso;

/// <summary>
/// Native ISO 9660 filesystem sidecar. The archive descriptor remains the
/// offline editor surface; this adapter gives mount backends a stable namespace
/// and positional file handles without extracting entries into temporary files
/// or byte arrays.
/// </summary>
public sealed class IsoFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  private const int LogicalBlockSize = 2048;

  public string FormatId => "Iso";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      return Unsupported("ISO mounted reads require a readable, seekable image.");

    var original = image.Position;
    try {
      using var reader = new IsoReader(image, leaveOpen: true);
      ValidateVolumeDescriptors(image);
      ValidateEntries(image, reader.Entries);

      return new FilesystemDriverProfile(
        FormatId,
        "ECMA-119 native file-section reader",
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,
        [
          "File handles read directly from decoded ECMA-119 file sections, including multi-extent files and per-section extended-attribute prefixes.",
          "Node ids are deterministic for the mounted session; ISO 9660 has no inode number and durable identity across remount is not claimed.",
          "Interleaved file sections fail closed until File Unit Size / Interleave Gap addressing is implemented.",
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

  public FilesystemDriverProfile ProbeFilesystem(IRandomAccessBlockDevice device) {
    ArgumentNullException.ThrowIfNull(device);
    using var stream = new BlockDeviceStream(device, leaveOpen: true);
    return ProbeFilesystem(stream);
  }

  public IFilesystemSession OpenFilesystem(IRandomAccessBlockDevice device, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(device);
    ArgumentNullException.ThrowIfNull(options);
    var stream = new BlockDeviceStream(device, leaveOpen: false);
    try {
      return OpenFilesystem(stream, options with { LeaveOpen = false });
    } catch {
      stream.Dispose();
      throw;
    }
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

  private static void ValidateVolumeDescriptors(Stream image) {
    Span<byte> header = stackalloc byte[6];
    Span<byte> rootRecord = stackalloc byte[18];
    var seen = false;

    for (var sector = 16; sector < 256; ++sector) {
      var offset = (long)sector * LogicalBlockSize;
      if (offset + LogicalBlockSize > image.Length)
        break;

      image.Position = offset;
      image.ReadExactly(header);
      if (header[0] == 0xFF)
        break;
      if (header[1] != 'C' || header[2] != 'D' || header[3] != '0' || header[4] != '0' || header[5] != '1')
        continue;
      if (header[0] is not (1 or 2))
        continue;

      image.Position = offset + 156;
      image.ReadExactly(rootRecord);
      if (rootRecord[0] < 34)
        throw new InvalidDataException("ISO root directory record is truncated.");
      var extendedAttributeBlocks = rootRecord[1];
      var extentLba = BinaryPrimitives.ReadUInt32LittleEndian(rootRecord[2..]);
      var length = BinaryPrimitives.ReadUInt32LittleEndian(rootRecord[10..]);
      var extent = checked(((long)extentLba + extendedAttributeBlocks) * LogicalBlockSize);
      if (extent < 0 || extent > image.Length || length > image.Length - extent)
        throw new InvalidDataException(
          $"ISO root directory data extent [{extent}, {extent + length}) lies outside the image.");

      seen = true;
    }

    if (!seen)
      throw new InvalidDataException("ISO image carries no complete volume descriptor.");
  }

  private static void ValidateEntries(Stream image, IReadOnlyList<IsoEntry> entries) {
    var paths = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in entries) {
      var path = Normalize(entry.Name);
      if (path.Length == 0)
        throw new InvalidDataException("ISO reader returned an empty filesystem entry path.");
      if (!paths.Add(path))
        throw new InvalidDataException($"ISO filesystem contains duplicate decoded path '{path}'.");
      if (entry.MountLimitation is { } limitation)
        throw new NotSupportedException($"ISO file '{path}' is outside the native mounted profile: {limitation}");
      if (entry.IsDirectory) {
        if (entry.DataOffset < 0 || entry.DataOffset >= image.Length)
          throw new InvalidDataException($"ISO directory '{path}' extent starts outside the image.");
        continue;
      }

      long logical = 0;
      foreach (var segment in IsoReader.Segments(entry)) {
        if (segment.LogicalOffset != logical)
          throw new InvalidDataException($"ISO file '{path}' has a discontinuous file-section map.");
        if (segment.Length < 0 || segment.PhysicalOffset < 0 ||
            segment.PhysicalOffset > image.Length || segment.Length > image.Length - segment.PhysicalOffset)
          throw new InvalidDataException(
            $"ISO file '{path}' section [{segment.PhysicalOffset}, {segment.PhysicalOffset + Math.Max(0, segment.Length)}) lies outside the image.");
        logical = checked(logical + segment.Length);
      }
      if (logical != entry.Size)
        throw new InvalidDataException($"ISO file '{path}' sections cover {logical} of {entry.Size} logical bytes.");
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

  internal static long AllocatedLength(IsoEntry entry) {
    long result = 0;
    foreach (var segment in IsoReader.Segments(entry)) {
      if (segment.Length <= 0) continue;
      result = checked(result + ((segment.Length + LogicalBlockSize - 1) / LogicalBlockSize) * LogicalBlockSize);
    }
    return result;
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
      var capturedLength = entry.Size;
      var capturedSegments = IsoReader.Segments(entry).ToArray();
      result.Add(new FilesystemSnapshotNode(
        nodeId,
        parent,
        name,
        entry.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile,
        entry.IsDirectory ? 0 : entry.Size,
        entry.IsDirectory ? 0 : IsoFilesystemDriverAdapter.AllocatedLength(entry),
        LinkCount: entry.IsDirectory ? 2U : 1U,
        Modified: ToOffset(entry.LastModified),
        OpenReadHandle: entry.IsDirectory
          ? null
          : () => new IsoPositionalFileHandle(nodeId, _image, _ioGate, capturedSegments, capturedLength)));
    }

    return result;
  }

  private void ValidateExtentBounds(IEnumerable<IsoEntry> entries) {
    foreach (var entry in entries) {
      if (entry.IsDirectory)
        continue;
      if (entry.MountLimitation is { } limitation)
        throw new NotSupportedException($"ISO file '{entry.Name}' is outside the mounted profile: {limitation}");
      foreach (var segment in IsoReader.Segments(entry))
        if (segment.Length < 0 || segment.PhysicalOffset < 0 ||
            segment.PhysicalOffset > _image.Length || segment.Length > _image.Length - segment.PhysicalOffset)
          throw new InvalidDataException($"ISO file '{entry.Name}' section lies outside the image.");
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
  IReadOnlyList<IsoDataSegment> segments,
  long length
) : IFilesystemFileHandle {
  private readonly Stream _image = image ?? throw new ArgumentNullException(nameof(image));
  private readonly object _ioGate = ioGate ?? throw new ArgumentNullException(nameof(ioGate));
  private readonly IReadOnlyList<IsoDataSegment> _segments = segments ?? throw new ArgumentNullException(nameof(segments));
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
    if (destination.IsEmpty || offset >= _length)
      return 0;

    var wanted = checked((int)Math.Min(destination.Length, _length - offset));
    var target = destination[..wanted];
    var copied = 0;
    var logicalEnd = checked(offset + wanted);

    lock (_ioGate) {
      foreach (var segment in _segments) {
        var segmentEnd = checked(segment.LogicalOffset + segment.Length);
        if (segmentEnd <= offset) continue;
        if (segment.LogicalOffset >= logicalEnd) break;

        var from = Math.Max(offset, segment.LogicalOffset);
        var to = Math.Min(logicalEnd, segmentEnd);
        var take = checked((int)(to - from));
        if (take <= 0) continue;
        var physical = checked(segment.PhysicalOffset + from - segment.LogicalOffset);
        if (physical < 0 || physical > _image.Length - take)
          throw new InvalidDataException("ISO file section ends outside the backing image.");
        _image.Position = physical;
        _image.ReadExactly(target.Slice(copied, take));
        copied += take;
      }
    }

    if (copied != wanted)
      throw new InvalidDataException($"ISO file sections supplied only {copied} of {wanted} requested bytes.");
    return copied;
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
