#pragma warning disable CS1591

namespace Compression.Registry;

[Flags]
public enum FilesystemDriverReadinessLayer : ulong {
  None = 0,
  ImageValidation = 1UL << 0,
  Namespace = 1UL << 1,
  SessionStableNodeIds = 1UL << 2,
  NativeStableNodeIds = 1UL << 3,
  ReadData = 1UL << 4,
  RandomAccessRead = 1UL << 5,
  AllocationMap = 1UL << 6,
  WriteData = 1UL << 7,
  Truncate = 1UL << 8,
  NamespaceMutation = 1UL << 9,
  MetadataMutation = 1UL << 10,
  Links = 1UL << 11,
  Flush = 1UL << 12,
  DurabilityModel = 1UL << 13,
  Recovery = 1UL << 14,
  Concurrency = 1UL << 15,
  ValidationCorpus = 1UL << 16,
}

public enum FilesystemDriverTarget {
  ReadOnly,
  ReadWrite,
}

public sealed record FilesystemDriverReadinessReport(
  string FormatId,
  FilesystemDriverTarget Target,
  FilesystemDriverReadinessLayer AvailableLayers,
  FilesystemDriverReadinessLayer RequiredLayers,
  bool Derivable,
  bool UsesNativeProvider,
  IReadOnlyList<string> Blockers
);

/// <summary>
/// Optional filesystem-specific readiness description. The generic derivation
/// layer supplies a conservative report when a descriptor does not implement
/// this interface; native implementations can use it to explain exactly which
/// on-disk semantics still block a complete mounted driver.
/// </summary>
public interface IFilesystemDriverReadinessProvider {
  FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(
    Stream image,
    FilesystemDriverTarget target);
}

/// <summary>
/// Common entry point for filesystem frontends. Native filesystem providers are
/// always preferred. A descriptor that only exposes the normalized archive
/// listing/open-entry surface still gets a real read-only filesystem session:
/// hierarchy is reconstructed, node ids remain stable for the lifetime of the
/// mount, symlinks are represented, and file handles use positional reads.
///
/// The fallback is deliberately read-only. It never turns archive-level
/// rebuild/Add/Remove support into mounted write support. This makes every
/// filesystem parser usable by FUSE/Dokany/WinFsp-style frontends immediately,
/// while leaving a precise upgrade path to native allocation and mutation code.
/// </summary>
public static class FilesystemDriverDerivation {
  private const FilesystemDriverReadinessLayer ReadOnlyRequired =
    FilesystemDriverReadinessLayer.ImageValidation |
    FilesystemDriverReadinessLayer.Namespace |
    FilesystemDriverReadinessLayer.SessionStableNodeIds |
    FilesystemDriverReadinessLayer.ReadData |
    FilesystemDriverReadinessLayer.RandomAccessRead;

  private const FilesystemDriverReadinessLayer ReadWriteRequired =
    ReadOnlyRequired |
    FilesystemDriverReadinessLayer.AllocationMap |
    FilesystemDriverReadinessLayer.WriteData |
    FilesystemDriverReadinessLayer.Truncate |
    FilesystemDriverReadinessLayer.NamespaceMutation |
    FilesystemDriverReadinessLayer.Flush |
    FilesystemDriverReadinessLayer.DurabilityModel |
    FilesystemDriverReadinessLayer.Concurrency;

