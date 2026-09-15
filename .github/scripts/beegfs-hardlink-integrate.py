from pathlib import Path

path = Path('Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.BeeGfs/BeeGfsReadOnlyFilesystemSession.cs')
text = path.read_text(encoding='utf-8')

text = text.replace(
    '/// non-mirrored V3 directory dentries plus V6 inline regular-file inodes using RAID0,\n/// with non-sparse local chunks. Unsupported metadata fails closed.',
    '/// non-mirrored V3 directory/file dentries plus V6 regular-file inodes (inline or\n/// separately stored for hard links) using RAID0, with non-sparse local chunks. Unsupported metadata fails closed.',
    1)

marker = '''  private sealed record DiscoveredDentry(\n    string ParentEntryId,\n    string Name,\n    BeeGfsDecodedDentry Metadata);\n'''
addition = marker + '''\n  private sealed record DiscoveredInode(\n    uint OwnerNodeId,\n    string EntryId,\n    IFilesystemSession Session,\n    FilesystemNodeId NodeId,\n    string SourceName);\n'''
if marker not in text:
  raise SystemExit('DiscoveredDentry marker not found')
text = text.replace(marker, addition, 1)

start = text.index('  private (FilesystemSnapshotNode[] Nodes, FilesystemSnapshotDirectoryEntry[] Entries, FilesystemNodeId RootNodeId)\n      BuildLogicalSnapshot() {')
end = text.index('\n  private FilesystemSnapshotNode BuildFileNode(', start)
build = r'''  private (FilesystemSnapshotNode[] Nodes, FilesystemSnapshotDirectoryEntry[] Entries, FilesystemNodeId RootNodeId)
      BuildLogicalSnapshot() {
    var metadataTargets = _targets
      .Where(target => target.Member.Role == FilesystemSourceRole.Metadata)
      .ToDictionary(target => target.Member.NumericId);
    var storageTargets = _targets
      .Where(target => target.Member.Role == FilesystemSourceRole.Data)
      .ToDictionary(target => checked((ushort)target.Member.NumericId));

    var contentOwners = new Dictionary<string, uint>(StringComparer.Ordinal);
    var dentries = new List<DiscoveredDentry>();
    var inodeObjects = new Dictionary<(uint OwnerNodeId, string EntryId), DiscoveredInode>();
    foreach (var target in metadataTargets.Values) {
      ScanInodeTarget(target, inodeObjects);
      ScanMetadataTarget(target, contentOwners, dentries);
    }

    if (!contentOwners.ContainsKey(RootEntryId))
      throw new InvalidDataException(
        "BeeGFS metadata target set contains no dentries hash directory for the logical root EntryID 'root'.");

    var decodedSeparateInodes = new Dictionary<(uint OwnerNodeId, string EntryId), BeeGfsDecodedDentry>();
    BeeGfsDecodedDentry ResolveRegularFileMetadata(DiscoveredDentry dentry) {
      var metadata = dentry.Metadata;
      if (metadata.StorageFormatVersion == 6 && metadata.HasInlineInode) {
        if (metadata.MetadataType != BeeGfsDiskMetadataType.FileDentry || metadata.Raid0Pattern == null)
          throw new NotSupportedException(
            $"BeeGFS file '{dentry.Name}' is not a supported V6 inline RAID0 file inode.");
        return metadata;
      }

      if (metadata.StorageFormatVersion != 3 ||
          metadata.MetadataType != BeeGfsDiskMetadataType.FileDentry ||
          metadata.HasInlineInode)
        throw new NotSupportedException(
          $"BeeGFS file '{dentry.Name}' is neither a supported V6 inline inode nor a V3 dentry referencing a separate inode.");

      if (!metadataTargets.ContainsKey(metadata.OwnerNodeId))
        throw new InvalidDataException(
          $"BeeGFS file EntryID '{metadata.EntryId}' names missing metadata owner node {metadata.OwnerNodeId}.");

      var key = (metadata.OwnerNodeId, metadata.EntryId);
      if (decodedSeparateInodes.TryGetValue(key, out var cached))
        return cached;

      if (!inodeObjects.TryGetValue(key, out var inodeObject))
        throw new FileNotFoundException(
          $"BeeGFS file EntryID '{metadata.EntryId}' has no separate inode object on declared metadata owner node {metadata.OwnerNodeId}.");

      var inodeBytes = ReadMetadataObject(
        inodeObject.Session,
        inodeObject.NodeId,
        inodeObject.SourceName,
        inodeObject.EntryId,
        "file inode");
      var decoded = BeeGfsMetadataCodec.ParseDentry(inodeBytes, dentry.ParentEntryId);
      if (decoded.MetadataType != BeeGfsDiskMetadataType.FileInode ||
          decoded.StorageFormatVersion != 6 ||
          decoded.Kind != FilesystemNodeKind.RegularFile ||
          decoded.HasInlineInode ||
          decoded.Raid0Pattern == null)
        throw new NotSupportedException(
          $"BeeGFS separate inode '{metadata.EntryId}' is not a supported non-inline V6 regular-file RAID0 inode.");
      if (!string.Equals(decoded.EntryId, metadata.EntryId, StringComparison.Ordinal))
        throw new InvalidDataException(
          $"BeeGFS V3 dentry EntryID '{metadata.EntryId}' resolves to separate inode EntryID '{decoded.EntryId}'.");

      decodedSeparateInodes.Add(key, decoded);
      return decoded;
    }

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
          "the current multi-target reader supports directories and regular files only.");
      } else {
        metadata = ResolveRegularFileMetadata(dentry);
        foreach (var targetId in metadata.Raid0Pattern!.TargetIds)
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

    foreach (var contentEntryId in contentOwners.Keys) {
      if (contentEntryId == RootEntryId) continue;
      if (!nodesByEntryId.TryGetValue(contentEntryId, out var node))
        throw new InvalidDataException(
          $"BeeGFS metadata contains an orphan content directory for unknown EntryID '{contentEntryId}'.");
      if (node.Kind != FilesystemNodeKind.Directory)
        throw new InvalidDataException(
          $"BeeGFS content directory EntryID '{contentEntryId}' resolves to non-directory kind {node.Kind}.");
    }

    return (nodesByEntryId.Values.ToArray(), links.ToArray(), rootId);
  }
'''
text = text[:start] + build + text[end:]

