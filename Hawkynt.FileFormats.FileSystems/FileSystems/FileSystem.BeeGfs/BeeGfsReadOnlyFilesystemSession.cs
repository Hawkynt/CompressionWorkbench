#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileSystem.BeeGfs;

internal sealed record BeeGfsTargetChunkBinding(
  IFilesystemSession Session,
  FilesystemNodeId NodeId,
  long RequiredLength
);

/// <summary>
/// Read-only logical BeeGFS namespace reconstructed from a quiescent set of metadata
/// and storage targets. The initial supported profile is intentionally narrow:
/// non-mirrored V3 directory dentries plus V6 inline regular-file inodes using RAID0,
/// with non-sparse local chunks. Unsupported metadata fails closed.
/// </summary>
internal sealed class BeeGfsReadOnlyFilesystemSession : IFilesystemSession {
  private const int MaxMetadataBytes = 4096;
  private const string MetadataXattrName = "user.fhgfs";
  private const string EntryIdDirectoryName = "#fSiDs#";
  private const string RootEntryId = "root";

  private sealed record OpenTarget(
    BeeGfsTargetMember Member,
    IFilesystemSession Session,
    FilesystemNodeId RootNodeId);

  private sealed record DiscoveredDentry(
    string ParentEntryId,
    string Name,
    BeeGfsDecodedDentry Metadata);

  private readonly ReadOnlyFilesystemSnapshotSession _namespace;
  private readonly OpenTarget[] _targets;
  private readonly FilesystemStreamSource[] _sources;
  private readonly bool _leaveOpen;
  private bool _disposed;

