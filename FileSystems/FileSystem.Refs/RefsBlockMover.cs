#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Refs;

/// <summary>
/// Offline ReFS 3.x data mover. Raw bytes are rearranged first; allocation,
/// stream metadata, and Block Refcount ownership are then committed as
/// allocate-new -> repoint -> detach-old-reference -> release-unowned-old.
/// Metadata checksum ancestry is refreshed to CHKP after each metadata commit.
/// </summary>
public sealed class RefsBlockMover : IFilesystemBlockMover {
  private readonly int _clusterSize;

  /// <summary>
  /// Initializes a new instance of <see cref="RefsBlockMover"/>.
  /// </summary>
  public RefsBlockMover(Stream image) {
    var metadata = RefsMetadataReader.Open(image);
    this._clusterSize = metadata.ClusterSize;
  }

  /// <summary>
  /// Gets the allocation block size.
  /// </summary>
  public int AllocationBlockSize => this._clusterSize;
  /// <summary>
  /// Gets a value indicating whether supports scattered relink.
  /// </summary>
  public bool SupportsScatteredRelink => true;
  /// <summary>
  /// Gets a value indicating whether supports held runs.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource) Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var blocks = checked((int)((length + this._clusterSize - 1) / this._clusterSize));
    var oldBlocks = new long[blocks];
    var newBlocks = new long[blocks];
    for (var i = 0; i < blocks; ++i) {
      oldBlocks[i] = checked(oldOffset + (long)i * this._clusterSize);
      newBlocks[i] = checked(newOffset + (long)i * this._clusterSize);
    }
    this.UpdateAllocationScattered(image, fileName, oldBlocks, newBlocks, null);
  }

  /// <summary>
  /// Performs the update allocation scattered operation.
  /// </summary>
  public void UpdateAllocationScattered(
      Stream image,
      string fileName,
      IReadOnlyList<long> oldBlockOffsets,
      IReadOnlyList<long> newBlockOffsets,
      IReadOnlySet<long>? blocksLiveElsewhere) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (oldBlockOffsets.Count != newBlockOffsets.Count)
      throw new InvalidOperationException("ReFS old/new allocation block counts differ.");
    if (newBlockOffsets.Count == 0) return;

    var metadata = RefsMetadataReader.Open(image);
    if (metadata.ClusterSize != this._clusterSize)
      throw new InvalidOperationException("ReFS allocation geometry changed during defragmentation.");
    var namespaceReader = new RefsNamespaceReader(metadata);
    var file = namespaceReader.ReadAll().FirstOrDefault(f =>
      !f.IsDirectory && f.Path.Equals(fileName, StringComparison.OrdinalIgnoreCase))
      ?? throw new InvalidOperationException($"ReFS file '{fileName}' is no longer reachable.");
    if (file.IsResident)
      throw new InvalidOperationException($"ReFS resident file '{fileName}' was not promoted before relocation planning.");

    var writable = new RefsWritableNamespace(metadata);
    var storageRow = file.Backing?.Row ?? writable.FindDirectoryEntry(file.Path);
    var replacementExtents = RefsStreamLayoutEditor.BuildExtents(metadata, newBlockOffsets);
    var replacementValue = RefsStreamLayoutEditor.BuildUpdatedValue(file, storageRow, replacementExtents, this._clusterSize);
    var graph = new RefsMetadataGraph(image, metadata);
    if (!RefsPageEditor.CanReplaceValue(graph, storageRow, replacementValue.Length))
      throw new InvalidOperationException(
        $"ReFS allocation for '{fileName}' needs {replacementExtents.Count} extent rows and would require a B+ page split.");

    var newPhysical = ToPhysicalLcns(newBlockOffsets, this._clusterSize);
    var oldPhysical = ToPhysicalLcns(oldBlockOffsets, this._clusterSize);

    // 1. Claim every destination first. A destination that another file still
    // owns in the old layout is already set and therefore remains set.
    new RefsAllocatorWriter(metadata, graph).SetAllocated(newPhysical, allocated: true);
    image.Flush();

    // 2. Repoint the file's live stream allocation and refresh its checksum
    // ancestry. Until this succeeds, the old stream map is still authoritative.
    try {
      var changed = RefsPageEditor.ReplaceValue(graph, storageRow, replacementValue);
      graph.RefreshChecksumPaths([changed]);
      image.Flush();
    } catch {
      // Destinations are only orphan allocations at this point. Keep the old
      // file reachable and best-effort release claims that were genuinely free.
      TryReleaseOrphans(image, newPhysical, oldPhysical);
      throw;
    }

    // 3. Remove this stream's ownership of old blocks only when the final
    // layout no longer uses that physical cluster. Block Refcount is updated
    // before the allocation bitmap. Shared/snapshot/dedup-owned blocks are not
    // returned to the allocator merely because this one stream moved away.
    var live = new HashSet<ulong>(newPhysical);
    if (blocksLiveElsewhere != null)
      foreach (var offset in blocksLiveElsewhere)
        if (offset >= 0 && offset % this._clusterSize == 0)
          live.Add(checked((ulong)(offset / this._clusterSize)));
    var detached = oldPhysical.Where(c => !live.Contains(c)).Distinct().ToArray();
    if (detached.Length > 0) {
      var fresh = RefsMetadataReader.Open(image);
      var freshGraph = new RefsMetadataGraph(image, fresh);
      var releasable = new RefsBlockRefcount(fresh, freshGraph).DetachPhysicalReferences(detached);
      image.Flush();
      if (releasable.Count > 0) {
        var afterRefcount = RefsMetadataReader.Open(image);
        var afterRefcountGraph = new RefsMetadataGraph(image, afterRefcount);
        new RefsAllocatorWriter(afterRefcount, afterRefcountGraph).SetAllocated(releasable, allocated: false);
        image.Flush();
      }
    }
  }

  /// <summary>
  /// Converts every non-empty resident file to the extent-backed long-value
  /// form before the planner runs, so small files participate in exactly the
  /// same physical layout operation as large files.
  /// </summary>
  public void PrepareResidentFiles(Stream image) {
    while (true) {
      var metadata = RefsMetadataReader.Open(image);
      var files = new RefsNamespaceReader(metadata).ReadAll();
      var file = files.FirstOrDefault(f => !f.IsDirectory && f.IsResident && f.Size > 0);
      if (file == null) return;
      if (file.ResidentContent == null || file.ResidentContent.LongLength != file.Size)
        throw new InvalidDataException($"ReFS resident bytes for '{file.Path}' could not be reconstructed exactly.");

      var blocksNeeded = checked((int)((file.Size + metadata.ClusterSize - 1) / metadata.ClusterSize));
      var targets = SelectFreeClusters(metadata, blocksNeeded);
      if (targets.Count != blocksNeeded)
        throw new InvalidOperationException($"ReFS has no allocator-verified space to promote resident file '{file.Path}'.");
      var targetOffsets = targets.Select(c => checked((long)c * metadata.ClusterSize)).ToArray();
      var extents = RefsStreamLayoutEditor.BuildExtents(metadata, targetOffsets);
      var row = new RefsWritableNamespace(metadata).FindDirectoryEntry(file.Path);
      var value = RefsStreamLayoutEditor.BuildUpdatedValue(file, row, extents, metadata.ClusterSize);
      var graph = new RefsMetadataGraph(image, metadata);
      if (!RefsPageEditor.CanReplaceValue(graph, row, value.Length))
        throw new InvalidOperationException(
          $"ReFS resident promotion for '{file.Path}' would require a B+ page split; refusing before allocating data.");

      var allocator = new RefsAllocatorWriter(metadata, graph);
      allocator.SetAllocated(targets, allocated: true);
      image.Flush();
      try {
        WriteResidentBytes(image, file.ResidentContent, targets, metadata.ClusterSize);
        var changed = RefsPageEditor.ReplaceValue(graph, row, value);
        graph.RefreshChecksumPaths([changed]);
        image.Flush();
      } catch {
        TryReleaseOrphans(image, targets, []);
        throw;
      }
    }
  }

  /// <summary>
  /// Conservative global extent-run budget for interleave planning. It probes
  /// each current storage row and returns the minimum run count that fits
  /// without an outer B+ page split.
  /// </summary>
  public int GetMaximumExtentRuns(Stream image) {
    var metadata = RefsMetadataReader.Open(image);
    var files = new RefsNamespaceReader(metadata).ReadAll();
    var writable = new RefsWritableNamespace(metadata);
    var graph = new RefsMetadataGraph(image, metadata);
    var limit = 4096;
    foreach (var file in files.Where(f => !f.IsDirectory && !f.IsResident && f.Extents.Count > 0)) {
      if (file.Extents.Any(e => e.IsSparse || e.Flags == 0x1C00D0 || (e.Flags & 0x04) != 0)) continue;
      var row = file.Backing?.Row ?? writable.FindDirectoryEntry(file.Path);
      var blocks = checked((int)file.Extents.Sum(e => (long)e.ClusterCount));
      var hi = Math.Min(4096, Math.Max(1, blocks));
      var lo = 1;
      var best = 0;
      while (lo <= hi) {
        var mid = lo + ((hi - lo) >> 1);
        var dummy = new RefsExtentSpec[mid];
        for (var i = 0; i < mid; ++i) dummy[i] = new RefsExtentSpec((uint)i, (ulong)(0x10000 + i * 2), 1);
        try {
          var value = RefsStreamLayoutEditor.BuildUpdatedValue(file, row, dummy, this._clusterSize);
          if (RefsPageEditor.CanReplaceValue(graph, row, value.Length)) {
            best = mid;
            lo = mid + 1;
          } else hi = mid - 1;
        } catch (NotSupportedException) {
          best = 0;
          break;
        }
      }
      if (best > 0) limit = Math.Min(limit, best);
    }
    return Math.Max(1, limit);
  }

  private static List<ulong> SelectFreeClusters(RefsMetadataReader metadata, int count) {
    var state = RefsAllocatorMap.Read(metadata);
    var result = new List<ulong>(count);
    foreach (var run in state.Free) {
      for (ulong i = 0; i < run.Count && result.Count < count; ++i)
        result.Add(run.Start + i);
      if (result.Count == count) break;
    }
    return result;
  }

  private static void WriteResidentBytes(Stream image, byte[] data, IReadOnlyList<ulong> targets, int clusterSize) {
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
    if (cursor != data.Length) throw new InvalidDataException("ReFS resident promotion did not write every source byte.");
  }

  private static ulong[] ToPhysicalLcns(IReadOnlyList<long> offsets, int clusterSize) {
    var result = new ulong[offsets.Count];
    for (var i = 0; i < offsets.Count; ++i) {
      if (offsets[i] < 0 || offsets[i] % clusterSize != 0)
        throw new InvalidOperationException("ReFS allocation block is not cluster aligned.");
      result[i] = checked((ulong)(offsets[i] / clusterSize));
    }
    return result;
  }

  private static void TryReleaseOrphans(Stream image, IEnumerable<ulong> candidates, IEnumerable<ulong> keep) {
    try {
      var protectedSet = keep.ToHashSet();
      var release = candidates.Where(c => !protectedSet.Contains(c)).Distinct().ToArray();
      if (release.Length == 0) return;
      var metadata = RefsMetadataReader.Open(image);
      var graph = new RefsMetadataGraph(image, metadata);
      new RefsAllocatorWriter(metadata, graph).SetAllocated(release, allocated: false);
      image.Flush();
    } catch {
      // Leaking an orphan allocation is preferable to masking the original
      // failure with a second exception or freeing reachable file data.
    }
  }
}
