#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Ext;

/// <summary>
/// Native ext2/3/4 driver sidecar. Inode numbers + i_generation form stable node
/// identities and hard-linked directory entries converge on the same node. File
/// content is streamed by the existing native inode/extent walker into a bounded
/// positional spool until the reader exposes its extent map directly.
/// </summary>
public sealed class ExtFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "Ext";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var super = ExtDriverSuperblock.Parse(image);
      super.ValidateReadableProfile(image);
      using var reader = new ExtReader(image, leaveOpen: true);
      var entries = reader.Entries.ToArray();
      ValidateEntries(image, super, entries);

      return new FilesystemDriverProfile(
        FormatId,
        super.ProfileName,
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.CaseSensitiveNames |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,
        [
          "Native inode+i_generation identities and hard-link aliasing are preserved.",
          "File handles currently spool the native streaming inode/extent reader to obtain positional frontend I/O; direct logical-block mapping is the next read-path optimization.",
          "Dirty journal-recovery profiles and incompatible data-layout features are rejected rather than exposing stale or undecoded contents.",
          "Mounted writes remain disabled until nested namespace mutation, journal transactions/replay and complete extent/htree/xattr ownership semantics are available.",
        ]);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        FormatId,
        "unsupported or damaged ext profile",
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
    ArgumentNullException.ThrowIfNull(options);
    if (!options.ReadOnly)
      throw new NotSupportedException(
        "ext mounted writes are fail-closed until JBD/JBD2 publication/replay and complete nested namespace/extent mutation are implemented.");
    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("ext image is not mountable: " + string.Join("; ", profile.Limitations));
    return new ExtReadOnlyFilesystemSession(image, profile, options.LeaveOpen);
  }

  public FilesystemDriverProfile ProbeFilesystem(IRandomAccessBlockDevice device) {
    using var stream = new BlockDeviceStream(device, leaveOpen: true);
    return ProbeFilesystem(stream);
  }

  public IFilesystemSession OpenFilesystem(IRandomAccessBlockDevice device, FilesystemOpenOptions options) {
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
      FilesystemDriverReadinessLayer.MetadataMutation |
      FilesystemDriverReadinessLayer.Links |
      FilesystemDriverReadinessLayer.Flush |
      FilesystemDriverReadinessLayer.DurabilityModel |
      FilesystemDriverReadinessLayer.Recovery |
      FilesystemDriverReadinessLayer.Concurrency;

    var available = profile.CanMount
      ? readRequired |
        FilesystemDriverReadinessLayer.NativeStableNodeIds |
        FilesystemDriverReadinessLayer.AllocationMap
      : FilesystemDriverReadinessLayer.None;
    var blockers = new List<string>(profile.Limitations);
    if (target == FilesystemDriverTarget.ReadWrite) {
      blockers.Add("Move ExtModifier/ExtRemover operations behind IRandomAccessBlockDevice and remove root-only / rebuild fallbacks from the mounted path.");
      blockers.Add("Implement arbitrary-directory create/mkdir/unlink/rmdir/rename/link/symlink with htree split/update and inode link-count/orphan handling.");
      blockers.Add("Implement positional write/truncate for direct/indirect and extent-tree files, including sparse/unwritten extents, extent-tree growth/merge and delayed allocation rules.");
      blockers.Add("Implement JBD/JBD2 transaction emission, revoke handling, barriers/checkpoint ordering and journal replay before enabling ext3/ext4 writable mounts.");
      blockers.Add("Complete xattr/ACL/quota/project-ID/encryption/verity/inline-data semantics or reject each profile before mutation.");
      blockers.Add("Add inode/dentry/page-cache locking semantics plus crash fault-injection and e2fsck/debugfs interoperability corpus.");
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

  private static void ValidateEntries(
      Stream image,
      ExtDriverSuperblock super,
      IReadOnlyList<ExtEntry> entries) {
    var byInode = new Dictionary<uint, ExtDriverInode>();
    foreach (var entry in entries) {
      if (entry.Inode == 0) throw new InvalidDataException($"ext entry '{entry.Name}' has inode 0.");
      if (!byInode.TryGetValue(entry.Inode, out var inode))
        byInode[entry.Inode] = inode = super.ReadInode(image, entry.Inode);

      var expectedKind = inode.Kind;
      if (entry.IsDirectory != (expectedKind == FilesystemNodeKind.Directory)
          || entry.IsSymlink != (expectedKind == FilesystemNodeKind.SymbolicLink))
        throw new InvalidDataException($"ext inode {entry.Inode} type disagrees with directory entry '{entry.Name}'.");
      if (!entry.IsDirectory && inode.Size != entry.Size)
        throw new NotSupportedException(
          $"ext inode {entry.Inode} for '{entry.Name}' has 64-bit size {inode.Size:N0}, but the current namespace reader exposed {entry.Size:N0}; direct large-file decoding must be completed before mounting this profile.");
    }
  }

  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}

internal sealed class ExtReadOnlyFilesystemSession : IFilesystemSession {
  private readonly Stream _image;
  private readonly bool _leaveOpen;
  private readonly object _ioGate = new();
  private readonly ExtReader _reader;
  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private bool _disposed;

  public ExtReadOnlyFilesystemSession(Stream image, FilesystemDriverProfile profile, bool leaveOpen) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ext mounted reads require a readable, seekable image.", nameof(image));
    _image = image;
    _leaveOpen = leaveOpen;
    var super = ExtDriverSuperblock.Parse(image);
    _reader = new ExtReader(image, leaveOpen: true);
    var records = _reader.Entries.ToArray();

    var rootInode = super.ReadInode(image, 2);
    var rootId = new FilesystemNodeId(2, rootInode.Generation);
    var (nodes, links) = BuildNamespace(super, records, rootId, rootInode);
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
      ExtDriverSuperblock super,
      IReadOnlyList<ExtEntry> records,
      FilesystemNodeId rootId,
      ExtDriverInode rootInode) {
    var nodesByInode = new Dictionary<uint, FilesystemSnapshotNode>();
    var pathToNode = new Dictionary<string, FilesystemNodeId>(StringComparer.Ordinal) {
      [string.Empty] = rootId,
    };
    var links = new List<FilesystemSnapshotDirectoryEntry>(records.Count);

    nodesByInode[2] = MakeNode(rootId, rootInode, string.Empty, default, null);

    foreach (var record in records.OrderBy(r => Depth(r.Name)).ThenBy(r => r.Name, StringComparer.Ordinal)) {
      var path = Normalize(record.Name);
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var name = slash < 0 ? path : path[(slash + 1)..];
      if (!pathToNode.TryGetValue(parentPath, out var parentId))
        throw new InvalidDataException($"ext entry '{path}' has no decoded parent '{parentPath}'.");

      var inode = super.ReadInode(_image, record.Inode);
      var nodeId = new FilesystemNodeId(record.Inode, inode.Generation);
      if (!nodesByInode.TryGetValue(record.Inode, out var existing)) {
        var captured = record;
        nodesByInode[record.Inode] = MakeNode(nodeId, inode, name, parentId, captured);
      } else if (existing.NodeId != nodeId || existing.Kind != inode.Kind || existing.Size != inode.Size) {
        throw new InvalidDataException($"ext hard-link aliases for inode {record.Inode} disagree on object metadata.");
      }

      links.Add(new FilesystemSnapshotDirectoryEntry(parentId, name, nodeId));
      if (inode.Kind == FilesystemNodeKind.Directory)
        pathToNode[path] = nodeId;
    }

    return (nodesByInode.Values.ToArray(), links.ToArray());
  }

  private FilesystemSnapshotNode MakeNode(
      FilesystemNodeId nodeId,
      ExtDriverInode inode,
      string name,
      FilesystemNodeId parent,
      ExtEntry? record) {
    Func<IFilesystemFileHandle>? open = null;
    if (inode.Kind == FilesystemNodeKind.RegularFile && record != null) {
      var captured = record;
      open = () => SpoolingReadOnlyFileHandle.Create(
        nodeId,
        inode.Size,
        output => {
          lock (_ioGate) _reader.ExtractTo(captured, output);
        });
    }

    return new FilesystemSnapshotNode(
      nodeId,
      parent,
      name,
      inode.Kind,
      inode.Kind == FilesystemNodeKind.Directory ? 0 : inode.Size,
      inode.AllocatedBytes,
      LinkCount: inode.LinkCount,
      NativeAttributes: ((ulong)inode.Flags << 32) | inode.Mode,
      Modified: inode.Modified,
      SymbolicLinkTarget: record?.LinkTarget,
      OpenReadHandle: open);
  }

  private static int Depth(string path) => Normalize(path).Count(c => c == '/');
  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}

