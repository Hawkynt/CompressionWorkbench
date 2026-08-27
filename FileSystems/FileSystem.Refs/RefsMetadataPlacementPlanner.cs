#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Refs;

/// <summary>
/// Places ReFS filesystem structures through the allocator tier that owns each
/// structure. The generic block planner has one global free-space view; ReFS
/// cannot use that for bootstrap metadata because Medium, Container and Small
/// allocator ownership is not interchangeable.
/// </summary>
internal sealed class RefsMetadataPlacementPlanner {
  private readonly Stream _image;

  public RefsMetadataPlacementPlanner(Stream image) {
    this._image = image;
  }

  public int Execute(DefragOptions options) {
    if (options.MetadataZonePlacement == MetadataZone.Unchanged) return 0;

    var initialMetadata = RefsMetadataReader.Open(this._image);
    var clusterSize = initialMetadata.ClusterSize;
    var pageSize = initialMetadata.PageSize;
    var pageClusters = pageSize / clusterSize;
    var imageEnd = options.ImageEnd > 0 ? Math.Min(options.ImageEnd, this._image.Length) : this._image.Length;
    var origin = Math.Max(0, options.Origin);
    if (imageEnd < pageSize || origin >= imageEnd) return 0;

    var minHead = checked((ulong)((origin + clusterSize - 1L) / clusterSize));
    var lastByteStart = imageEnd - pageSize;
    var maxHead = checked((ulong)(lastByteStart / clusterSize));
    if (minHead > maxHead) return 0;
    var middle = minHead + ((maxHead - minHead) >> 1);

    ulong Score(ulong head) => options.MetadataZonePlacement switch {
      MetadataZone.Front or MetadataZone.BeforeContent => head >= minHead ? head - minHead : ulong.MaxValue,
      MetadataZone.Back => head <= maxHead ? maxHead - head : ulong.MaxValue,
      MetadataZone.Middle => head >= middle ? head - middle : middle - head,
      _ => ulong.MaxValue,
    };

    bool InPlacementWindow(ulong physicalLcn)
      => physicalLcn >= minHead && physicalLcn <= maxHead + checked((ulong)pageClusters - 1);

    // Snapshot the original identities. A move changes a page's physical-name
    // identity, so rebuilding this list after every move would allow the same
    // page to chase successively better holes forever. Each live structure gets
    // at most one relocation in this pass; a later defrag may refine it again.
    var initialMover = new RefsMetadataMover(this._image);
    var names = initialMover.RelocatableMetadata
      .Select(name => (Name: name, Head: ParseHead(name)))
      .Where(x => x.Head.HasValue)
      .Select(x => (x.Name, Head: x.Head!.Value))
      .OrderBy(x => Score(x.Head))
      .ToArray();

    var moved = 0;
    foreach (var original in names) {
      var metadata = RefsMetadataReader.Open(this._image);
      var graph = new RefsMetadataGraph(this._image, metadata);
      var mover = new RefsMetadataMover(this._image);
      if (!mover.RelocatableMetadata.Contains(original.Name)) continue;
      if (!RefsMetadataMover.TryGetTier(graph, original.Name, out var tier, out var sourceHead)) continue;

      var allocator = new RefsAllocatorWriter(metadata, graph, tier);
      if (!allocator.TryFindBestFreeRun(pageClusters, InPlacementWindow, Score, out var targetHead)) continue;
      if (Score(targetHead) >= Score(sourceHead)) continue;

      var sourceOffset = checked((long)sourceHead * clusterSize);
      var targetOffset = checked((long)targetHead * clusterSize);
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "writing",
        imageEnd == 0 ? 0 : Math.Clamp((double)targetOffset / imageEnd, 0, 1),
        sourceOffset,
        targetOffset,
        this._image.Length,
        null,
        $"Placing {original.Name} via ReFS {tier} Allocator"));

      // Allocation is deliberately claimed before copying. This is essential
      // when the structure being moved is an allocator page whose moved bytes
      // must already contain ownership of their destination.
      mover.PrepareMetadataMove(this._image, original.Name, sourceOffset, targetOffset, pageSize);
      Compression.Core.DiskImage.ExtentCopy.Move(this._image, sourceOffset, targetOffset, pageSize);
      mover.UpdateMetadataAfterMove(this._image, original.Name, sourceOffset, targetOffset, pageSize);
      this._image.Flush();
      ++moved;
    }

    return moved;
  }

  private static ulong? ParseHead(string name) {
    if (RefsMetadataNames.TryParsePage(name, out var page)) return page;
    if (RefsMetadataNames.TryParseCheckpoint(name, out var checkpoint)) return checkpoint;
    return null;
  }
}
