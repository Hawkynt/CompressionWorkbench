#pragma warning disable CS1591

namespace FileSystem.Refs;

internal sealed record RefsOfflineCloneResult(
  long LogicalSize,
  int SharedClusterCount,
  ulong CheckpointPhysicalLcn,
  ulong CheckpointClock);

/// <summary>
/// Performs the bounded offline ReFS block-clone operation for two existing,
/// equal-sized ordinary extent-backed files. The destination's stream mapping is
/// replaced by the source mapping through immutable object/Object-Table CoW,
/// while root #6 gains the corresponding shared-cluster reference counts in the
/// same alternate-checkpoint publication.
///
/// This is deliberately narrower than FSCTL_DUPLICATE_EXTENTS_TO_FILE: it clones
/// a whole cluster-aligned file only, does not create names or extend EOF, and
/// refuses resident, sparse, integrity, already-shared and otherwise undecoded
/// stream layouts. Native mounted-driver/MLog semantics remain fail-closed.
/// </summary>
internal static class RefsOfflineBlockCloner {
  public static RefsOfflineCloneResult CloneWholeFile(
      Stream image,
      string sourcePath,
      string destinationPath) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("ReFS offline block clone requires a readable, writable, seekable image.", nameof(image));

    sourcePath = NormalizePath(sourcePath, nameof(sourcePath));
    destinationPath = NormalizePath(destinationPath, nameof(destinationPath));
    if (sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException("ReFS block clone source and destination must be different files.", nameof(destinationPath));

    var metadata = RefsMetadataReader.Open(image);
    var files = new RefsNamespaceReader(metadata).ReadAll();
    var source = FindRegularFile(files, sourcePath, nameof(sourcePath));
    var destination = FindRegularFile(files, destinationPath, nameof(destinationPath));
    ValidateWholeFileProfile(metadata, source, destination);

    var sourcePhysical = ExpandPhysicalClusters(metadata, source);
    var destinationPhysical = ExpandPhysicalClusters(metadata, destination);
    if (sourcePhysical.Count != destinationPhysical.Count)
      throw new InvalidDataException("ReFS whole-file clone source and destination have different allocation coverage.");
    if (sourcePhysical.Count == 0)
      throw new NotSupportedException("ReFS zero-length files do not need block-clone metadata.");
    if (sourcePhysical.Distinct().Count() != sourcePhysical.Count)
      throw new NotSupportedException("ReFS source file already aliases a physical cluster inside its own extent map.");
    if (destinationPhysical.Distinct().Count() != destinationPhysical.Count)
      throw new NotSupportedException("ReFS destination file already aliases a physical cluster inside its own extent map.");
    if (sourcePhysical.Intersect(destinationPhysical).Any())
      throw new NotSupportedException("ReFS whole-file clone refuses source/destination mappings that already overlap.");

    var refcounts = new RefsBlockRefcount(metadata);
    EnsureUnshared(refcounts, sourcePhysical, "source");
    EnsureUnshared(refcounts, destinationPhysical, "destination");

    var sourceOffsets = sourcePhysical
      .Select(lcn => checked((long)lcn * metadata.ClusterSize))
      .ToArray();
    var clonedExtents = RefsStreamLayoutEditor.BuildExtents(metadata, sourceOffsets);

    var store = new RefsCowPageStore(image, metadata);
    var tree = new RefsCowBTree(image, metadata, store);
    var objectEditor = new RefsCowObjectEditor(metadata, tree);
    var objectMutation = objectEditor.UpdateStorageValue(
      destinationPath,
      location => RefsStreamLayoutEditor.BuildUpdatedValue(
        destination,
        location.StorageRow,
        clonedExtents,
        metadata.ClusterSize));

    var refcountTree = new RefsCowBlockRefcountEditor(metadata, tree)
      .IncrementPhysicalReferences(sourcePhysical);

    var checkpoint = PublishOfflineCheckpoint(
      image,
      metadata,
      store,
      new Dictionary<int, byte[]> {
        [0] = objectMutation.ObjectTableTree.RootReference,
        [6] = refcountTree.RootReference,
      });

    // The clone is already durable at this point. Releasing the destination's
    // former ordinary allocation is post-commit reclamation; inability to prove
    // allocator ownership leaves a safe leak rather than risking live data.
    TryReleaseFormerDestination(image, destinationPhysical);
    VerifyClone(image, sourcePath, destinationPath, sourcePhysical);

    return new RefsOfflineCloneResult(
      source.Size,
      sourcePhysical.Count,
      checkpoint.ActiveCheckpointLcn,
      checkpoint.ActiveCheckpointClock);
  }

  private static RefsMetadataReader PublishOfflineCheckpoint(
      Stream image,
      RefsMetadataReader metadata,
      RefsCowPageStore store,
      IReadOnlyDictionary<int, byte[]> replacements) {
    if (!replacements.ContainsKey(0) || !replacements.ContainsKey(6))
      throw new ArgumentException("ReFS block clone must publish Object Table root #0 and Block Refcount root #6 together.", nameof(replacements));

    var roots = replacements.ToDictionary(item => item.Key, item => item.Value.ToArray());
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
    image.Flush();

    var verify = RefsMetadataReader.Open(image);
    foreach (var rootIndex in new[] { 0, 6 }) {
      var expected = RefsPageReference.Parse(roots[rootIndex]);
      var actual = verify.Roots[rootIndex];
      if (!expected.Lcns.SequenceEqual(actual.Lcns))
        throw new IOException($"ReFS clone checkpoint published but root #{rootIndex} does not match the replacement tree.");
    }
    return verify;
  }

