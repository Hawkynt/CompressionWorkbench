#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Refs;

/// <summary>
/// Offline-quiescent ReFS 3.x regular-file editor.
///
/// This is deliberately not a mounted-driver transaction layer. It operates on an
/// unmounted image, reopens the active metadata graph between logical edits, uses
/// allocator-verified storage for replacement data, and uses the existing immutable
/// CoW B+ engine + alternate checkpoint publisher for namespace deletion so parent
/// separator keys remain correct even when a leaf disappears.
///
/// Supported profile:
/// - replace existing regular files whose live stream layout is resident or an
///   ordinary non-sparse/non-integrity/non-shared extent holder understood by
///   <see cref="RefsStreamLayoutEditor"/>;
/// - remove regular files and empty directories;
/// - release old ordinary data extents after the namespace/data repoint is live.
///
/// New-name insertion remains fail-closed until the directory-entry value template
/// (file identity/security/link semantics) is derived for every writable ReFS 3.x
/// profile. Replacing an existing name through <see cref="Add"/> is fully supported.
/// </summary>
internal static class RefsOfflineModifier {
  private const ulong RootDirectoryOid = 0x600;

  public static void Add(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    RequireWritableImage(image);

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var path = NormalizePath(input.ArchiveName);
      if (path.Length == 0)
        throw new ArgumentException("ReFS entry path must not be empty.", nameof(inputs));

      var metadata = RefsMetadataReader.Open(image);
      var existing = new RefsNamespaceReader(metadata).ReadAll().FirstOrDefault(f =>
        !f.IsDirectory && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
      if (existing == null)
        throw new NotSupportedException(
          $"ReFS offline R/W currently replaces existing regular files; creating the new namespace entry '{path}' " +
          "is withheld until all file-identity/security/link fields are proven for the active ReFS profile.");

      ReplaceExisting(image, path, input.ReadContent());
    }
  }

  public static void Remove(Stream image, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);
    RequireWritableImage(image);