  public BeeGfsReadOnlyFilesystemSession(
      FilesystemStreamSet sources,
      BeeGfsTargetTopology topology,
      bool leaveOpen) {
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(topology);
    _leaveOpen = leaveOpen;
    _sources = sources.ToArray();

    var opened = new List<OpenTarget>();
    try {
      foreach (var member in topology.MetadataTargets.Concat(topology.StorageTargets)) {
        var source = _sources.Single(source =>
          string.Equals(source.Name, member.SourceName, StringComparison.OrdinalIgnoreCase));
        source.Stream.Position = 0;
        var session = FormatRegistry.OpenFilesystem(
          member.BackingFormatId,
          source.Stream,
          new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
        var root = ResolveDirectory(session, session.RootNodeId, member.RootPath, member.SourceName);
        opened.Add(new OpenTarget(member, session, root));
      }

      _targets = opened.ToArray();
      var profile = BuildProfile(topology);
      var (nodes, entries, rootNodeId) = BuildLogicalSnapshot();
      _namespace = new ReadOnlyFilesystemSnapshotSession(profile, rootNodeId, nodes, entries);
    } catch {
      foreach (var target in opened)
        target.Session.Dispose();
      throw;
    }
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
    foreach (var target in _targets)
      target.Session.Dispose();
    if (!_leaveOpen)
      foreach (var stream in _sources.Select(source => source.Stream).Distinct(ReferenceEqualityComparer.Instance))
        stream.Dispose();
  }

  private (FilesystemSnapshotNode[] Nodes, FilesystemSnapshotDirectoryEntry[] Entries, FilesystemNodeId RootNodeId)
      BuildLogicalSnapshot() {
    var metadataTargets = _targets
      .Where(target => target.Member.Role == FilesystemSourceRole.Metadata)
      .ToDictionary(target => target.Member.NumericId);
    var storageTargets = _targets
      .Where(target => target.Member.Role == FilesystemSourceRole.Data)
      .ToDictionary(target => checked((ushort)target.Member.NumericId));

    var contentOwners = new Dictionary<string, uint>(StringComparer.Ordinal);
    var dentries = new List<DiscoveredDentry>();
    foreach (var target in metadataTargets.Values)
      ScanMetadataTarget(target, contentOwners, dentries);

    if (!contentOwners.ContainsKey(RootEntryId))
      throw new InvalidDataException(
        "BeeGFS metadata target set contains no dentries hash directory for the logical root EntryID 'root'.");

    var nodeIds = new Dictionary<string, FilesystemNodeId>(StringComparer.Ordinal);
    var entryIdByNode = new Dictionary<FilesystemNodeId, string>();
    FilesystemNodeId NodeIdFor(string entryId) {
      if (nodeIds.TryGetValue(entryId, out var existing)) return existing;
      var id = StableNodeId(entryId);
      if (entryIdByNode.TryGetValue(id, out var collision) &&
          !string.Equals(collision, entryId, StringComparison.Ordinal))
        throw new InvalidDataException(
          $"BeeGFS EntryIDs '{collision}' and '{entryId}' collide in the mounted node-id projection.");
      nodeIds[entryId] = id;
      entryIdByNode[id] = entryId;
      return id;
    }

    var rootId = NodeIdFor(RootEntryId);
    var nodesByEntryId = new Dictionary<string, FilesystemSnapshotNode>(StringComparer.Ordinal) {
      [RootEntryId] = new FilesystemSnapshotNode(
        rootId, default, string.Empty, FilesystemNodeKind.Directory,
        Size: 0, AllocatedSize: 0,
        LinkCount: 1,
        NativeAttributes: contentOwners[RootEntryId]),
    };
    var links = new List<FilesystemSnapshotDirectoryEntry>(dentries.Count);
    var logicalNames = new HashSet<(string Parent, string Name)>();

    foreach (var dentry in dentries) {
      if (!logicalNames.Add((dentry.ParentEntryId, dentry.Name)))
        throw new InvalidDataException(
          $"BeeGFS namespace contains duplicate name '{dentry.Name}' below EntryID '{dentry.ParentEntryId}'.");

      var metadata = dentry.Metadata;
      if (metadata.Kind == FilesystemNodeKind.Directory) {
        if (metadata.MetadataType != BeeGfsDiskMetadataType.DirectoryDentry || metadata.StorageFormatVersion != 3)
          throw new NotSupportedException(
            $"BeeGFS directory '{dentry.Name}' is not represented by the supported V3 directory-dentry profile.");
        if (!metadataTargets.ContainsKey(metadata.OwnerNodeId))
          throw new InvalidDataException(
            $"BeeGFS directory EntryID '{metadata.EntryId}' names missing metadata owner node {metadata.OwnerNodeId}.");
        if (!contentOwners.TryGetValue(metadata.EntryId, out var physicalOwner))
          throw new InvalidDataException(
            $"BeeGFS directory EntryID '{metadata.EntryId}' has no content directory in the supplied metadata targets.");
        if (physicalOwner != metadata.OwnerNodeId)
          throw new InvalidDataException(
            $"BeeGFS directory EntryID '{metadata.EntryId}' says owner node {metadata.OwnerNodeId}, " +
            $"but its content directory is on metadata node {physicalOwner}.");
      } else if (metadata.Kind != FilesystemNodeKind.RegularFile) {
        throw new NotSupportedException(
          $"BeeGFS namespace entry '{dentry.Name}' has kind {metadata.Kind}; " +
          "the initial multi-target reader supports directories and regular files only.");
      } else {
        if (metadata.StorageFormatVersion != 6 || !metadata.HasInlineInode || metadata.Raid0Pattern == null)
          throw new NotSupportedException(
            $"BeeGFS file '{dentry.Name}' is not a supported V6 inline RAID0 file inode.");
        foreach (var targetId in metadata.Raid0Pattern.TargetIds)
          if (!storageTargets.ContainsKey(targetId))
            throw new InvalidDataException(
              $"BeeGFS file EntryID '{metadata.EntryId}' references storage target {targetId}, " +
              "which is absent from the source set.");
      }

      var nodeId = NodeIdFor(metadata.EntryId);
      if (!nodesByEntryId.TryGetValue(metadata.EntryId, out var node)) {
        node = metadata.Kind switch {
          FilesystemNodeKind.Directory => new FilesystemSnapshotNode(
            nodeId,
            NodeIdFor(dentry.ParentEntryId),
            dentry.Name,
            FilesystemNodeKind.Directory,
            Size: 0,
            AllocatedSize: 0,
            LinkCount: 1,
            NativeAttributes: metadata.OwnerNodeId),
          FilesystemNodeKind.RegularFile => BuildFileNode(
            nodeId,
            NodeIdFor(dentry.ParentEntryId),
            dentry.Name,
            metadata,
            storageTargets),
          _ => throw new InvalidOperationException("Unsupported BeeGFS logical node kind reached node construction."),
        };
        nodesByEntryId.Add(metadata.EntryId, node);
      } else if (node.Kind != metadata.Kind || node.Size != Math.Max(0, metadata.Size)) {
        throw new InvalidDataException(
          $"BeeGFS hard-link aliases for EntryID '{metadata.EntryId}' disagree on object metadata.");
      }

      links.Add(new FilesystemSnapshotDirectoryEntry(NodeIdFor(dentry.ParentEntryId), dentry.Name, nodeId));
    }

    foreach (var dentry in dentries)
      if (!nodesByEntryId.ContainsKey(dentry.ParentEntryId))
        throw new InvalidDataException(
          $"BeeGFS namespace entry '{dentry.Name}' references missing parent EntryID '{dentry.ParentEntryId}'.");

    foreach (var (contentEntryId, _) in contentOwners)
      if (contentEntryId != RootEntryId && !nodesByEntryId.TryGetValue(contentEntryId, out var node))
        throw new InvalidDataException(
          $"BeeGFS metadata contains an orphan content directory for unknown EntryID '{contentEntryId}'.");
      else if (contentEntryId != RootEntryId && node.Kind != FilesystemNodeKind.Directory)
        throw new InvalidDataException(
          $"BeeGFS content directory EntryID '{contentEntryId}' resolves to non-directory kind {node.Kind}.");

    return (nodesByEntryId.Values.ToArray(), links.ToArray(), rootId);
  }

  private FilesystemSnapshotNode BuildFileNode(
      FilesystemNodeId nodeId,
      FilesystemNodeId parentNodeId,
      string name,
      BeeGfsDecodedDentry metadata,
      IReadOnlyDictionary<ushort, OpenTarget> storageTargets) {
    var pattern = metadata.Raid0Pattern!;
    var chunkPath = BeeGfsChunkLayout.BuildChunkRelativePath(
      metadata.OriginalUserId,
      metadata.OriginalParentEntryId,
      metadata.EntryId);
    var chunks = new BeeGfsTargetChunkBinding?[pattern.TargetIds.Count];

    for (var index = 0; index < pattern.TargetIds.Count; ++index) {
      var required = RequiredTargetLength(metadata.Size, pattern.ChunkSize, pattern.TargetIds.Count, index);
      if (required == 0) continue;
      var target = storageTargets[pattern.TargetIds[index]];
      var chunksRoot = ResolveDirectory(target.Session, target.RootNodeId, "chunks", target.Member.SourceName);
      var chunkNode = ResolvePath(target.Session, chunksRoot, chunkPath, target.Member.SourceName)
        ?? throw new FileNotFoundException(
          $"BeeGFS file EntryID '{metadata.EntryId}' is missing chunk '{chunkPath}' " +
          $"on storage target {pattern.TargetIds[index]}.");
      var stat = target.Session.Stat(chunkNode);
      if (stat.Kind != FilesystemNodeKind.RegularFile)
        throw new InvalidDataException(
          $"BeeGFS chunk '{chunkPath}' on storage target {pattern.TargetIds[index]} is not a regular file.");
      if (stat.Size < required)
        throw new EndOfStreamException(
          $"BeeGFS chunk '{chunkPath}' on storage target {pattern.TargetIds[index]} has {stat.Size} bytes; " +
          $"{required} are required by logical file size {metadata.Size}.");
      chunks[index] = new BeeGfsTargetChunkBinding(target.Session, chunkNode, required);
    }

    var capturedChunks = chunks;
    var capturedPattern = pattern;
    return new FilesystemSnapshotNode(
      nodeId,
      parentNodeId,
      name,
      FilesystemNodeKind.RegularFile,
      metadata.Size,
      metadata.Size,
      LinkCount: Math.Max(1u, metadata.LinkCount),
      NativeAttributes: metadata.Mode,
      OpenReadHandle: () => new BeeGfsRaid0FileHandle(
        nodeId, metadata.Size, capturedPattern, capturedChunks));
  }

  private static void ScanMetadataTarget(
      OpenTarget target,
      IDictionary<string, uint> contentOwners,
      ICollection<DiscoveredDentry> dentries) {
    var dentriesRoot = ResolveDirectory(target.Session, target.RootNodeId, "dentries", target.Member.SourceName);
    foreach (var level1 in target.Session.Enumerate(dentriesRoot)
               .Where(entry => entry.Kind == FilesystemNodeKind.Directory)) {
      foreach (var level2 in target.Session.Enumerate(level1.NodeId)
                 .Where(entry => entry.Kind == FilesystemNodeKind.Directory)) {
        foreach (var content in target.Session.Enumerate(level2.NodeId)
                   .Where(entry => entry.Kind == FilesystemNodeKind.Directory)) {
          var parentEntryId = content.Name;
          if (parentEntryId == EntryIdDirectoryName)
            throw new InvalidDataException(
              $"BeeGFS metadata source '{target.Member.SourceName}' has '#fSiDs#' at the hash-directory level " +
              "instead of inside a content directory.");
          if (contentOwners.TryGetValue(parentEntryId, out var existingOwner) &&
              existingOwner != target.Member.NumericId)
            throw new InvalidDataException(
              $"BeeGFS content directory EntryID '{parentEntryId}' appears on metadata nodes " +
              $"{existingOwner} and {target.Member.NumericId}; buddy mirroring is not enabled in the current reader.");
          contentOwners[parentEntryId] = target.Member.NumericId;

          foreach (var physicalEntry in target.Session.Enumerate(content.NodeId)) {
            if (physicalEntry.Name == EntryIdDirectoryName) continue;
            if (physicalEntry.Kind != FilesystemNodeKind.RegularFile)
              throw new InvalidDataException(
                $"BeeGFS physical dentry '{physicalEntry.Name}' below parent EntryID '{parentEntryId}' " +
                "is not a regular metadata file.");
            var metadataBytes = ReadDentryMetadata(
              target.Session, physicalEntry.NodeId, target.Member.SourceName, physicalEntry.Name);
            var decoded = BeeGfsMetadataCodec.ParseDentry(metadataBytes, parentEntryId);
            dentries.Add(new DiscoveredDentry(parentEntryId, physicalEntry.Name, decoded));
          }
        }
      }
    }
  }

  private static byte[] ReadDentryMetadata(
      IFilesystemSession session,
      FilesystemNodeId nodeId,
      string sourceName,
      string entryName) {
    if (session is IFilesystemExtendedAttributeReader attributes) {
      try {
        var all = attributes.ReadExtendedAttributes(nodeId);
        if (all.TryGetValue(MetadataXattrName, out var metadata)) {
          if (metadata.Length is < 8 or > MaxMetadataBytes)
            throw new InvalidDataException(
              $"BeeGFS dentry '{entryName}' in source '{sourceName}' has implausible " +
              $"{MetadataXattrName} length {metadata.Length}.");
          return metadata;
        }
      } catch (NotSupportedException) {
        // BeeGFS can store the same metadata in the file body. Fall back only
        // when the backing driver cannot read this xattr storage form.
      }
    }

    var stat = session.Stat(nodeId);
    if (stat.Size is < 8 or > MaxMetadataBytes)
      throw new InvalidDataException(
        $"BeeGFS dentry '{entryName}' in source '{sourceName}' has no readable {MetadataXattrName} " +
        $"and body size {stat.Size} is outside 8..{MaxMetadataBytes} bytes.");
    using var handle = session.OpenFile(nodeId, FileAccess.Read);
    var data = new byte[checked((int)stat.Size)];
    ReadExactly(handle, data, sourceName, entryName);
    return data;
  }

  private static FilesystemNodeId ResolveDirectory(
      IFilesystemSession session,
      FilesystemNodeId start,
      string path,
      string sourceName) {
    var node = ResolvePath(session, start, path, sourceName)
      ?? throw new DirectoryNotFoundException(
        $"BeeGFS source '{sourceName}' path '{path}' does not exist in its backing filesystem.");
    if (session.Stat(node).Kind != FilesystemNodeKind.Directory)
      throw new DirectoryNotFoundException(
        $"BeeGFS source '{sourceName}' path '{path}' is not a directory.");
    return node;
  }

  private static FilesystemNodeId? ResolvePath(
      IFilesystemSession session,
      FilesystemNodeId start,
      string path,
      string sourceName) {
    var current = start;
    foreach (var component in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      if (component is "." or "..")
        throw new InvalidDataException(
          $"BeeGFS source '{sourceName}' path '{path}' contains forbidden component '{component}'.");
      var child = session.Lookup(current, component);
      if (child == null) return null;
      current = child.Value;
    }
    return current;
  }

  private static long RequiredTargetLength(long fileSize, uint chunkSize, int targetCount, int targetIndex) {
    if (fileSize <= 0) return 0;
    var fullChunks = fileSize / chunkSize;
    var tail = fileSize % chunkSize;
    var assignedFullChunks = fullChunks / targetCount + (targetIndex < fullChunks % targetCount ? 1 : 0);
    var result = checked(assignedFullChunks * chunkSize);
    if (tail != 0 && targetIndex == fullChunks % targetCount)
      result = checked(result + tail);
    return result;
  }

  private static FilesystemNodeId StableNodeId(string entryId) {
    const ulong offset = 14695981039346656037UL;
    const ulong prime = 1099511628211UL;
    var hash = offset;
    foreach (var value in Encoding.UTF8.GetBytes(entryId)) {
      hash ^= value;
      hash *= prime;
    }
    return new FilesystemNodeId(hash == 0 ? 1UL : hash, Generation: 1);
  }

  private static void ReadExactly(
      IFilesystemFileHandle handle,
      Span<byte> destination,
      string sourceName,
      string entryName) {
    var done = 0;
    while (done < destination.Length) {
      var read = handle.Read(done, destination[done..]);
      if (read <= 0)
        throw new EndOfStreamException(
          $"BeeGFS source '{sourceName}' metadata file '{entryName}' ended after " +
          $"{done} of {destination.Length} bytes.");
      done += read;
    }
  }

  private static FilesystemDriverProfile BuildProfile(BeeGfsTargetTopology topology) => new(
    "BeeGfs",
    $"BeeGFS offline V3/V6 non-mirrored RAID0 ({topology.MetadataTargets.Count} metadata, " +
    $"{topology.StorageTargets.Count} storage)",
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
      "Read-only support is limited to quiescent non-mirrored BeeGFS snapshots with V3 directory dentries, V6 inline regular-file inodes, RAID0 stripe patterns and non-sparse local chunks.",
      "Hard-linked non-inline file inodes, symbolic/special files, buddy/legacy mirroring, sparse chunk-block vectors, remote-storage targets and unsupported metadata versions fail closed.",
      "Writable mounting remains disabled until metadata/chunk allocation, target mappings, buddy consistency, durability/recovery and concurrency are transactional across the complete target set.",
    ]);
}