  public static FilesystemDriverProfile Probe(
      IFormatDescriptor descriptor,
      Stream image,
      string? password = null) {
    ArgumentNullException.ThrowIfNull(descriptor);
    ArgumentNullException.ThrowIfNull(image);

    if (descriptor is IFilesystemDriverProvider native)
      return native.ProbeFilesystem(image);

    if (descriptor is not IArchiveFormatOperations archive)
      return new FilesystemDriverProfile(
        descriptor.Id,
        "no filesystem projection",
        FilesystemDriverCapabilities.None,
        FilesystemMutationModel.None,
        CanMount: false,
        CanMountWritable: false,
        ["Descriptor exposes neither IFilesystemDriverProvider nor IArchiveFormatOperations."]);

    try {
      using var snapshot = DerivedFilesystemSnapshot.Capture(image);
      using var probe = snapshot.OpenRead();
      _ = archive.List(probe, password);
      return DerivedReadOnlyProfile(descriptor.Id);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException) {
      return new FilesystemDriverProfile(
        descriptor.Id,
        "archive-view probe failed",
        FilesystemDriverCapabilities.None,
        FilesystemMutationModel.None,
        CanMount: false,
        CanMountWritable: false,
        [$"Filesystem projection could not enumerate this image: {FirstLine(e.Message)}"]);
    }
  }

  public static IFilesystemSession Open(
      IFormatDescriptor descriptor,
      Stream image,
      FilesystemOpenOptions options,
      string? password = null) {
    ArgumentNullException.ThrowIfNull(descriptor);
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);

    if (descriptor is IFilesystemDriverProvider native)
      return native.OpenFilesystem(image, options);

    if (!options.ReadOnly)
      throw new NotSupportedException(
        $"{descriptor.Id} has no native writable filesystem provider. " +
        "Archive-level rebuild/modify support is not a mount-grade write path.");

    if (descriptor is not IArchiveFormatOperations archive)
      throw new NotSupportedException(
        $"{descriptor.Id} exposes no filesystem-driver provider and no list/open-entry projection.");

