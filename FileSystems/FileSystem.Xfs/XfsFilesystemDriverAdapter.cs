#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Xfs;

/// <summary>
/// Native XFS read-only driver sidecar. XFS inode numbers and di_gen are used
/// as path-independent object identities; duplicate directory entries targeting
/// one inode therefore naturally become hard links in the common session.
/// The current reader supports local and inline extent forks. Btree-format data
/// forks, sparse logical gaps and unsupported v5 incompat features fail closed
/// until their mappings are decoded rather than being flattened incorrectly.
/// </summary>
public sealed class XfsFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "Xfs";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var geometry = XfsDriverGeometry.Parse(image);
      using var reader = new XfsReader(image, leaveOpen: true);
      var records = reader.Entries.ToArray();
      geometry.ValidateInode(image, geometry.RootInode, requireDirectory: true);
      foreach (var entry in records)
        geometry.ValidateEntry(image, entry);

      return new FilesystemDriverProfile(
        FormatId,
        geometry.Version >= 5 ? "XFS v5 native inode reader" : "XFS v4 native inode reader",
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.HardLinks |
        FilesystemDriverCapabilities.SymbolicLinks |
        FilesystemDriverCapabilities.CaseSensitiveNames |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,
        [
          "Native inode+di_gen identities are preserved; multiple decoded directory entries can reference the same session object as hard links.",
          "Regular-file reads stream local/extent forks into a bounded positional spool; direct extent-at-offset handles are the next read-path optimization.",
          "Sparse logical gaps and btree-format data forks are rejected because the current XfsReader extent streamer would otherwise flatten them.",
          "Mounted writes remain disabled until allocation-group btrees, log transactions/replay and complete directory/data-fork mutation share one transactional core.",
        ]);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        FormatId,
        "unsupported or damaged XFS profile",
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
        "XFS mounted writes require allocation-group btree updates plus log emission/replay and complete inode/directory mutation; offline archive mutation is not sufficient.");
    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("XFS image is not mountable: " + string.Join("; ", profile.Limitations));
    return new XfsReadOnlyFilesystemSession(image, profile, options.LeaveOpen);
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
      FilesystemDriverReadinessLayer.NativeStableNodeIds |
      FilesystemDriverReadinessLayer.ReadData |
      FilesystemDriverReadinessLayer.RandomAccessRead;
    var writeRequired = readRequired |
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
        FilesystemDriverReadinessLayer.Links |
        FilesystemDriverReadinessLayer.ValidationCorpus
      : FilesystemDriverReadinessLayer.None;
    var blockers = new List<string>(profile.Limitations);
    if (target == FilesystemDriverTarget.ReadWrite) {
      blockers.Add("Implement direct positional extent/local/btree file handles, including sparse and unwritten extent semantics and extent-btree growth/merge.");
      blockers.Add("Move create/unlink/mkdir/rmdir/rename/link/symlink behind common inode + dir2/dir3 shortform/block/leaf/node mutation with exact name-hash/collation semantics.");
      blockers.Add("Unify bnobt/cntbt/inobt/finobt/rmapbt/refcountbt allocation ownership and per-AG free-space updates behind bounded block-device transactions.");
      blockers.Add("Implement XFS log item formatting, transaction commit ordering, log grant/tail handling, recovery replay and superblock/AG/inode CRC publication.");
      blockers.Add("Cover reflink/COW, realtime, quota, xattr/ACL, project ID, bigtime, sparse-inode and newer v5 feature profiles before allowing their mutation.");
      blockers.Add("Add inode/dentry/AG locking plus crash fault-injection and xfs_repair/xfs_db interoperability corpora for mounted mutations.");
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

  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}