  private static void VerifyClone(
      Stream image,
      string sourcePath,
      string destinationPath,
      IReadOnlyList<ulong> expectedPhysical) {
    var metadata = RefsMetadataReader.Open(image);
    var files = new RefsNamespaceReader(metadata).ReadAll();
    var source = FindRegularFile(files, sourcePath, nameof(sourcePath));
    var destination = FindRegularFile(files, destinationPath, nameof(destinationPath));
    var sourcePhysical = ExpandPhysicalClusters(metadata, source);
    var destinationPhysical = ExpandPhysicalClusters(metadata, destination);
    if (!sourcePhysical.SequenceEqual(expectedPhysical) || !destinationPhysical.SequenceEqual(expectedPhysical))
      throw new IOException("ReFS block clone checkpoint is live but source/destination extent mappings do not match.");

    var refcounts = new RefsBlockRefcount(metadata);
    foreach (var physical in expectedPhysical) {
      if (!refcounts.TryGetPhysical(physical, out var entry) || entry.ReferenceCount != 2)
        throw new IOException($"ReFS block clone PLCN 0x{physical:X} does not have the expected reference count 2.");
    }
  }

  private static void TryReleaseFormerDestination(Stream image, IReadOnlyList<ulong> physicalClusters) {
    try {
      var metadata = RefsMetadataReader.Open(image);
      var graph = new RefsMetadataGraph(image, metadata);
      var remaining = new RefsBlockRefcount(metadata, graph)
        .DetachPhysicalReferences(physicalClusters)
        .ToHashSet();
      if (remaining.Count == 0) return;

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
        image.Flush();
      }
      // Unknown ownership remains allocated deliberately.
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or InvalidOperationException) {
      // The metadata clone was committed first. Reclamation failure is a safe
      // allocation leak and must not turn a durable clone into a false rollback.
    }
  }

  private static void ValidateWholeFileProfile(
      RefsMetadataReader metadata,
      RefsFileRecord source,
      RefsFileRecord destination) {
    if (source.IsResident || destination.IsResident)
      throw new NotSupportedException("ReFS whole-file block clone currently requires two extent-backed files.");
    if (source.Size != destination.Size)
      throw new NotSupportedException("ReFS whole-file block clone requires equal source/destination logical sizes; EOF extension is not implemented.");
    if (source.Size <= 0 || source.Size % metadata.ClusterSize != 0)
      throw new NotSupportedException("ReFS whole-file block clone currently requires a non-empty cluster-aligned logical size.");
    if (source.AllocatedSize != source.Size || destination.AllocatedSize != destination.Size)
      throw new NotSupportedException("ReFS whole-file block clone requires fully allocated, non-sparse stream coverage.");
    if (source.Extents.Count == 0 || destination.Extents.Count == 0)
      throw new InvalidDataException("ReFS extent-backed clone candidate has no decoded data extents.");
    if (source.Extents.Any(IsUnsupportedExtent) || destination.Extents.Any(IsUnsupportedExtent))
      throw new NotSupportedException("ReFS whole-file block clone refuses sparse, integrity, snapshot/shared or otherwise special extents.");
  }

  private static bool IsUnsupportedExtent(RefsDataExtent extent)
    => extent.IsSparse || extent.Flags == 0x1C00D0 || (extent.Flags & 0x04) != 0;

  private static IReadOnlyList<ulong> ExpandPhysicalClusters(
      RefsMetadataReader metadata,
      RefsFileRecord file) {
    var result = new List<ulong>();
    uint expectedVcn = 0;
    foreach (var extent in file.Extents.OrderBy(item => item.FileVcn)) {
      if (extent.FileVcn != expectedVcn)
        throw new InvalidDataException($"ReFS file '{file.Path}' extent map is not a contiguous VCN cover.");
      for (uint i = 0; i < extent.ClusterCount; ++i)
        result.Add(metadata.TranslateVirtualLcn(checked(extent.VirtualLcn + i)));
      expectedVcn = checked(expectedVcn + extent.ClusterCount);
    }

    var expectedClusters = checked((int)(file.Size / metadata.ClusterSize));
    if (result.Count != expectedClusters)
      throw new InvalidDataException(
        $"ReFS file '{file.Path}' extent map covers {result.Count} cluster(s), expected {expectedClusters}.");
    return result;
  }

  private static void EnsureUnshared(
      RefsBlockRefcount refcounts,
      IEnumerable<ulong> physicalClusters,
      string role) {
    foreach (var physical in physicalClusters)
      if (refcounts.TryGetPhysical(physical, out var entry) && entry.IsShared)
        throw new NotSupportedException(
          $"ReFS whole-file clone {role} PLCN 0x{physical:X} is already shared/dedup-managed; repeated clone integration is a separate profile.");
  }

  private static RefsFileRecord FindRegularFile(
      IEnumerable<RefsFileRecord> files,
      string path,
      string parameterName) {
    var file = files.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
      ?? throw new FileNotFoundException($"ReFS file '{path}' was not found.", path);
    if (file.IsDirectory)
      throw new ArgumentException($"ReFS path '{path}' names a directory.", parameterName);
    return file;
  }

  private static string NormalizePath(string path, string parameterName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.IndexOf('\0') >= 0))
      throw new ArgumentException("ReFS clone path must be a canonical relative filesystem path.", parameterName);
    return string.Join('/', parts);
  }
}