internal sealed record ExtDriverInode(
  uint Number,
  ushort Mode,
  FilesystemNodeKind Kind,
  long Size,
  long AllocatedBytes,
  uint LinkCount,
  uint Generation,
  uint Flags,
  DateTimeOffset? Modified);

internal readonly record struct ExtDriverSuperblock(
  uint InodesCount,
  ulong BlocksCount,
  int BlockSize,
  uint BlocksPerGroup,
  uint InodesPerGroup,
  ushort InodeSize,
  uint FirstDataBlock,
  uint FeatureCompat,
  uint FeatureIncompat,
  uint FeatureRoCompat,
  ushort State,
  ushort DescriptorSize) {

  public const uint CompatHasJournal = 0x0004;
  private const uint IncompatRecover = 0x0004;
  private const uint IncompatJournalDevice = 0x0008;
  private const uint IncompatMetaBg = 0x0010;
  private const uint Incompat64Bit = 0x0080;
  private const uint IncompatDirData = 0x1000;
  private const uint IncompatInlineData = 0x8000;
  private const uint IncompatEncrypt = 0x10000;
  private const uint IncompatCasefold = 0x20000;
  private const uint IncompatVerity = 0x100000;
  private const uint RoCompatBigalloc = 0x0200;

  public string ProfileName {
    get {
      var ext4 = (FeatureIncompat & (0x0040u | Incompat64Bit)) != 0 || (FeatureRoCompat & 0x0400u) != 0;
      if (ext4) return "ext4 native inode reader";
      return (FeatureCompat & CompatHasJournal) != 0
        ? "ext3 native inode reader"
        : "ext2 native inode reader";
    }
  }

  public static ExtDriverSuperblock Parse(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ext driver probing requires a readable, seekable image.", nameof(image));
    if (image.Length < 2048) throw new InvalidDataException("ext image is too small for its superblock.");
    var sb = new byte[1024];
    var original = image.Position;
    try {
      image.Position = 1024;
      image.ReadExactly(sb);
    } finally {
      image.Position = original;
    }
    if (BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(56, 2)) != 0xEF53)
      throw new InvalidDataException("ext superblock magic is not 0xEF53.");

    var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24, 4));
    if (logBlock > 6) throw new NotSupportedException($"ext block-size shift {logBlock} is unsupported.");
    var blockSize = checked(1024 << (int)logBlock);
    var blocksLo = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4, 4));
    var incompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(96, 4));
    var blocksHi = (incompat & Incompat64Bit) != 0
      ? BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x150, 4))
      : 0u;
    var blocks = ((ulong)blocksHi << 32) | blocksLo;
    var inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(88, 2));
    if (inodeSize == 0) inodeSize = 128;
    var descSize = (incompat & Incompat64Bit) != 0
      ? Math.Max((ushort)64, BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(0xFE, 2)))
      : (ushort)32;

    return new ExtDriverSuperblock(
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0, 4)),
      blocks,
      blockSize,
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32, 4)),
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(40, 4)),
      inodeSize,
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20, 4)),
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(92, 4)),
      incompat,
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(100, 4)),
      BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(58, 2)),
      descSize);
  }

  public void ValidateReadableProfile(Stream image) {
    if (InodesCount == 0 || BlocksCount == 0 || BlocksPerGroup == 0 || InodesPerGroup == 0)
      throw new InvalidDataException("ext superblock has zero geometry fields.");
    var declared = checked((long)Math.Min(BlocksCount, (ulong)long.MaxValue) * BlockSize);
    if ((ulong)declared / (ulong)BlockSize != BlocksCount)
      throw new NotSupportedException("ext declared volume size exceeds the addressable stream range.");
    if (image.Length < declared)
      throw new InvalidDataException($"ext volume declares {declared:N0} bytes but image has {image.Length:N0}.");

    var unsupported = FeatureIncompat &
      (IncompatRecover | IncompatJournalDevice | IncompatMetaBg | IncompatDirData |
       IncompatInlineData | IncompatEncrypt | IncompatCasefold | IncompatVerity);
    if (unsupported != 0)
      throw new NotSupportedException(
        $"ext profile has unsupported/unsafe incompat feature bits 0x{unsupported:X8}; mount remains fail-closed.");
    if ((FeatureRoCompat & RoCompatBigalloc) != 0)
      throw new NotSupportedException("ext bigalloc cluster semantics are not decoded by the current mounted reader.");
    if ((State & 0x0001) == 0 && (FeatureCompat & CompatHasJournal) != 0)
      throw new NotSupportedException("ext journaled volume is not marked clean; JBD/JBD2 replay is required before mounting this snapshot.");
  }

  public ExtDriverInode ReadInode(Stream image, uint inodeNumber) {
    if (inodeNumber == 0 || inodeNumber > InodesCount)
      throw new InvalidDataException($"ext inode {inodeNumber} lies outside 1..{InodesCount}.");
    var group = (inodeNumber - 1) / InodesPerGroup;
    var index = (inodeNumber - 1) % InodesPerGroup;
    var groupCount = checked((BlocksCount + BlocksPerGroup - 1) / BlocksPerGroup);
    if (group >= groupCount) throw new InvalidDataException($"ext inode {inodeNumber} has invalid block group {group}.");

    var bgdtBlock = FirstDataBlock + 1UL;
    var descriptorOffset = checked((long)bgdtBlock * BlockSize + (long)group * DescriptorSize);
    Span<byte> descriptor = stackalloc byte[64];
    var original = image.Position;
    try {
      image.Position = descriptorOffset;
      image.ReadExactly(descriptor[..DescriptorSize]);
    } finally {
      image.Position = original;
    }
    var tableLo = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(8, 4));
    var tableHi = DescriptorSize >= 64
      ? BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(0x28, 4))
      : 0u;
    var tableBlock = ((ulong)tableHi << 32) | tableLo;
    var inodeOffset = checked((long)tableBlock * BlockSize + (long)index * InodeSize);
    if (inodeOffset < 0 || inodeOffset > image.Length - InodeSize)
      throw new InvalidDataException($"ext inode {inodeNumber} table slot lies outside the image.");

    var inode = new byte[InodeSize];
    try {
      image.Position = inodeOffset;
      image.ReadExactly(inode);
    } finally {
      image.Position = original;
    }

    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(0, 2));
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
    var sizeLo = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4, 4));
    var sizeHi = kind == FilesystemNodeKind.RegularFile && inode.Length >= 112
      ? BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(108, 4))
      : 0u;
    var size = checked((long)(((ulong)sizeHi << 32) | sizeLo));
    var blocksLo = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(28, 4));
    var blocksHi = inode.Length >= 118 ? BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(116, 2)) : (ushort)0;
    var sectors = ((ulong)blocksHi << 32) | blocksLo;
    var allocated = checked((long)checked(sectors * 512UL));
    var links = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(26, 2));
    var generation = inode.Length >= 104 ? BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(100, 4)) : 0u;
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(32, 4));
    var mtime = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(16, 4));
    DateTimeOffset? modified = null;
    if (mtime != 0) {
      try { modified = DateTimeOffset.FromUnixTimeSeconds(mtime); } catch { }
    }
    return new ExtDriverInode(inodeNumber, mode, kind, size, allocated, links, generation, flags, modified);
  }
}