internal sealed class XfsReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly XfsReader _reader;
  private readonly XfsDriverGeometry _geometry;
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public XfsReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("XFS mounted reads require a readable, seekable image.", nameof(image));
    _image = image;
    _leaveOpen = leaveOpen;
    _geometry = XfsDriverGeometry.Parse(image);
    _reader = new XfsReader(image, leaveOpen: true);
    var records = _reader.Entries.ToArray();
    var rootInode = _geometry.ReadInode(image, _geometry.RootInode);
    var rootId = new FilesystemNodeId(_geometry.RootInode, rootInode.Generation);
    var (nodes, links) = BuildNamespace(records, rootId, rootInode);
    _namespace = new ReadOnlyFilesystemSnapshotSession(profile, rootId, nodes, links);
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
      IReadOnlyList<XfsEntry> records,
      FilesystemNodeId rootId,
      XfsDriverInode rootInode) {
    var nodes = new Dictionary<ulong, FilesystemSnapshotNode>();
    var links = new List<FilesystemSnapshotDirectoryEntry>(records.Count);
    var pathToNode = new Dictionary<string, FilesystemNodeId>(StringComparer.Ordinal) {
      [string.Empty] = rootId,
    };
    nodes[_geometry.RootInode] = MakeNode(rootId, rootInode, string.Empty, default, null);

    foreach (var record in records.OrderBy(r => Depth(r.Name)).ThenBy(r => r.Name, StringComparer.Ordinal)) {
      var path = Normalize(record.Name);
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var name = slash < 0 ? path : path[(slash + 1)..];
      if (!pathToNode.TryGetValue(parentPath, out var parent))
        throw new InvalidDataException($"XFS entry '{path}' has no decoded parent '{parentPath}'.");
      if (record.InodeNumber <= 0)
        throw new InvalidDataException($"XFS entry '{path}' has invalid inode {record.InodeNumber}.");

      var ino = checked((ulong)record.InodeNumber);
      var inode = _geometry.ReadInode(_image, ino);
      var nodeId = new FilesystemNodeId(ino, inode.Generation);
      if (!nodes.TryGetValue(ino, out var existing)) {
        nodes[ino] = MakeNode(nodeId, inode, name, parent, record);
      } else if (existing.NodeId != nodeId || existing.Kind != inode.Kind || existing.Size != (inode.Kind == FilesystemNodeKind.Directory ? 0 : inode.Size)) {
        throw new InvalidDataException($"XFS hard-link aliases for inode {ino} disagree on object metadata.");
      }

      links.Add(new FilesystemSnapshotDirectoryEntry(parent, name, nodeId));
      if (inode.Kind == FilesystemNodeKind.Directory)
        pathToNode[path] = nodeId;
    }
    return (nodes.Values.ToArray(), links.ToArray());
  }

  private FilesystemSnapshotNode MakeNode(
      FilesystemNodeId nodeId,
      XfsDriverInode inode,
      string name,
      FilesystemNodeId parent,
      XfsEntry? record) {
    Func<IFilesystemFileHandle>? open = null;
    string? symlink = null;
    if (record != null && inode.Kind == FilesystemNodeKind.RegularFile) {
      var captured = record;
      open = () => SpoolingReadOnlyFileHandle.Create(
        nodeId,
        inode.Size,
        output => {
          long written;
          lock (_ioGate) written = _reader.ExtractTo(captured, output);
          if (written != inode.Size)
            throw new InvalidDataException($"XFS inode {inode.Number} yielded {written:N0} of {inode.Size:N0} logical bytes.");
        });
    } else if (record != null && inode.Kind == FilesystemNodeKind.SymbolicLink) {
      if (inode.Size > 64 * 1024)
        throw new NotSupportedException($"XFS symlink inode {inode.Number} has implausible size {inode.Size:N0}.");
      using var memory = new MemoryStream();
      long written;
      lock (_ioGate) written = _reader.ExtractTo(record, memory);
      if (written != inode.Size)
        throw new InvalidDataException($"XFS symlink inode {inode.Number} could not be decoded completely.");
      symlink = Encoding.UTF8.GetString(memory.ToArray()).TrimEnd('\0');
    }

    return new FilesystemSnapshotNode(
      nodeId,
      parent,
      name,
      inode.Kind,
      inode.Kind == FilesystemNodeKind.Directory ? 0 : inode.Size,
      inode.AllocatedBytes,
      LinkCount: inode.LinkCount,
      NativeAttributes: ((ulong)inode.Format << 32) | inode.Mode,
      Modified: inode.Modified,
      SymbolicLinkTarget: symlink,
      OpenReadHandle: open);
  }

  private static int Depth(string path) => Normalize(path).Count(c => c == '/');
  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}