    return new DerivedReadOnlyFilesystemSession(descriptor, archive, image, password);
  }

  public static FilesystemDriverReadinessReport Assess(
      IFormatDescriptor descriptor,
      Stream image,
      FilesystemDriverTarget target,
      string? password = null) {
    ArgumentNullException.ThrowIfNull(descriptor);
    ArgumentNullException.ThrowIfNull(image);

    if (descriptor is IFilesystemDriverReadinessProvider specific)
      return specific.DescribeFilesystemDriverReadiness(image, target);

    var required = target == FilesystemDriverTarget.ReadOnly ? ReadOnlyRequired : ReadWriteRequired;
    var blockers = new List<string>();
    FilesystemDriverReadinessLayer available = FilesystemDriverReadinessLayer.None;
    var usesNative = descriptor is IFilesystemDriverProvider;

    if (usesNative) {
      var profile = ((IFilesystemDriverProvider)descriptor).ProbeFilesystem(image);
      if (profile.CanMount) {
        available |= FilesystemDriverReadinessLayer.ImageValidation |
                     FilesystemDriverReadinessLayer.Namespace |
                     FilesystemDriverReadinessLayer.SessionStableNodeIds;
        available |= LayersFor(profile.Capabilities);
      }
      if (profile.MutationModel != FilesystemMutationModel.None &&
          profile.MutationModel != FilesystemMutationModel.WholeImageRebuild)
        available |= FilesystemDriverReadinessLayer.DurabilityModel;
      blockers.AddRange(profile.Limitations);
    } else if (descriptor is IArchiveFormatOperations archive) {
      try {
        using var snapshot = DerivedFilesystemSnapshot.Capture(image);
        using var source = snapshot.OpenRead();
        _ = archive.List(source, password);
        available |= ReadOnlyRequired;
        blockers.Add("Node ids are stable only within the derived session; native on-disk object identity is not exposed yet.");
        blockers.Add("Allocation/extents are not exposed through the generic archive projection.");
      } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException) {
        blockers.Add($"Current parser cannot enumerate this image: {FirstLine(e.Message)}");
      }
    } else {
      blockers.Add("Descriptor does not expose IArchiveFormatOperations or a native filesystem provider.");
    }

    if (target == FilesystemDriverTarget.ReadWrite && (available & ReadWriteRequired) != ReadWriteRequired) {
      if ((available & FilesystemDriverReadinessLayer.AllocationMap) == 0)
        blockers.Add("Expose native allocation/free-space ownership instead of inferring it from extracted files.");
      if ((available & FilesystemDriverReadinessLayer.WriteData) == 0)
        blockers.Add("Implement bounded native file-data writes through the filesystem allocator.");
      if ((available & FilesystemDriverReadinessLayer.NamespaceMutation) == 0)
        blockers.Add("Implement native create/unlink/rename namespace mutation.");
      if ((available & FilesystemDriverReadinessLayer.DurabilityModel) == 0)
        blockers.Add("Model the filesystem's real write-ordering/journal/CoW durability boundary.");
      if ((available & FilesystemDriverReadinessLayer.Concurrency) == 0)
        blockers.Add("Define handle/cache/locking behavior for concurrent frontend requests.");
    }

    var derivable = (available & required) == required;
    return new FilesystemDriverReadinessReport(
      descriptor.Id,
      target,
      available,
      required,
      derivable,
      usesNative,
      blockers.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static FilesystemDriverProfile DerivedReadOnlyProfile(string formatId)
    => new(
      formatId,
      "derived read-only archive view",
      FilesystemDriverCapabilities.EnumerateDirectories |
      FilesystemDriverCapabilities.ReadData |
      FilesystemDriverCapabilities.RandomAccess |
      FilesystemDriverCapabilities.StableNodeIds |
      FilesystemDriverCapabilities.CasePreservingNames,
      FilesystemMutationModel.None,
      CanMount: true,
      CanMountWritable: false,
      [
        "Read-only compatibility projection over List/OpenEntry; allocation metadata is not exposed.",
        "Node ids are deterministic and stable for this session, not claimed to be native inode/object identifiers.",
      ]);

  private static FilesystemDriverReadinessLayer LayersFor(FilesystemDriverCapabilities capabilities) {
    var result = FilesystemDriverReadinessLayer.None;
    if ((capabilities & FilesystemDriverCapabilities.EnumerateDirectories) != 0)
      result |= FilesystemDriverReadinessLayer.Namespace;
    if ((capabilities & FilesystemDriverCapabilities.StableNodeIds) != 0)
      result |= FilesystemDriverReadinessLayer.SessionStableNodeIds |
                FilesystemDriverReadinessLayer.NativeStableNodeIds;
    if ((capabilities & FilesystemDriverCapabilities.ReadData) != 0)
      result |= FilesystemDriverReadinessLayer.ReadData;
    if ((capabilities & FilesystemDriverCapabilities.RandomAccess) != 0)
      result |= FilesystemDriverReadinessLayer.RandomAccessRead;
    if ((capabilities & FilesystemDriverCapabilities.WriteData) != 0)
      result |= FilesystemDriverReadinessLayer.WriteData;
    if ((capabilities & FilesystemDriverCapabilities.Truncate) != 0)
      result |= FilesystemDriverReadinessLayer.Truncate;
    if ((capabilities & (FilesystemDriverCapabilities.CreateFile |
                         FilesystemDriverCapabilities.DeleteFile |
                         FilesystemDriverCapabilities.CreateDirectory |
                         FilesystemDriverCapabilities.RemoveDirectory |
                         FilesystemDriverCapabilities.Rename)) != 0)
      result |= FilesystemDriverReadinessLayer.NamespaceMutation;
    if ((capabilities & FilesystemDriverCapabilities.SetMetadata) != 0)
      result |= FilesystemDriverReadinessLayer.MetadataMutation;
    if ((capabilities & (FilesystemDriverCapabilities.HardLinks |
                         FilesystemDriverCapabilities.SymbolicLinks)) != 0)
      result |= FilesystemDriverReadinessLayer.Links;
    if ((capabilities & FilesystemDriverCapabilities.Flush) != 0)
      result |= FilesystemDriverReadinessLayer.Flush;
    return result;
  }

  private static string FirstLine(string message) {
    var index = message.IndexOfAny(['\r', '\n']);
    return index < 0 ? message : message[..index];
  }
}

