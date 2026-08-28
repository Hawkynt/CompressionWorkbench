#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Stable, path-independent identity of a filesystem object. A real driver must
/// not use a pathname as identity: rename/unlink can change names while open
/// handles keep referring to the same inode/object. Providers map their native
/// inode, file-reference, object-id or directory-slot identity into these two
/// opaque 64-bit words.
/// </summary>
public readonly record struct FilesystemNodeId(ulong Value, ulong Generation = 0);

public enum FilesystemNodeKind {
  Unknown,
  RegularFile,
  Directory,
  SymbolicLink,
  BlockDevice,
  CharacterDevice,
  Fifo,
  Socket,
}

[Flags]
public enum FilesystemDriverCapabilities : ulong {
  None = 0,
  EnumerateDirectories = 1UL << 0,
  ReadData = 1UL << 1,
  RandomAccess = 1UL << 2,
  StableNodeIds = 1UL << 3,
  WriteData = 1UL << 4,
  Truncate = 1UL << 5,
  CreateFile = 1UL << 6,
  DeleteFile = 1UL << 7,
  CreateDirectory = 1UL << 8,
  RemoveDirectory = 1UL << 9,
  Rename = 1UL << 10,
  HardLinks = 1UL << 11,
  SymbolicLinks = 1UL << 12,
  SetMetadata = 1UL << 13,
  SparseFiles = 1UL << 14,
  Flush = 1UL << 15,
  Transactions = 1UL << 16,
  CaseSensitiveNames = 1UL << 17,
  CasePreservingNames = 1UL << 18,
}

/// <summary>
/// Describes how namespace/data writes become durable on this exact on-disk
/// profile. This is intentionally separate from <see cref="FormatCapabilities.CanModify"/>:
/// archive-level Add/Remove may legitimately rebuild a whole image, while a
/// writable mounted filesystem driver needs bounded, handle-safe mutations.
/// </summary>
public enum FilesystemMutationModel {
  None,
  Direct,
  Journaled,
  CopyOnWrite,
  LogStructured,
  WholeImageRebuild,
}

/// <summary>
/// Per-image probe result. Capabilities are not assumed from the format name:
/// an EROFS flat profile, a compressed EROFS profile, a damaged FAT image, or a
/// ReFS version with an unsupported metadata feature can have different safe
/// operations even though they share one descriptor.
/// </summary>
public sealed record FilesystemDriverProfile(
  string FormatId,
  string ProfileName,
  FilesystemDriverCapabilities Capabilities,
  FilesystemMutationModel MutationModel,
  bool CanMount,
  bool CanMountWritable,
  IReadOnlyList<string> Limitations
);

public sealed record FilesystemOpenOptions(
  bool ReadOnly = true,
  bool LeaveOpen = true
);

public sealed record FilesystemDirectoryEntry(
  string Name,
  FilesystemNodeId NodeId,
  FilesystemNodeKind Kind
);

public sealed record FilesystemNodeInfo(
  FilesystemNodeId NodeId,
  FilesystemNodeKind Kind,
  long Size,
  long AllocatedSize,
  uint LinkCount = 1,
  ulong NativeAttributes = 0,
  DateTimeOffset? Created = null,
  DateTimeOffset? Modified = null,
  DateTimeOffset? Accessed = null,
  DateTimeOffset? Changed = null
);

/// <summary>Optional metadata changes; null means leave the field unchanged.</summary>
public sealed record FilesystemMetadataPatch(
  DateTimeOffset? Created = null,
  DateTimeOffset? Modified = null,
  DateTimeOffset? Accessed = null,
  ulong? NativeAttributes = null
);

/// <summary>
/// Descriptor-side entry point for a mount-grade filesystem implementation.
/// Probe must be non-destructive and fail closed. Open must reject writable mode
/// unless the returned profile has <see cref="FilesystemDriverProfile.CanMountWritable"/>.
/// </summary>
public interface IFilesystemDriverProvider {
  FilesystemDriverProfile ProbeFilesystem(Stream image);
  IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options);
}