internal sealed record XfsDriverInode(
  ulong Number,
  ushort Mode,
  FilesystemNodeKind Kind,
  byte Format,
  long Size,
  long AllocatedBytes,
  uint LinkCount,
  uint Generation,
  DateTimeOffset? Modified);

internal readonly record struct XfsDriverGeometry(
  uint BlockSize,
  ushort InodeSize,
  ulong RootInode,
  uint AgBlocks,
  uint AgCount,
  byte AgBlockLog,
  ushort VersionNumber,
  uint FeaturesIncompat,
  ulong DataBlocks) {

  public int Version => VersionNumber & 0xF;
  private int InodeForkOffset => Version >= 5 ? 176 : 100;
  public long VolumeBytes => checked((long)Math.Min(DataBlocks, (ulong)(long.MaxValue / BlockSize)) * BlockSize);

  public static XfsDriverGeometry Parse(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("XFS driver probing requires a readable, seekable stream.", nameof(image));
    if (image.Length < 512) throw new InvalidDataException("XFS image is too small for its superblock.");
    Span<byte> sb = stackalloc byte[512];
    var original = image.Position;
    try {
      image.Position = 0;
      image.ReadExactly(sb);
    } finally {
      image.Position = original;
    }
    if (BinaryPrimitives.ReadUInt32BigEndian(sb[0..4]) != 0x58465342)
      throw new InvalidDataException("XFS superblock magic is invalid.");
    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(sb[4..8]);
    if (blockSize is < 512 or > 65536 || (blockSize & (blockSize - 1)) != 0)
      throw new NotSupportedException($"XFS block size {blockSize:N0} is unsupported.");
    var dblocks = BinaryPrimitives.ReadUInt64BigEndian(sb[8..16]);
    var root = BinaryPrimitives.ReadUInt64BigEndian(sb[56..64]);
    var agBlocks = BinaryPrimitives.ReadUInt32BigEndian(sb[84..88]);
    var agCount = BinaryPrimitives.ReadUInt32BigEndian(sb[88..92]);
    var version = BinaryPrimitives.ReadUInt16BigEndian(sb[100..102]);
    var inodeSize = BinaryPrimitives.ReadUInt16BigEndian(sb[104..106]);
    var agBlkLog = sb[124];
    var incompat = (version & 0xF) >= 5 ? BinaryPrimitives.ReadUInt32BigEndian(sb[216..220]) : 0u;

    if (inodeSize is < 256 or > 2048 || (inodeSize & (inodeSize - 1)) != 0)
      throw new NotSupportedException($"XFS inode size {inodeSize:N0} is unsupported.");
    if (root == 0 || agBlocks == 0 || agCount == 0 || dblocks == 0)
      throw new InvalidDataException("XFS superblock has zero root/AG/data geometry.");
    if (agBlkLog == 0 || agBlkLog >= 32)
      throw new InvalidDataException($"XFS sb_agblklog {agBlkLog} is invalid.");
    var declaredBytes = checked((long)Math.Min(dblocks, (ulong)(long.MaxValue / blockSize)) * blockSize);
    if ((ulong)(declaredBytes / blockSize) != dblocks)
      throw new NotSupportedException("XFS declared data-device size exceeds the addressable stream range.");
    if (declaredBytes > image.Length)
      throw new InvalidDataException($"XFS data device declares {declaredBytes:N0} bytes but image has {image.Length:N0}.");

    // The current namespace/data reader only understands FTYPE among v5
    // incompat features. Refuse any other incompat bit rather than interpreting
    // newer on-disk structures with an older layout.
    const uint ftype = 0x00000001;
    var unsupported = incompat & ~ftype;
    if (unsupported != 0)
      throw new NotSupportedException($"XFS incompat feature bits 0x{unsupported:X8} are not decoded by the mounted reader.");

    return new XfsDriverGeometry(blockSize, inodeSize, root, agBlocks, agCount, agBlkLog, version, incompat, dblocks);
  }

  public void ValidateEntry(Stream image, XfsEntry entry) {
    if (entry.InodeNumber <= 0)
      throw new InvalidDataException($"XFS entry '{entry.Name}' has invalid inode number {entry.InodeNumber}.");
    var inode = ReadInode(image, checked((ulong)entry.InodeNumber));
    if (entry.IsDirectory != (inode.Kind == FilesystemNodeKind.Directory))
      throw new InvalidDataException($"XFS inode {inode.Number} type disagrees with directory entry '{entry.Name}'.");
    if (!entry.IsDirectory && entry.Size != inode.Size)
      throw new InvalidDataException($"XFS inode {inode.Number} size disagrees with directory entry '{entry.Name}'.");
    ValidateInodeDataMap(image, inode);
  }

  public void ValidateInode(Stream image, ulong inodeNumber, bool requireDirectory = false) {
    var inode = ReadInode(image, inodeNumber);
    if (requireDirectory && inode.Kind != FilesystemNodeKind.Directory)
      throw new InvalidDataException($"XFS root inode {inodeNumber} is not a directory.");
    ValidateInodeDataMap(image, inode);
  }

  public XfsDriverInode ReadInode(Stream image, ulong inodeNumber) {
    var offset = InodeOffset(inodeNumber);
    if (offset < 0 || offset > image.Length - InodeSize)
      throw new InvalidDataException($"XFS inode {inodeNumber} lies outside the data device.");
    var inode = new byte[InodeSize];
    var original = image.Position;
    try {
      image.Position = offset;
      image.ReadExactly(inode);
    } finally {
      image.Position = original;
    }
    if (BinaryPrimitives.ReadUInt16BigEndian(inode.AsSpan(0, 2)) != 0x494E)
      throw new InvalidDataException($"XFS inode {inodeNumber} has invalid magic.");
    var mode = BinaryPrimitives.ReadUInt16BigEndian(inode.AsSpan(2, 2));
    var kind = (mode & 0xF000) switch {
      0x4000 => FilesystemNodeKind.Directory,
      0x8000 => FilesystemNodeKind.RegularFile,
      0xA000 => FilesystemNodeKind.SymbolicLink,
      0x2000 => FilesystemNodeKind.CharacterDevice,
      0x6000 => FilesystemNodeKind.BlockDevice,
      0x1000 => FilesystemNodeKind.Fifo,
      0xC000 => FilesystemNodeKind.Socket,
      _ => FilesystemNodeKind.Unknown,
    };
    var format = inode[5];
    if (format is not (1 or 2))
      throw new NotSupportedException($"XFS inode {inodeNumber} uses data-fork format {format}; only local/extents are decoded by the mounted reader.");
    var sizeU = BinaryPrimitives.ReadUInt64BigEndian(inode.AsSpan(56, 8));
    if (sizeU > long.MaxValue) throw new NotSupportedException($"XFS inode {inodeNumber} size exceeds Int64.");
    var size = (long)sizeU;
    var blocks = BinaryPrimitives.ReadUInt64BigEndian(inode.AsSpan(64, 8));
    var allocated = blocks > (ulong)(long.MaxValue / 512) ? long.MaxValue : (long)blocks * 512;
    var links = BinaryPrimitives.ReadUInt32BigEndian(inode.AsSpan(16, 4));
    var generation = inode.Length >= 96 ? BinaryPrimitives.ReadUInt32BigEndian(inode.AsSpan(92, 4)) : 0u;
    DateTimeOffset? modified = null;
    if (inode.Length >= 40) {
      var seconds = BinaryPrimitives.ReadInt32BigEndian(inode.AsSpan(32, 4));
      try { modified = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch { }
    }
    return new XfsDriverInode(inodeNumber, mode, kind, format, size, allocated, links, generation, modified);
  }

  private void ValidateInodeDataMap(Stream image, XfsDriverInode inode) {
    if (inode.Kind is FilesystemNodeKind.CharacterDevice or FilesystemNodeKind.BlockDevice or FilesystemNodeKind.Fifo or FilesystemNodeKind.Socket)
      return;
    if (inode.Format == 1) {
      if (inode.Size > InodeSize - InodeForkOffset)
        throw new InvalidDataException($"XFS local inode {inode.Number} size does not fit its data fork.");
      return;
    }

    var offset = InodeOffset(inode.Number);
    Span<byte> header = stackalloc byte[96];
    var original = image.Position;
    try {
      image.Position = offset;
      image.ReadExactly(header);
    } finally {
      image.Position = original;
    }
    var nextents = BinaryPrimitives.ReadUInt32BigEndian(header[76..80]);
    var capacity = (InodeSize - InodeForkOffset) / 16;
    if (nextents > capacity)
      throw new NotSupportedException($"XFS inode {inode.Number} has {nextents} inline extents; extent-btree decoding is required.");

    var extentBytes = new byte[checked((int)nextents * 16)];
    if (extentBytes.Length > 0) {
      try {
        image.Position = offset + InodeForkOffset;
        image.ReadExactly(extentBytes);
      } finally {
        image.Position = original;
      }
    }
    ulong logical = 0;
    for (var i = 0; i < nextents; ++i) {
      var hi = BinaryPrimitives.ReadUInt64BigEndian(extentBytes.AsSpan(i * 16, 8));
      var lo = BinaryPrimitives.ReadUInt64BigEndian(extentBytes.AsSpan(i * 16 + 8, 8));
      var startOff = (hi >> 9) & 0x003F_FFFF_FFFF_FFFFUL;
      var startBlock = ((hi & 0x1FF) << 43) | (lo >> 21);
      var blockCount = lo & 0x1F_FFFFUL;
      if (blockCount == 0) throw new InvalidDataException($"XFS inode {inode.Number} contains a zero-length extent.");
      if (startOff != logical)
        throw new NotSupportedException($"XFS inode {inode.Number} has a sparse/non-contiguous logical extent map; mounted sparse reads are not decoded yet.");
      if (startBlock >= DataBlocks || blockCount > DataBlocks - startBlock)
        throw new InvalidDataException($"XFS inode {inode.Number} extent points outside the data device.");
      logical += blockCount;
    }
    var covered = checked(logical * BlockSize);
    if (covered < (ulong)inode.Size)
      throw new InvalidDataException($"XFS inode {inode.Number} extents cover {covered:N0} of {inode.Size:N0} logical bytes.");
  }

  private long InodeOffset(ulong ino) {
    var inodesPerBlock = BlockSize / InodeSize;
    if (inodesPerBlock == 0 || (inodesPerBlock & (inodesPerBlock - 1)) != 0)
      throw new NotSupportedException("XFS inodes-per-block is not a power of two.");
    var inoPbLog = 0;
    for (var v = inodesPerBlock; v > 1; v >>= 1) ++inoPbLog;
    var aginoLog = checked(AgBlockLog + inoPbLog);
    if (aginoLog >= 64) throw new InvalidDataException("XFS inode geometry overflows native inode encoding.");
    var agNo = ino >> aginoLog;
    var agInoMask = aginoLog == 64 ? ulong.MaxValue : (1UL << aginoLog) - 1;
    var agIno = ino & agInoMask;
    var block = agIno / inodesPerBlock;
    var index = agIno % inodesPerBlock;
    if (agNo >= AgCount || block >= AgBlocks)
      throw new InvalidDataException($"XFS inode {ino} encodes an invalid allocation-group position.");
    return checked((long)((agNo * AgBlocks + block) * BlockSize + index * InodeSize));
  }
}