internal sealed class DerivedReadOnlyFilesystemSession : IFilesystemSession {
  private readonly IArchiveFormatOperations _operations;
  private readonly DerivedFilesystemSnapshot _snapshot;
  private readonly string? _password;
  private readonly Dictionary<FilesystemNodeId, Node> _nodes = [];
  private readonly Dictionary<FilesystemNodeId, List<Node>> _children = [];
  private bool _disposed;

  private sealed class Node {
    public required FilesystemNodeId Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required FilesystemNodeId Parent { get; init; }
    public required FilesystemNodeKind Kind { get; set; }
    public ArchiveEntryInfo? Entry { get; set; }
  }

  public DerivedReadOnlyFilesystemSession(
      IFormatDescriptor descriptor,
      IArchiveFormatOperations operations,
      Stream image,
      string? password) {
    _operations = operations;
    _password = password;
    _snapshot = DerivedFilesystemSnapshot.Capture(image);
    Profile = new FilesystemDriverProfile(
      descriptor.Id,
      "derived read-only archive view",
      FilesystemDriverCapabilities.EnumerateDirectories |
      FilesystemDriverCapabilities.ReadData |
      FilesystemDriverCapabilities.RandomAccess |
      FilesystemDriverCapabilities.StableNodeIds |
      FilesystemDriverCapabilities.SymbolicLinks |
      FilesystemDriverCapabilities.CasePreservingNames,
      FilesystemMutationModel.None,
      CanMount: true,
      CanMountWritable: false,
      [
        "Compatibility filesystem projection; native allocation/extents are not exposed.",
        "Writes require a native IFilesystemDriverProvider and are never emulated by rebuilding the image.",
      ]);

    BuildNamespace();
  }

  public FilesystemDriverProfile Profile { get; }
  public FilesystemNodeId RootNodeId { get; } = new(1, 1);

  public FilesystemNodeInfo Stat(FilesystemNodeId nodeId) {
    ThrowIfDisposed();
    var node = RequireNode(nodeId);
    var entry = node.Entry;
    var logical = node.Kind == FilesystemNodeKind.Directory ? 0 : Math.Max(0, entry?.OriginalSize ?? 0);
    var allocated = node.Kind == FilesystemNodeKind.Directory
      ? 0
      : entry?.CompressedSize is >= 0 ? entry.CompressedSize : logical;
    return new FilesystemNodeInfo(
      node.Id,
      node.Kind,
      logical,
      Math.Max(0, allocated),
      LinkCount: 1,
      Modified: ToOffset(entry?.LastModified));
  }