start = text.index('  private static void ScanMetadataTarget(')
end = text.index('\n  private static FilesystemNodeId ResolveDirectory(', start)
scan = r'''  private static void ScanInodeTarget(
      OpenTarget target,
      IDictionary<(uint OwnerNodeId, string EntryId), DiscoveredInode> inodeObjects) {
    var inodesRoot = ResolveDirectory(target.Session, target.RootNodeId, "inodes", target.Member.SourceName);
    foreach (var level1 in target.Session.Enumerate(inodesRoot)
               .Where(entry => entry.Kind == FilesystemNodeKind.Directory)) {
      foreach (var level2 in target.Session.Enumerate(level1.NodeId)
                 .Where(entry => entry.Kind == FilesystemNodeKind.Directory)) {
        foreach (var inodeEntry in target.Session.Enumerate(level2.NodeId)) {
          if (inodeEntry.Kind != FilesystemNodeKind.RegularFile)
            throw new InvalidDataException(
              $"BeeGFS inode object '{inodeEntry.Name}' in metadata source '{target.Member.SourceName}' is not a regular metadata file.");
          var key = (target.Member.NumericId, inodeEntry.Name);
          if (!inodeObjects.TryAdd(key, new DiscoveredInode(
                target.Member.NumericId,
                inodeEntry.Name,
                target.Session,
                inodeEntry.NodeId,
                target.Member.SourceName)))
            throw new InvalidDataException(
              $"BeeGFS metadata source '{target.Member.SourceName}' contains duplicate inode object EntryID '{inodeEntry.Name}'.");
        }
      }
    }
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
            var metadataBytes = ReadMetadataObject(
              target.Session,
              physicalEntry.NodeId,
              target.Member.SourceName,
              physicalEntry.Name,
              "dentry");
            var decoded = BeeGfsMetadataCodec.ParseDentry(metadataBytes, parentEntryId);
            dentries.Add(new DiscoveredDentry(parentEntryId, physicalEntry.Name, decoded));
          }
        }
      }
    }
  }

  private static byte[] ReadMetadataObject(
      IFilesystemSession session,
      FilesystemNodeId nodeId,
      string sourceName,
      string entryName,
      string objectKind) {
    if (session is IFilesystemExtendedAttributeReader attributes) {
      try {
        var all = attributes.ReadExtendedAttributes(nodeId);
        if (all.TryGetValue(MetadataXattrName, out var metadata)) {
          if (metadata.Length is < 8 or > MaxMetadataBytes)
            throw new InvalidDataException(
              $"BeeGFS {objectKind} '{entryName}' in source '{sourceName}' has implausible " +
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
        $"BeeGFS {objectKind} '{entryName}' in source '{sourceName}' has no readable {MetadataXattrName} " +
        $"and body size {stat.Size} is outside 8..{MaxMetadataBytes} bytes.");
    using var handle = session.OpenFile(nodeId, FileAccess.Read);
    var data = new byte[checked((int)stat.Size)];
    ReadExactly(handle, data, sourceName, entryName);
    return data;
  }
'''
text = text[:start] + scan + text[end:]

text = text.replace(
    '"Read-only support is limited to quiescent non-mirrored BeeGFS snapshots with V3 directory dentries, V6 inline regular-file inodes, RAID0 stripe patterns and non-sparse local chunks.",\n      "Hard-linked non-inline file inodes, symbolic/special files, buddy/legacy mirroring, sparse chunk-block vectors, remote-storage targets and unsupported metadata versions fail closed.",',
    '"Read-only support is limited to quiescent non-mirrored BeeGFS snapshots with V3 directory/file dentries, V6 regular-file inodes (inline or separate), RAID0 stripe patterns and non-sparse local chunks.",\n      "Symbolic/special files, buddy/legacy mirroring, sparse chunk-block vectors, remote-storage targets and unsupported metadata versions fail closed.",',
    1)

path.write_text(text, encoding='utf-8')
