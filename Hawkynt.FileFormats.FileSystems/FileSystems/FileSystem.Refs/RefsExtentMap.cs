#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Refs;

/// <summary>
/// Enumerates the active ReFS byte layout. Free space is fail-closed: a gap is
/// free only when covered and clear in the on-disk allocator. ReFS structures
/// are emitted individually so a filesystem-specific metadata mover can place
/// movable pages while fixed bootstrap anchors remain hard reservations.
/// </summary>
public static class RefsExtentMap {
  private readonly record struct ClusterRun(ulong Start, ulong Count) {
    public ulong End => checked(this.Start + this.Count);
  }

  private sealed record NamedRun(string Name, ClusterRun Run);
  private sealed record FileRun(string Path, ClusterRun Run, bool Movable);

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek || image.Length < 512) yield break;

    RefsMetadataReader metadata;
    IReadOnlyList<RefsFileRecord> files;
    RefsAllocatorMap.State allocator;
    RefsMetadataGraph graph;
    RefsBootstrapState bootstrap;
    try {
      metadata = RefsMetadataReader.Open(image);
      files = new RefsNamespaceReader(metadata).ReadAll();
      graph = new RefsMetadataGraph(image, metadata);
      bootstrap = RefsBootstrapState.Open(image);
      allocator = RefsAllocatorMap.Read(metadata);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException) {
      yield break;
    }

    var clusterSize = metadata.ClusterSize;
    var totalClusters = (ulong)(image.Length / clusterSize);
    if (totalClusters == 0) yield break;
    var pageClusters = checked((ulong)(metadata.PageSize / clusterSize));

    var fixedRuns = new List<NamedRun> {
      new(RefsMetadataNames.Vbr, new ClusterRun(0, 1)),
      new(RefsMetadataNames.PrimarySuperblock, new ClusterRun(0x1E, 1)),
      new(RefsMetadataNames.TailVbr, new ClusterRun(totalClusters - 1, 1)),
    };
    if (totalClusters >= 3) {
      fixedRuns.Add(new NamedRun(RefsMetadataNames.BackupSuperblock1, new ClusterRun(totalClusters - 2, 1)));
      fixedRuns.Add(new NamedRun(RefsMetadataNames.BackupSuperblock2, new ClusterRun(totalClusters - 3, 1)));
    }

    var metadataRuns = new List<NamedRun>();
    foreach (var page in graph.Pages)
      foreach (var run in CoalesceSlots(page.PhysicalSlots))
        metadataRuns.Add(new NamedRun(RefsMetadataNames.Page(page.PhysicalHead), run));

    foreach (var checkpoint in bootstrap.CheckpointLcns) {
      if (checkpoint >= totalClusters) continue;
      var count = Math.Min(pageClusters, totalClusters - checkpoint);
      if (count > 0)
        metadataRuns.Add(new NamedRun(RefsMetadataNames.Checkpoint(checkpoint), new ClusterRun(checkpoint, count)));
    }

    var fileRuns = new List<FileRun>();
    foreach (var file in files) {
      if (file.IsDirectory || file.Extents.Count == 0) continue;
      var movable = file.Extents.All(e =>
        !e.IsSparse && e.ClusterCount > 0 && e.Flags is 0x180040 or 0x180050);

      foreach (var extent in file.Extents) {
        if (extent.IsSparse || extent.ClusterCount == 0) continue;
        foreach (var run in TranslateExtent(metadata, extent, totalClusters))
          fileRuns.Add(new FileRun(file.Path, run, movable));
      }
    }

    var knownRuns = new List<ClusterRun>();
    knownRuns.AddRange(fixedRuns.Select(r => r.Run));
    knownRuns.AddRange(metadataRuns.Select(r => r.Run));
    knownRuns.AddRange(fileRuns.Select(f => f.Run));
    var knownMerged = Coalesce(knownRuns);

    var allocatorAllocated = allocator.Allocated
      .Select(r => new ClusterRun(r.Start, r.Count))
      .Where(r => r.Count > 0 && r.Start < totalClusters)
      .Select(r => r.End > totalClusters ? new ClusterRun(r.Start, totalClusters - r.Start) : r)
      .ToList();
    var allocatorCovered = allocator.Covered
      .Select(r => new ClusterRun(r.Start, r.Count))
      .Where(r => r.Count > 0 && r.Start < totalClusters)
      .Select(r => r.End > totalClusters ? new ClusterRun(r.Start, totalClusters - r.Start) : r)
      .ToList();

    // Allocated but not decoded is still a real owner: MLog, snapshot/shared
    // data, feature metadata, or a structure not decoded yet. Keep it pinned
    // and visible rather than silently treating it as free.
    foreach (var run in Subtract(Coalesce(allocatorAllocated), knownMerged))
      yield return ToBlock(run, clusterSize, image.Length, DefragBlockKind.MetadataReserved, "$ReFS/allocated/unclassified");

    // The generic extent-map contract treats every omitted byte as free, so
    // allocator-uncovered space must be stated explicitly as reserved.
    foreach (var run in Subtract(Complement(Coalesce(allocatorCovered), totalClusters), knownMerged))
      yield return ToBlock(run, clusterSize, image.Length, DefragBlockKind.MetadataReserved, "$ReFS/allocator-uncovered");

    foreach (var item in fixedRuns)
      yield return ToBlock(item.Run, clusterSize, image.Length, DefragBlockKind.MetadataReserved, item.Name);

    foreach (var item in metadataRuns)
      yield return ToBlock(item.Run, clusterSize, image.Length, DefragBlockKind.MetadataReserved, item.Name);

    foreach (var fileRun in fileRuns)
      yield return ToBlock(
        fileRun.Run,
        clusterSize,
        image.Length,
        fileRun.Movable ? DefragBlockKind.Used : DefragBlockKind.MetadataReserved,
        fileRun.Movable ? fileRun.Path : $"$ReFS/pinned-stream/{fileRun.Path}");
  }

  private static IEnumerable<ClusterRun> TranslateExtent(
      RefsMetadataReader metadata,
      RefsDataExtent extent,
      ulong totalClusters) {
    ulong? start = null;
    ulong previous = 0;
    for (uint i = 0; i < extent.ClusterCount; ++i) {
      ulong physical = 0;
      var translated = false;
      try {
        physical = metadata.TranslateVirtualLcn(checked(extent.VirtualLcn + i));
        translated = true;
      } catch (Exception e) when (e is InvalidDataException or OverflowException) { }

      if (!translated) {
        if (start is not null) {
          yield return new ClusterRun(start.Value, previous - start.Value + 1);
          start = null;
        }
        continue;
      }
      if (physical >= totalClusters) continue;
      if (start is null) {
        start = previous = physical;
        continue;
      }
      if (physical == previous + 1) {
        previous = physical;
        continue;
      }
      yield return new ClusterRun(start.Value, previous - start.Value + 1);
      start = previous = physical;
    }
    if (start is not null)
      yield return new ClusterRun(start.Value, previous - start.Value + 1);
  }

  private static IEnumerable<ClusterRun> CoalesceSlots(IReadOnlyList<ulong> slots) {
    if (slots.Count == 0) yield break;
    var start = slots[0];
    var previous = slots[0];
    for (var i = 1; i < slots.Count; ++i) {
      if (slots[i] == previous + 1) {
        previous = slots[i];
        continue;
      }
      yield return new ClusterRun(start, previous - start + 1);
      start = previous = slots[i];
    }
    yield return new ClusterRun(start, previous - start + 1);
  }

  private static DefragBlockInfo ToBlock(
      ClusterRun run,
      int clusterSize,
      long imageLength,
      DefragBlockKind kind,
      string name) {
    var offset = checked((long)run.Start * clusterSize);
    var length = checked((long)run.Count * clusterSize);
    if (offset + length > imageLength) length = imageLength - offset;
    return new DefragBlockInfo(offset, Math.Max(0, length), kind, name);
  }

  private static List<ClusterRun> Coalesce(IEnumerable<ClusterRun> source) {
    var ordered = source.Where(r => r.Count > 0).OrderBy(r => r.Start).ToArray();
    if (ordered.Length == 0) return [];
    var result = new List<ClusterRun>();
    var start = ordered[0].Start;
    var end = ordered[0].End;
    for (var i = 1; i < ordered.Length; ++i) {
      var run = ordered[i];
      if (run.Start <= end) {
        if (run.End > end) end = run.End;
        continue;
      }
      result.Add(new ClusterRun(start, end - start));
      start = run.Start;
      end = run.End;
    }
    result.Add(new ClusterRun(start, end - start));
    return result;
  }

  private static List<ClusterRun> Complement(IReadOnlyList<ClusterRun> covered, ulong totalClusters) {
    var result = new List<ClusterRun>();
    ulong cursor = 0;
    foreach (var run in covered) {
      if (run.Start > cursor) result.Add(new ClusterRun(cursor, run.Start - cursor));
      if (run.End > cursor) cursor = run.End;
      if (cursor >= totalClusters) break;
    }
    if (cursor < totalClusters) result.Add(new ClusterRun(cursor, totalClusters - cursor));
    return result;
  }

  private static List<ClusterRun> Subtract(
      IReadOnlyList<ClusterRun> source,
      IReadOnlyList<ClusterRun> remove) {
    var result = new List<ClusterRun>();
    var removeIndex = 0;
    foreach (var run in source) {
      var cursor = run.Start;
      var end = run.End;
      while (removeIndex < remove.Count && remove[removeIndex].End <= cursor) ++removeIndex;
      var i = removeIndex;
      while (i < remove.Count && remove[i].Start < end) {
        var cut = remove[i];
        if (cut.Start > cursor) result.Add(new ClusterRun(cursor, cut.Start - cursor));
        if (cut.End > cursor) cursor = cut.End;
        if (cursor >= end) break;
        ++i;
      }
      if (cursor < end) result.Add(new ClusterRun(cursor, end - cursor));
    }
    return result;
  }
}