internal sealed class BeeGfsRaid0FileHandle : IFilesystemFileHandle {
  private readonly BeeGfsRaid0Pattern _pattern;
  private readonly IFilesystemFileHandle?[] _chunks;
  private bool _disposed;

  public BeeGfsRaid0FileHandle(
      FilesystemNodeId nodeId,
      long length,
      BeeGfsRaid0Pattern pattern,
      IReadOnlyList<BeeGfsTargetChunkBinding?> chunkBindings) {
    ArgumentNullException.ThrowIfNull(pattern);
    ArgumentNullException.ThrowIfNull(chunkBindings);
    if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
    if (chunkBindings.Count != pattern.TargetIds.Count)
      throw new ArgumentException(
        "BeeGFS chunk binding count must match the RAID0 target vector.", nameof(chunkBindings));

    NodeId = nodeId;
    Length = length;
    _pattern = pattern;
    _chunks = new IFilesystemFileHandle?[chunkBindings.Count];
    try {
      for (var i = 0; i < chunkBindings.Count; ++i) {
        var binding = chunkBindings[i];
        if (binding == null) continue;
        var handle = binding.Session.OpenFile(binding.NodeId, FileAccess.Read);
        if (handle.Length < binding.RequiredLength) {
          var actual = handle.Length;
          handle.Dispose();
          throw new EndOfStreamException(
            $"BeeGFS storage chunk {i} has {actual} bytes; {binding.RequiredLength} are required.");
        }
        _chunks[i] = handle;
      }
    } catch {
      foreach (var handle in _chunks) handle?.Dispose();
      throw;
    }
  }