    foreach (var raw in entryNames) {
      var path = NormalizePath(raw);
      if (path.Length == 0) continue;
      RemoveOne(image, path);
    }
  }

  private static void ReplaceExisting(Stream image, string path, byte[] data) {
    var metadata = RefsMetadataReader.Open(image);
    var files = new RefsNamespaceReader(metadata).ReadAll();
    var file = files.FirstOrDefault(f =>
      !f.IsDirectory && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))
      ?? throw new FileNotFoundException($"ReFS file '{path}' is no longer reachable.", path);
    if (file.Extents.Any(e => e.IsSparse || e.Flags == 0x1C00D0 || (e.Flags & 0x04) != 0))
      throw new NotSupportedException(
        $"ReFS file '{path}' uses sparse/integrity/shared allocation semantics outside the offline CRUD profile.");

    var graph = new RefsMetadataGraph(image, metadata);
    var writable = new RefsWritableNamespace(metadata);
    var location = writable.ResolveStorage(path);

    var clusterSize = metadata.ClusterSize;
    var blocks = checked((data.LongLength + clusterSize - 1) / clusterSize);
    if (blocks > int.MaxValue)
      throw new NotSupportedException("ReFS replacement exceeds the supported allocation-run size.");

    var targets = blocks == 0
      ? Array.Empty<ulong>()
      : SelectContiguousFreeDataRun(metadata, graph, checked((int)blocks));
    var targetOffsets = targets.Select(lcn => checked((long)lcn * clusterSize)).ToArray();
    var extents = RefsStreamLayoutEditor.BuildExtents(metadata, targetOffsets);
    var allocatedBytes = checked((long)targets.Length * clusterSize);
    var replacementFile = file with {
      Size = data.LongLength,
      AllocatedSize = allocatedBytes,
    };
    var replacementValue = RefsStreamLayoutEditor.BuildUpdatedValue(
      replacementFile,
      location.StorageRow,
      extents,
      clusterSize);

    if (!RefsPageEditor.CanReplaceValue(graph, location.StorageRow, replacementValue.Length))
      throw new NotSupportedException(
        $"ReFS replacement for '{path}' would require an outer B+ page split; this offline value-repoint path refuses before allocating data.");

    RefsBTreeRow? shortEntry = null;
    byte[]? shortEntryValue = null;
    if (location.UsesBackingRow) {
      shortEntry = writable.FindDirectoryEntry(path);
      if (shortEntry.Value.Length < 0x40)
        throw new InvalidDataException("ReFS short directory entry is too small for size/allocation fields.");
      shortEntryValue = shortEntry.Value.ToArray();
      BinaryPrimitives.WriteUInt64LittleEndian(shortEntryValue.AsSpan(0x30, 8), checked((ulong)allocatedBytes));
      BinaryPrimitives.WriteUInt64LittleEndian(shortEntryValue.AsSpan(0x38, 8), checked((ulong)data.LongLength));
      if (shortEntryValue.Length >= 0x20)
        BinaryPrimitives.WriteUInt64LittleEndian(shortEntryValue.AsSpan(0x18, 8), checked((ulong)DateTime.UtcNow.ToFileTimeUtc()));
      if (!RefsPageEditor.CanReplaceValue(graph, shortEntry, shortEntryValue.Length))
        throw new NotSupportedException(
          $"ReFS parent entry for '{path}' cannot be updated without an outer B+ split.");
    }

    var newClaimed = false;
    var metadataRepointed = false;
    try {
      if (targets.Length > 0) {
        var allocator = FindAllocator(metadata, graph, targets[0]);
        if (!targets.All(allocator.CoversPhysical))
          throw new InvalidDataException("ReFS replacement run crosses allocator ownership boundaries.");
        allocator.SetAllocated(targets, allocated: true);
        image.Flush();
        newClaimed = true;
        WriteData(image, data, targets, clusterSize);
        image.Flush();
      }

      var changedPages = new HashSet<ulong> {
        RefsPageEditor.ReplaceValue(graph, location.StorageRow, replacementValue),
      };
      metadataRepointed = true;
      if (shortEntry != null && shortEntryValue != null)
        changedPages.Add(RefsPageEditor.ReplaceValue(graph, shortEntry, shortEntryValue));
      graph.RefreshChecksumPaths(changedPages);
      image.Flush();
    } catch {
      // Before the stream metadata points at the new allocation, the new run is
      // merely an orphan reservation and can be released. After the repoint, a
      // leak is safer than freeing bytes that may already be reachable.
      if (newClaimed && !metadataRepointed)
        TryReleaseAllocation(image, targets);
      throw;
    }

    ReleaseOldData(image, file);
  }

  private static void RemoveOne(Stream image, string path) {
    var metadata = RefsMetadataReader.Open(image);
    var files = new RefsNamespaceReader(metadata).ReadAll();
    var file = files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))
      ?? throw new FileNotFoundException($"ReFS entry '{path}' was not found.", path);

    if (file.IsDirectory && files.Any(f =>
          !string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)
          && f.Path.StartsWith(path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
      throw new IOException($"ReFS directory '{path}' is not empty.");
    if (!file.IsDirectory && file.Extents.Any(e => e.IsSparse || e.Flags == 0x1C00D0 || (e.Flags & 0x04) != 0))
      throw new NotSupportedException(
        $"ReFS file '{path}' uses sparse/integrity/shared allocation semantics outside the offline CRUD profile.");

    var parent = ResolveParentDirectory(metadata, path);
    var writable = new RefsWritableNamespace(metadata);
    var entry = writable.FindDirectoryEntry(path);
    var keys = new List<byte[]> { entry.Key.ToArray() };
    if (!file.IsDirectory && file.Backing != null)
      keys.Add(file.Backing.Row.Key.ToArray());

    var store = new RefsCowPageStore(image, metadata);
    var tree = new RefsCowBTree(image, metadata, store);
    var parentTree = tree.Rewrite(parent.Root, virtualAddresses: true, (rows, comparer) => {
      var removed = 0;
      foreach (var key in keys) {
        var index = FindKey(rows, key, comparer);
        if (index < 0) continue;
        rows.RemoveAt(index);
        ++removed;
      }
      if (removed != keys.Count)
        throw new InvalidDataException(
          $"ReFS namespace/storage rows for '{path}' changed before deletion could be materialized.");
      return true;
    });

    var objectEditor = new RefsCowObjectEditor(metadata, tree);
    var objectTable = objectEditor.ReplaceObjectRoot(parent.ObjectId, parentTree.RootReference);
    PublishOfflineCheckpoint(image, metadata, store, objectTable);

    if (!file.IsDirectory)
      ReleaseOldData(image, file);
  }

  /// <summary>
  /// Publishes immutable namespace/Object-Table pages and the allocator roots that
  /// account for their newly reserved metadata pages. There is intentionally no
  /// synthetic MLog redo record here: this is the offline-quiescent transaction
  /// boundary, not the native mounted-driver crash-recovery path.
  /// </summary>
  private static void PublishOfflineCheckpoint(
      Stream image,
      RefsMetadataReader metadata,
      RefsCowPageStore store,
      RefsCowTreeResult objectTable) {
    var roots = new Dictionary<int, byte[]> { [0] = objectTable.RootReference };
    var allocatorChanged = false;
    foreach (var tier in new[] {
               RefsAllocatorTier.Medium,
               RefsAllocatorTier.Container,
               RefsAllocatorTier.Small,
             }) {
      if (store.GetReservedClusters(tier).Count == 0) continue;
      var publication = new RefsCowAllocatorPublisher(image, metadata, store).Publish(tier);
      roots[publication.RootIndex] = publication.Tree.RootReference;
      allocatorChanged = true;
    }

    var committer = new RefsCheckpointCommitter(image);
    var prepared = committer.PrepareNext();
    committer.SetRootReferences(prepared, roots);
    committer.Commit(prepared, allocatorChanged: allocatorChanged);
  }

  private static ParentDirectory ResolveParentDirectory(RefsMetadataReader metadata, string path) {
    var parts = NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) throw new ArgumentException("ReFS path is empty.", nameof(path));

    var objects = BuildObjectMap(metadata);
    if (!objects.TryGetValue(RootDirectoryOid, out var root))
      throw new InvalidDataException("ReFS root directory object is absent from the Object Table.");
    var oid = RootDirectoryOid;

    for (var i = 0; i < parts.Length - 1; ++i) {
      RefsBTreeRow? found = null;
      foreach (var row in metadata.WalkTree(root, virtualAddresses: true)) {
        if (row.Key.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(row.Key.AsSpan(0, 2)) != 0x30)
          continue;
        var name = DecodeName(row.Key.AsSpan(4));
        if (!string.Equals(name, parts[i], StringComparison.OrdinalIgnoreCase)) continue;
        if (found != null)
          throw new InvalidDataException($"ReFS directory '{parts[i]}' is ambiguous.");
        found = row;
      }
      if (found == null || found.Value.Length < 0x44)
        throw new DirectoryNotFoundException($"ReFS parent directory component '{parts[i]}' was not found.");
      var attributes = BinaryPrimitives.ReadUInt32LittleEndian(found.Value.AsSpan(0x40, 4));
      if ((attributes & 0x10000000) == 0)
        throw new DirectoryNotFoundException($"ReFS path component '{parts[i]}' is not a directory.");
      oid = BinaryPrimitives.ReadUInt64LittleEndian(found.Value.AsSpan(0x08, 8));
      if (!objects.TryGetValue(oid, out root))
        throw new InvalidDataException($"ReFS child directory OID 0x{oid:X} has no Object Table root.");
    }
    return new ParentDirectory(oid, root);
  }

  private static Dictionary<ulong, RefsPageReference> BuildObjectMap(RefsMetadataReader metadata) {
    var result = new Dictionary<ulong, RefsPageReference>();
    foreach (var row in metadata.WalkRoot(0)) {
      if (row.Key.Length < 16 || row.Value.Length < 0x20 + metadata.PageReferenceSize) continue;
      var oid = BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(8, 8));
      var reference = RefsPageReference.Parse(row.Value.AsSpan(0x20));
      if (reference.Lcns.Count > 0) result[oid] = reference;
    }
    return result;
  }

  private static int FindKey(IReadOnlyList<RefsTreeRow> rows, byte[] key, RefsKeyComparer comparer) {
    for (var i = 0; i < rows.Count; ++i)
      if (comparer.Compare(rows[i].Key, key) == 0) return i;
    return -1;
  }

  private static ulong[] SelectContiguousFreeDataRun(
      RefsMetadataReader metadata,
      RefsMetadataGraph graph,
      int count) {
    var medium = new RefsAllocatorWriter(metadata, graph, RefsAllocatorTier.Medium);
    if (!medium.TryFindFreeRun(count, out var start))
      throw new IOException($"ReFS Medium Allocator has no verified contiguous free run of {count:N0} cluster(s).");
    var result = new ulong[count];
    for (var i = 0; i < count; ++i) result[i] = checked(start + (ulong)i);
    return result;
  }

  private static RefsAllocatorWriter FindAllocator(
      RefsMetadataReader metadata,
      RefsMetadataGraph graph,
      ulong physicalLcn) {
    foreach (var tier in new[] {
               RefsAllocatorTier.Medium,
               RefsAllocatorTier.Container,
               RefsAllocatorTier.Small,
             }) {
      var writer = new RefsAllocatorWriter(metadata, graph, tier);
      if (writer.CoversPhysical(physicalLcn)) return writer;
    }
    throw new InvalidDataException($"No ReFS allocator tier covers PLCN 0x{physicalLcn:X}.");
  }

  private static void WriteData(Stream image, byte[] data, IReadOnlyList<ulong> targets, int clusterSize) {
    var cursor = 0;
    var buffer = new byte[clusterSize];
    foreach (var lcn in targets) {
      buffer.AsSpan().Clear();
      var take = Math.Min(clusterSize, data.Length - cursor);
      if (take > 0) data.AsSpan(cursor, take).CopyTo(buffer);
      image.Position = checked((long)lcn * clusterSize);
      image.Write(buffer);
      cursor += take;
    }
    if (cursor != data.Length)
      throw new IOException("ReFS replacement allocation did not receive every source byte.");
  }

  private static void ReleaseOldData(Stream image, RefsFileRecord file) {
    var old = ExpandPhysicalClusters(file.Extents).Distinct().ToArray();
    if (old.Length == 0) return;

    var metadata = RefsMetadataReader.Open(image);
    var graph = new RefsMetadataGraph(image, metadata);
    var releasable = new RefsBlockRefcount(metadata, graph).DetachPhysicalReferences(old);
    image.Flush();
    if (releasable.Count == 0) return;

    var fresh = RefsMetadataReader.Open(image);
    var freshGraph = new RefsMetadataGraph(image, fresh);
    var remaining = releasable.ToHashSet();
    foreach (var tier in new[] {
               RefsAllocatorTier.Medium,
               RefsAllocatorTier.Container,
               RefsAllocatorTier.Small,
             }) {
      var writer = new RefsAllocatorWriter(fresh, freshGraph, tier);
      var covered = remaining.Where(writer.CoversPhysical).ToArray();
      if (covered.Length == 0) continue;
      writer.SetAllocated(covered, allocated: false);
      foreach (var lcn in covered) remaining.Remove(lcn);
      image.Flush();
    }
    // Unknown allocator ownership is intentionally leaked rather than guessed free.
  }

  private static IEnumerable<ulong> ExpandPhysicalClusters(IEnumerable<RefsDataExtent> extents) {
    foreach (var extent in extents) {
      if (extent.IsSparse) continue;
      for (uint i = 0; i < extent.ClusterCount; ++i)
        yield return checked(extent.PhysicalLcn + i);
    }
  }

  private static void TryReleaseAllocation(Stream image, IReadOnlyList<ulong> targets) {
    try {
      if (targets.Count == 0) return;
      var metadata = RefsMetadataReader.Open(image);
      var graph = new RefsMetadataGraph(image, metadata);
      var remaining = targets.ToHashSet();
      foreach (var tier in new[] {
                 RefsAllocatorTier.Medium,
                 RefsAllocatorTier.Container,
                 RefsAllocatorTier.Small,
               }) {
        var writer = new RefsAllocatorWriter(metadata, graph, tier);
        var covered = remaining.Where(writer.CoversPhysical).ToArray();
        if (covered.Length == 0) continue;
        writer.SetAllocated(covered, allocated: false);
        foreach (var lcn in covered) remaining.Remove(lcn);
      }
      image.Flush();
    } catch {
      // An allocation leak is safer than masking the original exception or
      // freeing a range whose publication state became uncertain.
    }
  }

  private static void RequireWritableImage(Stream image) {
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("ReFS offline mutation requires a readable, writable, seekable unmounted image stream.", nameof(image));
  }

  private static string NormalizePath(string path)
    => (path ?? string.Empty).Replace('\\', '/').Trim('/');

  private static string DecodeName(ReadOnlySpan<byte> bytes) {
    try { return Encoding.Unicode.GetString(bytes).TrimEnd('\0'); }
    catch { return Convert.ToHexString(bytes); }
  }

  private sealed record ParentDirectory(ulong ObjectId, RefsPageReference Root);
}