/// <summary>
/// Open filesystem namespace. Operations use stable node ids rather than paths,
/// mirroring the semantics required by FUSE/Dokany/WinFsp-style adapters: a
/// caller may keep a file handle open across rename or unlink.
/// </summary>
public interface IFilesystemSession : IDisposable {
  FilesystemDriverProfile Profile { get; }
  FilesystemNodeId RootNodeId { get; }

  FilesystemNodeInfo Stat(FilesystemNodeId nodeId);
  FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name);
  IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory);
  IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access);

  FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name);
  FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name);
  void DeleteFile(FilesystemNodeId parentDirectory, string name);
  void RemoveDirectory(FilesystemNodeId parentDirectory, string name);
  void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace);
  void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName);
  FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target);
  string ReadSymbolicLink(FilesystemNodeId nodeId);
  void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch);

  /// <summary>Flushes all dirty data and metadata that are not inside an active transaction.</summary>
  void Flush();

  /// <summary>
  /// Begins one durability transaction for this session. Until Commit/Rollback,
  /// namespace operations and writes through handles opened by the session belong
  /// to that transaction. Providers that do not advertise Transactions throw.
  /// </summary>
  IFilesystemTransaction BeginTransaction();
}

/// <summary>
/// Positional file handle. It deliberately has no shared Stream.Position so two
/// concurrent kernel requests cannot race a mutable cursor. Reads/writes operate
/// at explicit logical offsets and therefore map naturally to filesystem extents.
/// </summary>
public interface IFilesystemFileHandle : IDisposable {
  FilesystemNodeId NodeId { get; }
  long Length { get; }
  int Read(long offset, Span<byte> destination);
  void Write(long offset, ReadOnlySpan<byte> source);
  void SetLength(long length);
  void Flush();
}

public interface IFilesystemTransaction : IDisposable {
  bool IsCompleted { get; }
  void Commit();
  void Rollback();
}

/// <summary>
/// Geometry of a sector/block-addressable device exposed beneath a filesystem.
/// Container formats such as VHD/QCOW2/EWF should eventually implement this
/// layer; FAT/ext/ReFS drivers then consume block devices rather than knowing
/// how their outer container stores bytes.
/// </summary>
public sealed record BlockDeviceGeometry(
  int LogicalBlockSize,
  long BlockCount,
  int PhysicalBlockSize = 0,
  bool SupportsTrim = false
) {
  public long Length => checked(BlockCount * LogicalBlockSize);
}

public interface IRandomAccessBlockDevice : IDisposable {
  BlockDeviceGeometry Geometry { get; }
  bool CanWrite { get; }
  int ReadBlocks(long firstBlock, Span<byte> destination);
  void WriteBlocks(long firstBlock, ReadOnlySpan<byte> source);
  void Trim(long firstBlock, long blockCount);
  void Flush();
}

/// <summary>
/// Raw variable-length track device for flux/GCR/MFM-style containers that are
/// not yet sector-addressable. G64 belongs here; a decoder can later project it
/// as <see cref="IRandomAccessBlockDevice"/> for a Commodore filesystem driver.
/// </summary>
public sealed record RawTrackInfo(
  int Index,
  long Length,
  uint EncodingParameter = 0,
  bool IsPresent = true
);

public interface IRawTrackDevice : IDisposable {
  int TrackCount { get; }
  bool CanWrite { get; }
  IReadOnlyList<RawTrackInfo> EnumerateTracks();
  int ReadTrack(int index, Span<byte> destination);
  void WriteTrack(int index, ReadOnlySpan<byte> source, uint? encodingParameter = null);
  void ClearTrack(int index);
  void Flush();
}

/// <summary>
/// Optional descriptor capability for opening the raw-track layer directly.
/// This keeps track-container mutation separate from filesystem namespace CRUD.
/// </summary>
public interface IRawTrackDeviceProvider {
  IRawTrackDevice OpenRawTrackDevice(Stream image, bool writable, bool leaveOpen = true);
}