  public FilesystemNodeId NodeId { get; }
  public long Length { get; }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (offset >= Length || destination.Length == 0) return 0;
    var wanted = checked((int)Math.Min(destination.Length, Length - offset));
    var total = 0;
    while (total < wanted) {
      var logicalOffset = offset + total;
      var targetIndex = BeeGfsChunkLayout.TargetIndex(
        logicalOffset, _pattern.ChunkSize, _pattern.TargetIds.Count);
      var handle = _chunks[targetIndex]
        ?? throw new EndOfStreamException(
          $"BeeGFS logical offset {logicalOffset} maps to absent storage target " +
          $"{_pattern.TargetIds[targetIndex]}.");
      var localOffset = BeeGfsChunkLayout.TargetLocalOffset(
        logicalOffset, _pattern.ChunkSize, _pattern.TargetIds.Count);
      var segment = BeeGfsChunkLayout.BytesUntilNextChunk(
        logicalOffset, _pattern.ChunkSize, wanted - total);
      var copied = 0;
      while (copied < segment) {
        var read = handle.Read(
          localOffset + copied,
          destination.Slice(total + copied, segment - copied));
        if (read <= 0)
          throw new EndOfStreamException(
            $"BeeGFS storage target {_pattern.TargetIds[targetIndex]} ended while reading " +
            $"logical offset {logicalOffset + copied}.");
        copied += read;
      }
      total += copied;
    }
    return total;
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("BeeGFS multi-target sessions are read-only.");

  public void SetLength(long length)
    => throw new NotSupportedException("BeeGFS multi-target sessions are read-only.");

  public void Flush() => ThrowIfDisposed();

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    foreach (var handle in _chunks)
      handle?.Dispose();
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
