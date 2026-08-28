#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Fat;

/// <summary>
/// Native FAT12/16/32 driver sidecar. The current milestone is a validated,
/// positional read-only mount over the real FAT chains; writable mounting stays
/// fail-closed until the existing offline mutators are converted to bounded
/// block-device operations with complete directory/durability semantics.
/// </summary>
public sealed class FatFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "Fat";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var geometry = FatDriverGeometry.Parse(image);
      using var reader = new FatReader(image, leaveOpen: true);
      var entries = reader.Entries.ToArray();
      ValidateAllocationGraph(image, geometry, entries);

      return new FilesystemDriverProfile(
        FormatId,
        $"FAT{reader.FatType} native chain reader",
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.Direct,
        CanMount: true,
        CanMountWritable: false,
        [
          "Native positional reads use FAT chains directly; no whole-file extraction is required.",
          "Node ids are stable for the mounted session. FAT has no inode number; durable identity across remount is not claimed.",
          "Writable mounting remains disabled until nested namespace mutation, large-volume block writes, dirty-bit/write ordering and handle-safe truncate/rename are complete.",
        ]);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        FormatId,
        "unsupported or damaged FAT profile",
        FilesystemDriverCapabilities.None,
        FilesystemMutationModel.None,
        CanMount: false,
        CanMountWritable: false,
        [FirstLine(e.Message)]);
    } finally {
      if (image.CanSeek) image.Position = original;
    }
  }

  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    if (!options.ReadOnly)
      throw new NotSupportedException(
        "FAT mounted writes are not yet enabled: archive Add/Remove is not a substitute for complete handle-safe filesystem mutation.");

    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("FAT image is not mountable: " + string.Join("; ", profile.Limitations));
    return new FatReadOnlyFilesystemSession(image, profile, options.LeaveOpen);
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
      FilesystemDriverTarget target) {
    var profile = ProbeFilesystem(image);
    var readRequired =
      FilesystemDriverReadinessLayer.ImageValidation |
      FilesystemDriverReadinessLayer.Namespace |
      FilesystemDriverReadinessLayer.SessionStableNodeIds |
      FilesystemDriverReadinessLayer.ReadData |
      FilesystemDriverReadinessLayer.RandomAccessRead;
    var writeRequired = readRequired |
      FilesystemDriverReadinessLayer.AllocationMap |
      FilesystemDriverReadinessLayer.WriteData |
      FilesystemDriverReadinessLayer.Truncate |
      FilesystemDriverReadinessLayer.NamespaceMutation |
      FilesystemDriverReadinessLayer.Flush |
      FilesystemDriverReadinessLayer.DurabilityModel |
      FilesystemDriverReadinessLayer.Concurrency;

    var available = profile.CanMount
      ? readRequired |
        FilesystemDriverReadinessLayer.AllocationMap |
        FilesystemDriverReadinessLayer.DurabilityModel
      : FilesystemDriverReadinessLayer.None;
    var blockers = new List<string>(profile.Limitations);
    if (target == FilesystemDriverTarget.ReadWrite) {
      blockers.Add("Replace byte[]-based FatModifier/FatRemover mutation with bounded IRandomAccessBlockDevice updates so multi-gigabyte FAT32 volumes do not require materialization.");
      blockers.Add("Implement create/mkdir/unlink/rmdir in arbitrary directories, including directory-chain growth and LFN slot allocation.");
      blockers.Add("Implement positional handle writes and truncate with cluster allocate/free, all FAT-copy updates and FAT32 FSInfo maintenance.");
      blockers.Add("Implement rename with LFN/8.3 alias regeneration while open handles retain session object identity even when directory slots relocate.");
      blockers.Add("Implement FAT clean/dirty bit and write-ordering rules; TFAT-marked volumes require their transaction-specific commit protocol rather than ordinary FAT ordering.");
      blockers.Add("Add mounted locking/cache invalidation plus fault-injection tests around FAT, directory and FSInfo publication phases.");
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

  private static void ValidateAllocationGraph(
      Stream image,
      FatDriverGeometry geometry,
      IReadOnlyList<FatEntry> entries) {
    var owners = new Dictionary<int, string>();
    foreach (var entry in entries) {
      if (entry.StartCluster < 2) {
        if (!entry.IsDirectory && entry.Size > 0)
          throw new InvalidDataException($"FAT file '{entry.Name}' has data but no start cluster.");
        continue;
      }

      var chain = geometry.ReadChain(image, entry.StartCluster, entry.Name);
      if (!entry.IsDirectory) {
        var needed = entry.Size == 0 ? 0L : (entry.Size + geometry.ClusterSize - 1) / geometry.ClusterSize;
        if (chain.Count < needed)
          throw new InvalidDataException(
            $"FAT file '{entry.Name}' needs {needed} cluster(s) for {entry.Size} bytes but its chain has {chain.Count}.");
      }

      foreach (var cluster in chain) {
        if (owners.TryGetValue(cluster, out var previous))
          throw new InvalidDataException(
            $"FAT cluster {cluster} is cross-linked by '{previous}' and '{entry.Name}'.");
        owners[cluster] = entry.Name;
      }
    }
  }

  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}

internal sealed class FatReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public FatReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("FAT mounted reads require a readable, seekable image.", nameof(image));
    _image = image;
    _leaveOpen = leaveOpen;

    var geometry = FatDriverGeometry.Parse(image);
    using var reader = new FatReader(image, leaveOpen: true);
    var records = reader.Entries.ToArray();
    var root = new FilesystemNodeId(1, 1);
    var nodes = BuildNodes(records, geometry, root);
    _namespace = new ReadOnlyFilesystemSnapshotSession(profile, root, nodes);
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
    if (_disposed) return;
    _disposed = true;
    _namespace.Dispose();
    if (!_leaveOpen) _image.Dispose();
  }

  private IReadOnlyList<FilesystemSnapshotNode> BuildNodes(
      IReadOnlyList<FatEntry> entries,
      FatDriverGeometry geometry,
      FilesystemNodeId rootId) {
    var result = new List<FilesystemSnapshotNode>(entries.Count + 1) {
      new(rootId, default, string.Empty, FilesystemNodeKind.Directory, 0, 0),
    };
    var byPath = new Dictionary<string, FilesystemNodeId>(StringComparer.OrdinalIgnoreCase) {
      [string.Empty] = rootId,
    };
    ulong next = 2;

    foreach (var entry in entries.OrderBy(e => Depth(e.Name)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)) {
      var path = Normalize(entry.Name);
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var name = slash < 0 ? path : path[(slash + 1)..];
      if (!byPath.TryGetValue(parentPath, out var parent))
        throw new InvalidDataException($"FAT entry '{path}' has no decoded parent directory '{parentPath}'.");

      var nodeId = new FilesystemNodeId(next++, 1);
      byPath[path] = nodeId;
      var captured = entry;
      var allocated = entry.IsDirectory || entry.StartCluster < 2
        ? 0L
        : checked((long)geometry.ReadChain(_image, entry.StartCluster, entry.Name).Count * geometry.ClusterSize);
      result.Add(new FilesystemSnapshotNode(
        nodeId,
        parent,
        name,
        entry.IsDirectory ? FilesystemNodeKind.Directory : FilesystemNodeKind.RegularFile,
        entry.Size,
        allocated,
        Modified: ToOffset(entry.LastModified),
        OpenReadHandle: entry.IsDirectory ? null : () => new FatPositionalFileHandle(
          nodeId, _image, _ioGate, geometry, captured)));
    }
    return result;
  }

  private static int Depth(string path) => Normalize(path).Count(c => c == '/');
  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
  private static DateTimeOffset? ToOffset(DateTime? value) {
    if (value == null) return null;
    return value.Value.Kind == DateTimeKind.Local
      ? new DateTimeOffset(value.Value)
      : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), TimeSpan.Zero);
  }
}

