#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Ntfs;

/// <summary>
/// Native NTFS driver sidecar. Namespace identity is based on the MFT record
/// number rather than path text, so rename/unlink can later preserve open-handle
/// identity. The reader already decodes resident/non-resident $DATA, sparse
/// runs, LZNT1, reparse symlinks and INDEX_ALLOCATION directories. Mounted
/// writes remain fail-closed until $LogFile transactions/replay and the full
/// file-reference (MFT record + sequence number) are part of the mutable core.
/// </summary>
public sealed class NtfsFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "Ntfs";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var geometry = NtfsDriverGeometry.Parse(image);
      using var reader = new NtfsReader(image, leaveOpen: true);
      ValidateNamespace(reader.Entries, geometry);

      return new FilesystemDriverProfile(
        FormatId,
        "NTFS native MFT reader",
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.SymbolicLinks |
        FilesystemDriverCapabilities.SparseFiles |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,
        [
          "MFT record numbers provide path-independent object identity for the read-only mounted snapshot.",
          "The current namespace reader retains one preferred $FILE_NAME per MFT record; expose all $FILE_NAME attributes plus the MFT sequence number before claiming durable hard-link/file-reference identity.",
          "File data is decoded by the native NTFS reader; the transitional positional handle spools that decoded stream while a direct resident/data-run/LZNT1 positional handle is completed.",
          "$LogFile restart/replay and transactional publication are not implemented, so writable mounting remains disabled even though offline add/remove/block-move primitives exist.",
        ]);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        FormatId,
        "unsupported or damaged NTFS profile",
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
        "NTFS mounted writes require $LogFile transaction/replay semantics and complete mutable file-reference/index handling; offline archive mutation is not a mounted-write substitute.");

    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("NTFS image is not mountable: " + string.Join("; ", profile.Limitations));
    return new NtfsReadOnlyFilesystemSession(image, profile, options.LeaveOpen);
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
      FilesystemDriverReadinessLayer.NativeStableNodeIds |
      FilesystemDriverReadinessLayer.AllocationMap |
      FilesystemDriverReadinessLayer.WriteData |
      FilesystemDriverReadinessLayer.Truncate |
      FilesystemDriverReadinessLayer.NamespaceMutation |
      FilesystemDriverReadinessLayer.MetadataMutation |
      FilesystemDriverReadinessLayer.Links |
      FilesystemDriverReadinessLayer.Flush |
      FilesystemDriverReadinessLayer.DurabilityModel |
      FilesystemDriverReadinessLayer.Recovery |
      FilesystemDriverReadinessLayer.Concurrency;

    var available = profile.CanMount
      ? readRequired |
        FilesystemDriverReadinessLayer.AllocationMap |
        FilesystemDriverReadinessLayer.ValidationCorpus
      : FilesystemDriverReadinessLayer.None;
    var blockers = new List<string>(profile.Limitations);
    if (target == FilesystemDriverTarget.ReadWrite) {
      blockers.Add("Expose the MFT sequence number and every live $FILE_NAME attribute so file references and hard links survive rename/unlink/reuse correctly.");
      blockers.Add("Move resident/data-run/LZNT1 reads and writes behind direct positional handles; remove whole-file materialization from the mounted path.");
      blockers.Add("Implement arbitrary-directory create/unlink/mkdir/rmdir/rename/link with $INDEX_ROOT/$INDEX_ALLOCATION B+tree split/merge and correct namespace collation.");
      blockers.Add("Implement resident↔non-resident conversion, sparse/compressed run-list growth, truncate and $Bitmap allocation as bounded block-device transactions.");
      blockers.Add("Implement $LogFile restart areas, redo/undo records, transaction publication, replay/recovery and volume dirty/clean state before enabling writes.");
      blockers.Add("Complete security descriptors, ACLs, object IDs, quotas, EAs/ADS/reparse semantics and cache/locking behavior for concurrent kernel requests.");
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

  private static void ValidateNamespace(IReadOnlyList<NtfsEntry> entries, NtfsDriverGeometry geometry) {
    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in entries) {
      if (entry.MftRecord <= 15)
        throw new InvalidDataException($"NTFS user namespace unexpectedly exposed reserved MFT record {entry.MftRecord} as '{entry.Name}'.");
      var path = Normalize(entry.Name);
      if (path.Length == 0)
        throw new InvalidDataException("NTFS namespace exposed an empty path.");
      if (!paths.Add(path))
        throw new InvalidDataException($"NTFS namespace contains duplicate path '{path}'.");
      if (entry.Size < 0)
        throw new InvalidDataException($"NTFS entry '{path}' has a negative logical size.");
      if (entry.Size > geometry.VolumeBytes)
        throw new InvalidDataException($"NTFS entry '{path}' is larger than the declared volume.");
    }
  }

  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}

