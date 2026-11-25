namespace FileSystem.Refs;

/// <summary>
/// A new metadata page for an in-place edit: one page of clusters, on a page boundary, free in
/// the Medium Allocator row that holds an existing page of the same table — where every
/// directory page Windows wrote lies — with its virtual addresses for the page's self
/// reference and its parent's page reference. Nothing is written until <see cref="Claim"/>.
/// </summary>
internal sealed record RefsMetadataPageAllocation(RefsAllocatorWriter Allocator, IReadOnlyList<ulong> Physical, IReadOnlyList<ulong> Virtual) {

  /// <summary>Plans a free page next to <paramref name="nearPage"/> (its physical clusters); refuses when there is none.</summary>
  public static RefsMetadataPageAllocation Plan(RefsMetadataReader metadata, RefsMetadataGraph graph, IReadOnlyList<ulong> nearPage) {
    var pageClusters = Math.Max(1, metadata.PageSize / metadata.ClusterSize);
    var allocator = RefsAllocatorWriter.WindowsMedium(metadata, graph, nearPage)
      ?? throw new NotSupportedException("ReFS: the volume's Medium Allocator is not one this editor writes.");
    var (rowStart, rowLength) = allocator.RangeOfPhysical(nearPage[0])
      ?? throw new NotSupportedException("ReFS: the allocator row holding the table's pages was not found.");
    for (var candidate = (rowStart + (ulong)pageClusters - 1) / (ulong)pageClusters * (ulong)pageClusters;
         candidate + (ulong)pageClusters <= rowStart + rowLength; candidate += (ulong)pageClusters) {
      var run = Enumerable.Range(0, pageClusters).Select(i => candidate + (ulong)i).ToList();
      if (!run.All(allocator.CoversPhysical) || !allocator.AreAllocated(run, expectedAllocated: false)) continue;
      var virtualLcns = new List<ulong>();
      foreach (var p in run) {
        if (!metadata.TryPhysicalToVirtualLcn(p, out var v)) throw new NotSupportedException("ReFS: the new page has no virtual address.");
        virtualLcns.Add(v);
      }
      return new RefsMetadataPageAllocation(allocator, run, virtualLcns);
    }
    throw new NotSupportedException("ReFS: no free run for a new metadata page.");
  }

  /// <summary>Marks the page allocated and writes <paramref name="page"/> to it.</summary>
  public void Claim(Stream image, RefsMetadataReader metadata, byte[] page) {
    this.Allocator.SetAllocated(this.Physical, allocated: true);
    for (var i = 0; i < this.Physical.Count; ++i) {
      image.Position = (long)this.Physical[i] * metadata.ClusterSize;
      image.Write(page, i * metadata.ClusterSize, Math.Min(metadata.ClusterSize, page.Length - i * metadata.ClusterSize));
    }
    image.Flush();
  }

  /// <summary><paramref name="page"/> with its self reference (0x20..0x40) set to this page's virtual addresses.</summary>
  public byte[] Stamp(byte[] page) {
    var result = page.ToArray();
    for (var i = 0; i < 4; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x20 + i * 8), i < this.Virtual.Count ? this.Virtual[i] : 0);
    return result;
  }
}