  public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name) {
    ArgumentNullException.ThrowIfNull(name);
    ThrowIfDisposed();
    var parent = RequireNode(parentDirectory);
    if (parent.Kind != FilesystemNodeKind.Directory)
      throw new DirectoryNotFoundException(parent.Path);
    if (!_children.TryGetValue(parentDirectory, out var children)) return null;

    var exact = children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.Ordinal));
    if (exact != null) return exact.Id;
    var folded = children.Where(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
    return folded.Length == 1 ? folded[0].Id : null;
  }

  public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory) {
    ThrowIfDisposed();
    var parent = RequireNode(directory);
    if (parent.Kind != FilesystemNodeKind.Directory)
      throw new DirectoryNotFoundException(parent.Path);
    return (_children.TryGetValue(directory, out var children) ? children : [])
      .OrderBy(child => child.Name, StringComparer.Ordinal)
      .Select(child => new FilesystemDirectoryEntry(child.Name, child.Id, child.Kind))
      .ToArray();
  }

  public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
    ThrowIfDisposed();
    if (access != FileAccess.Read)
      throw new NotSupportedException("The derived filesystem projection is read-only.");
    var node = RequireNode(nodeId);
    if (node.Kind != FilesystemNodeKind.RegularFile)
      throw new UnauthorizedAccessException($"'{node.Path}' is not a regular file.");
    if (node.Entry == null)
      throw new InvalidDataException($"Derived node '{node.Path}' has no backing archive entry.");

    var backing = node.Entry;
    return SpoolingReadOnlyFileHandle.Create(
      node.Id,
      Math.Max(0, backing.OriginalSize),
      output => {
        using var archive = _snapshot.OpenRead();
        using var entry = _operations.OpenEntry(archive, backing.Name, _password);
        entry.CopyTo(output);
      });
  }

  public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name)
    => throw ReadOnly();
  public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name)
    => throw ReadOnly();
  public void DeleteFile(FilesystemNodeId parentDirectory, string name)
    => throw ReadOnly();
  public void RemoveDirectory(FilesystemNodeId parentDirectory, string name)
    => throw ReadOnly();
  public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace)
    => throw ReadOnly();
  public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName)
    => throw ReadOnly();
  public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target)
    => throw ReadOnly();

  public string ReadSymbolicLink(FilesystemNodeId nodeId) {
    ThrowIfDisposed();
    var node = RequireNode(nodeId);
    if (node.Kind != FilesystemNodeKind.SymbolicLink || node.Entry?.LinkTarget == null)
      throw new InvalidOperationException($"'{node.Path}' is not a symbolic link with a decoded target.");
    return node.Entry.LinkTarget;
  }

  public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch)
    => throw ReadOnly();

  public void Flush() {
    ThrowIfDisposed();
  }

  public IFilesystemTransaction BeginTransaction()
    => throw new NotSupportedException("The derived read-only filesystem projection has no transactions.");

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    _snapshot.Dispose();
  }

  private void BuildNamespace() {
    var root = new Node {
      Id = RootNodeId,
      Name = string.Empty,
      Path = string.Empty,
      Parent = default,
      Kind = FilesystemNodeKind.Directory,
    };
    _nodes[root.Id] = root;
    _children[root.Id] = [];

    List<ArchiveEntryInfo> entries;
    using (var source = _snapshot.OpenRead())
      entries = _operations.List(source, _password);

    var normalized = entries
      .Select(entry => (Entry: entry, Path: NormalizePath(entry.Name)))
      .Where(item => item.Path.Length > 0)
      .OrderBy(item => item.Path, StringComparer.Ordinal)
      .ToArray();

    var byPath = new Dictionary<string, Node>(StringComparer.Ordinal) { [string.Empty] = root };
    ulong nextId = 2;

    foreach (var item in normalized) {
      var segments = item.Path.Split('/');
      var currentPath = string.Empty;
      var parent = root;
      for (var i = 0; i < segments.Length; ++i) {
        var segment = segments[i];
        currentPath = currentPath.Length == 0 ? segment : currentPath + "/" + segment;
        var isLeaf = i == segments.Length - 1;
        if (!byPath.TryGetValue(currentPath, out var node)) {
          node = new Node {
            Id = new FilesystemNodeId(nextId++, 1),
            Name = segment,
            Path = currentPath,
            Parent = parent.Id,
            Kind = isLeaf ? KindFor(item.Entry) : FilesystemNodeKind.Directory,
            Entry = isLeaf ? item.Entry : null,
          };
          byPath[currentPath] = node;
          _nodes[node.Id] = node;
          _children.TryAdd(node.Id, []);
          _children[parent.Id].Add(node);
        } else if (isLeaf) {
          if (node.Entry != null)
            throw new InvalidDataException($"Filesystem projection contains duplicate path '{currentPath}'.");
          if (node.Kind != FilesystemNodeKind.Directory && item.Entry.IsDirectory)
            throw new InvalidDataException($"Filesystem projection path '{currentPath}' changes node kind.");
          node.Entry = item.Entry;
          node.Kind = KindFor(item.Entry);
        }
        parent = node;
      }
    }
  }

  private Node RequireNode(FilesystemNodeId nodeId)
    => _nodes.TryGetValue(nodeId, out var node)
      ? node
      : throw new FileNotFoundException($"Filesystem node {nodeId.Value}:{nodeId.Generation} is not present in this session.");

  private static FilesystemNodeKind KindFor(ArchiveEntryInfo entry)
    => entry.IsDirectory ? FilesystemNodeKind.Directory
      : entry.IsSymlink ? FilesystemNodeKind.SymbolicLink
      : FilesystemNodeKind.RegularFile;

  private static string NormalizePath(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var result = new List<string>();
    foreach (var raw in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      if (raw == ".") continue;
      if (raw == "..")
        throw new InvalidDataException($"Filesystem entry '{path}' escapes its root with '..'.");
      result.Add(raw);
    }
    return string.Join('/', result);
  }

  private static DateTimeOffset? ToOffset(DateTime? value) {
    if (value == null) return null;
    return value.Value.Kind switch {
      DateTimeKind.Utc => new DateTimeOffset(value.Value, TimeSpan.Zero),
      DateTimeKind.Local => new DateTimeOffset(value.Value),
      _ => new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), TimeSpan.Zero),
    };
  }

  private static NotSupportedException ReadOnly()
    => new("The derived filesystem projection is read-only; use a native filesystem provider for mounted writes.");

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class DerivedReadOnlyFileHandle : IFilesystemFileHandle {
  private readonly byte[] _data;
  private bool _disposed;

  public DerivedReadOnlyFileHandle(FilesystemNodeId nodeId, byte[] data) {
    NodeId = nodeId;
    _data = data;
  }

  public FilesystemNodeId NodeId { get; }
  public long Length {
    get {
      ThrowIfDisposed();
      return _data.LongLength;
    }
  }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (offset >= _data.LongLength || destination.Length == 0) return 0;
    var count = checked((int)Math.Min(destination.Length, _data.LongLength - offset));
    _data.AsSpan(checked((int)offset), count).CopyTo(destination);
    return count;
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("The derived filesystem projection is read-only.");
  public void SetLength(long length)
    => throw new NotSupportedException("The derived filesystem projection is read-only.");
  public void Flush() => ThrowIfDisposed();
  public void Dispose() => _disposed = true;
  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class DerivedFilesystemSnapshot : IDisposable {
  private const long MemoryThreshold = 16L * 1024 * 1024;
  private readonly byte[]? _memory;
  private readonly string? _temporaryPath;
  private bool _disposed;

  private DerivedFilesystemSnapshot(byte[] memory) => _memory = memory;
  private DerivedFilesystemSnapshot(string temporaryPath) => _temporaryPath = temporaryPath;

  public static DerivedFilesystemSnapshot Capture(Stream source) {
    ArgumentNullException.ThrowIfNull(source);
    if (!source.CanRead) throw new ArgumentException("Filesystem derivation requires a readable image.", nameof(source));

    var originalPosition = source.CanSeek ? source.Position : 0;
    try {
      if (source.CanSeek) source.Position = 0;
      if (source.CanSeek && source.Length <= MemoryThreshold) {
        using var memory = new MemoryStream(checked((int)source.Length));
        source.CopyTo(memory);
        return new DerivedFilesystemSnapshot(memory.ToArray());
      }

      var path = Path.Combine(Path.GetTempPath(), "cwb_fs_" + Guid.NewGuid().ToString("N") + ".img");
      try {
        using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                   64 * 1024, FileOptions.SequentialScan))
          source.CopyTo(target);
        return new DerivedFilesystemSnapshot(path);
      } catch {
        try { File.Delete(path); } catch { }
        throw;
      }
    } finally {
      if (source.CanSeek) source.Position = originalPosition;
    }
  }

  public Stream OpenRead() {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_memory != null) return new MemoryStream(_memory, writable: false);
    return new FileStream(_temporaryPath!, FileMode.Open, FileAccess.Read,
      FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
  }

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    if (_temporaryPath != null) {
      try { File.Delete(_temporaryPath); } catch { }
    }
  }
}