internal sealed class NtfsReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly NtfsReader _reader;
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public NtfsReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("NTFS mounted reads require a readable, seekable image.", nameof(image));
    _image = image;
    _leaveOpen = leaveOpen;
    _reader = new NtfsReader(image, leaveOpen: true);
    var records = _reader.Entries.ToArray();
    var root = new FilesystemNodeId(5, 0);
    var (nodes, links) = BuildNamespace(records, root);
    _namespace = new ReadOnlyFilesystemSnapshotSession(profile, root, nodes, links);
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
    _reader.Dispose();
    if (!_leaveOpen) _image.Dispose();
  }

  private (FilesystemSnapshotNode[] Nodes, FilesystemSnapshotDirectoryEntry[] Links) BuildNamespace(
      IReadOnlyList<NtfsEntry> records,
      FilesystemNodeId rootId) {
    var nodes = new Dictionary<uint, FilesystemSnapshotNode>();
    var links = new List<FilesystemSnapshotDirectoryEntry>(records.Count);
    var pathToNode = new Dictionary<string, FilesystemNodeId>(StringComparer.OrdinalIgnoreCase) {
      [string.Empty] = rootId,
    };
    nodes[5] = new FilesystemSnapshotNode(
      rootId, default, string.Empty, FilesystemNodeKind.Directory, 0, 0);

    foreach (var record in records.OrderBy(r => Depth(r.Name)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)) {
      var path = Normalize(record.Name);
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var name = slash < 0 ? path : path[(slash + 1)..];
      if (!pathToNode.TryGetValue(parentPath, out var parent))
        throw new InvalidDataException($"NTFS entry '{path}' has no decoded parent '{parentPath}'.");

      var nodeId = new FilesystemNodeId(record.MftRecord, 0);
      if (!nodes.TryGetValue(record.MftRecord, out var existing)) {
        var captured = record;
        Func<IFilesystemFileHandle>? open = null;
        if (!record.IsDirectory && !record.IsSymlink) {
          open = () => SpoolingReadOnlyFileHandle.Create(
            nodeId,
            record.Size,
            output => {
              byte[] data;
              lock (_ioGate) data = _reader.Extract(captured);
              output.Write(data);
            });
        }
        nodes[record.MftRecord] = new FilesystemSnapshotNode(
          nodeId,
          parent,
          name,
          record.IsDirectory ? FilesystemNodeKind.Directory
            : record.IsSymlink ? FilesystemNodeKind.SymbolicLink
            : FilesystemNodeKind.RegularFile,
          record.IsDirectory ? 0 : record.Size,
          record.IsDirectory ? 0 : record.Size,
          Modified: ToOffset(record.LastModified),
          SymbolicLinkTarget: record.LinkTarget,
          OpenReadHandle: open);
      } else {
        var expectedKind = record.IsDirectory ? FilesystemNodeKind.Directory
          : record.IsSymlink ? FilesystemNodeKind.SymbolicLink
          : FilesystemNodeKind.RegularFile;
        if (existing.Kind != expectedKind || existing.Size != (record.IsDirectory ? 0 : record.Size))
          throw new InvalidDataException($"NTFS aliases for MFT record {record.MftRecord} disagree on object metadata.");
      }

      links.Add(new FilesystemSnapshotDirectoryEntry(parent, name, nodeId));
      if (record.IsDirectory)
        pathToNode[path] = nodeId;
    }

    return (nodes.Values.ToArray(), links.ToArray());
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

internal readonly record struct NtfsDriverGeometry(
  int BytesPerSector,
  int SectorsPerCluster,
  long TotalSectors,
  long MftCluster,
  long MftMirrorCluster,
  int MftRecordSize) {

  public int ClusterSize => checked(BytesPerSector * SectorsPerCluster);
  public long VolumeBytes => checked(TotalSectors * BytesPerSector);

  public static NtfsDriverGeometry Parse(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("NTFS driver probing requires a readable, seekable stream.", nameof(image));
    if (image.Length < 512) throw new InvalidDataException("NTFS image is shorter than one boot sector.");

    Span<byte> boot = stackalloc byte[512];
    var original = image.Position;
    try {
      image.Position = 0;
      image.ReadExactly(boot);
    } finally {
      image.Position = original;
    }

    if (!boot.Slice(3, 8).SequenceEqual("NTFS    "u8))
      throw new InvalidDataException("NTFS OEM id is invalid.");
    if (boot[510] != 0x55 || boot[511] != 0xAA)
      throw new InvalidDataException("NTFS boot-sector signature is invalid.");

    var bps = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..13]);
    if (bps is not (512 or 1024 or 2048 or 4096))
      throw new NotSupportedException($"NTFS bytes-per-sector {bps} is unsupported.");
    var spc = boot[13];
    if (spc == 0 || (spc & (spc - 1)) != 0)
      throw new InvalidDataException($"NTFS sectors-per-cluster {spc} is invalid.");
    var total = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(boot[40..48]));
    if (total <= 0) throw new InvalidDataException("NTFS total-sector count is zero.");
    var volumeBytes = checked(total * bps);
    if (volumeBytes > image.Length)
      throw new InvalidDataException($"NTFS volume declares {volumeBytes:N0} bytes but image has {image.Length:N0}.");

    var clusterSize = checked(bps * spc);
    var mft = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(boot[48..56]));
    var mirror = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(boot[56..64]));
    if (mft <= 0 || mft >= total / spc)
      throw new InvalidDataException("NTFS $MFT LCN lies outside the declared volume.");
    if (mirror < 0 || mirror >= total / spc)
      throw new InvalidDataException("NTFS $MFTMirr LCN lies outside the declared volume.");

    var cpr = unchecked((sbyte)boot[64]);
    var recordSize = cpr < 0 ? 1 << -cpr : checked(cpr * clusterSize);
    if (recordSize is < 512 or > 65536 || (recordSize & (recordSize - 1)) != 0)
      throw new NotSupportedException($"NTFS MFT record size {recordSize:N0} is unsupported.");

    return new NtfsDriverGeometry(bps, spc, total, mft, mirror, recordSize);
  }
}