internal sealed class FatPositionalFileHandle : IFilesystemFileHandle {
  private readonly Stream _image;
  private readonly object _ioGate;
  private readonly FatDriverGeometry _geometry;
  private readonly FatEntry _entry;
  private readonly IReadOnlyList<int> _chain;
  private bool _disposed;

  public FatPositionalFileHandle(
      FilesystemNodeId nodeId,
      Stream image,
      object ioGate,
      FatDriverGeometry geometry,
      FatEntry entry) {
    NodeId = nodeId;
    _image = image;
    _ioGate = ioGate;
    _geometry = geometry;
    _entry = entry;
    _chain = entry.StartCluster < 2
      ? []
      : geometry.ReadChain(image, entry.StartCluster, entry.Name);
  }

  public FilesystemNodeId NodeId { get; }
  public long Length {
    get {
      ThrowIfDisposed();
      return Math.Max(0, _entry.Size);
    }
  }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (destination.Length == 0 || offset >= Length) return 0;

    var remaining = checked((int)Math.Min(destination.Length, Length - offset));
    var written = 0;
    var logical = offset;
    while (remaining > 0) {
      var chainIndex = checked((int)(logical / _geometry.ClusterSize));
      if (chainIndex < 0 || chainIndex >= _chain.Count)
        throw new InvalidDataException($"FAT chain for '{_entry.Name}' ends before logical offset {logical}.");
      var within = checked((int)(logical % _geometry.ClusterSize));
      var take = Math.Min(remaining, _geometry.ClusterSize - within);
      var physical = checked(_geometry.ClusterOffset(_chain[chainIndex]) + within);
      lock (_ioGate) {
        if (physical < 0 || physical > _image.Length - take)
          throw new InvalidDataException($"FAT cluster for '{_entry.Name}' lies outside the image.");
        _image.Position = physical;
        _image.ReadExactly(destination.Slice(written, take));
      }
      logical += take;
      written += take;
      remaining -= take;
    }
    return written;
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("The FAT filesystem session is read-only.");
  public void SetLength(long length)
    => throw new NotSupportedException("The FAT filesystem session is read-only.");
  public void Flush() => ThrowIfDisposed();
  public void Dispose() => _disposed = true;
  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal readonly record struct FatDriverGeometry(
  int BytesPerSector,
  int SectorsPerCluster,
  int ReservedSectors,
  int FatCount,
  long TotalSectors,
  int FatSize,
  long FirstDataSector,
  long TotalDataClusters,
  int FatType,
  int RootCluster) {

  public int ClusterSize => checked(BytesPerSector * SectorsPerCluster);
  public long DataLength => checked(TotalSectors * BytesPerSector);

  public static FatDriverGeometry Parse(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("FAT driver probing requires a readable, seekable stream.", nameof(image));
    if (image.Length < 512) throw new InvalidDataException("FAT image is shorter than one boot sector.");

    Span<byte> boot = stackalloc byte[512];
    var original = image.Position;
    try {
      image.Position = 0;
      image.ReadExactly(boot);
    } finally {
      image.Position = original;
    }

    if (boot[0] is not (0xEB or 0xE9 or 0x00))
      throw new InvalidDataException("FAT boot jump is invalid.");
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..13]);
    if (bps is not (512 or 1024 or 2048 or 4096))
      throw new InvalidDataException($"FAT bytes-per-sector value {bps} is unsupported.");
    var spc = boot[13];
    if (spc == 0 || (spc & (spc - 1)) != 0 || spc > 128)
      throw new InvalidDataException($"FAT sectors-per-cluster value {spc} is invalid.");
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..16]);
    if (reserved == 0) throw new InvalidDataException("FAT reserved-sector count is zero.");
    var fatCount = boot[16];
    if (fatCount is 0 or > 4) throw new InvalidDataException($"FAT copy count {fatCount} is invalid.");
    var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..19]);
    var total16 = BinaryPrimitives.ReadUInt16LittleEndian(boot[19..21]);
    var total = total16 != 0 ? total16 : BinaryPrimitives.ReadUInt32LittleEndian(boot[32..36]);
    if (total == 0) throw new InvalidDataException("FAT total sector count is zero.");
    var fat16 = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..24]);
    var fatSize = fat16 != 0 ? fat16 : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(boot[36..40]));
    if (fatSize <= 0) throw new InvalidDataException("FAT table size is zero.");

    var rootSectors = ((long)rootEntries * 32 + bps - 1) / bps;
    var firstData = checked((long)reserved + (long)fatCount * fatSize + rootSectors);
    if (firstData >= total) throw new InvalidDataException("FAT data region starts outside the volume.");
    var dataClusters = (total - firstData) / spc;
    var fatType = fat16 == 0 ? 32 : dataClusters < 4085 ? 12 : dataClusters < 65525 ? 16 : 32;
    var rootCluster = fatType == 32 ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(boot[44..48])) : 0;
    if (fatType == 32 && rootCluster < 2) throw new InvalidDataException("FAT32 root cluster is invalid.");
    var dataLength = checked((long)total * bps);
    if (image.Length < dataLength)
      throw new InvalidDataException($"FAT volume declares {dataLength:N0} bytes but image has only {image.Length:N0}.");

    return new FatDriverGeometry(
      bps, spc, reserved, fatCount, total, fatSize,
      firstData, dataClusters, fatType, rootCluster);
  }

  public long ClusterOffset(int cluster) {
    if (cluster < 2 || cluster >= TotalDataClusters + 2)
      throw new InvalidDataException($"FAT cluster {cluster} lies outside the data-cluster range.");
    return checked((FirstDataSector + (long)(cluster - 2) * SectorsPerCluster) * BytesPerSector);
  }

  public IReadOnlyList<int> ReadChain(Stream image, int startCluster, string owner) {
    if (startCluster < 2) return [];
    var result = new List<int>();
    var seen = new HashSet<int>();
    var cluster = startCluster;
    while (true) {
      if (cluster < 2 || cluster >= TotalDataClusters + 2)
        throw new InvalidDataException($"FAT chain for '{owner}' references out-of-range cluster {cluster}.");
      if (!seen.Add(cluster))
        throw new InvalidDataException($"FAT chain for '{owner}' contains a loop at cluster {cluster}.");
      result.Add(cluster);

      var next = ReadFatEntry(image, 0, cluster);
      for (var copy = 1; copy < FatCount; ++copy) {
        var mirror = ReadFatEntry(image, copy, cluster);
        if (mirror != next)
          throw new InvalidDataException(
            $"FAT copies disagree at cluster {cluster}: primary=0x{next:X}, copy {copy}=0x{mirror:X}.");
      }
      if (IsEndOfChain(next)) return result;
      if (IsBadOrReserved(next))
        throw new InvalidDataException($"FAT chain for '{owner}' terminates in reserved/bad value 0x{next:X}.");
      if (next == 0)
        throw new InvalidDataException($"FAT chain for '{owner}' reaches a free cluster after {cluster}.");
      cluster = next;
    }
  }

  private int ReadFatEntry(Stream image, int fatCopy, int cluster) {
    var fatStart = checked(((long)ReservedSectors + (long)fatCopy * FatSize) * BytesPerSector);
    Span<byte> bytes = stackalloc byte[4];
    var position = FatType switch {
      12 => fatStart + cluster + cluster / 2,
      16 => fatStart + (long)cluster * 2,
      _ => fatStart + (long)cluster * 4,
    };
    var needed = FatType == 12 || FatType == 16 ? 2 : 4;
    var original = image.Position;
    try {
      image.Position = position;
      image.ReadExactly(bytes[..needed]);
    } finally {
      image.Position = original;
    }

    if (FatType == 12) {
      var raw = BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
      return (cluster & 1) == 0 ? raw & 0x0FFF : raw >> 4;
    }
    if (FatType == 16) return BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
    return checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0x0FFFFFFF));
  }

  private bool IsEndOfChain(int value) => FatType switch {
    12 => value >= 0xFF8,
    16 => value >= 0xFFF8,
    _ => value >= 0x0FFFFFF8,
  };

  private bool IsBadOrReserved(int value) => FatType switch {
    12 => value is >= 0xFF0 and < 0xFF8,
    16 => value is >= 0xFFF0 and < 0xFFF8,
    _ => value is >= 0x0FFFFFF0 and < 0x0FFFFFF8,
  };
}
