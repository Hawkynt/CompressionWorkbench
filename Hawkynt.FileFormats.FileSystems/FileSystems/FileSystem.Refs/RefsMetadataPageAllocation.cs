namespace FileSystem.Refs;

/// <summary>
/// New metadata pages for an in-place edit: each one page of clusters, on a page boundary, free
/// in the Medium Allocator row that holds an existing page of the same table — where every
/// directory page Windows wrote lies — with its virtual addresses for the page's self reference
/// and its parent's page reference. Several pages come from one allocator so they are claimed
/// together; nothing is written until <see cref="Claim"/>.
/// </summary>
internal sealed class RefsMetadataPageAllocation {
  private readonly List<(IReadOnlyList<ulong> Physical, IReadOnlyList<ulong> Virtual)> _pages;

  private RefsMetadataPageAllocation(RefsAllocatorWriter allocator, List<(IReadOnlyList<ulong>, IReadOnlyList<ulong>)> pages) {
    this.Allocator = allocator;
    this._pages = pages;
  }

  public RefsAllocatorWriter Allocator { get; }
  public int Count => this._pages.Count;

  /// <summary>The first page's physical clusters.</summary>
  public IReadOnlyList<ulong> Physical => this._pages[0].Physical;

  /// <summary>The first page's virtual addresses.</summary>
  public IReadOnlyList<ulong> Virtual => this._pages[0].Virtual;

  /// <summary>Every planned page's physical clusters.</summary>
  public IEnumerable<ulong> AllPhysical => this._pages.SelectMany(static p => p.Physical);

  public IReadOnlyList<ulong> PhysicalOf(int page) => this._pages[page].Physical;
  public IReadOnlyList<ulong> VirtualOf(int page) => this._pages[page].Virtual;

  /// <summary>Plans <paramref name="count"/> free pages next to <paramref name="nearPage"/> (its physical clusters); refuses when there are not enough.</summary>
  public static RefsMetadataPageAllocation Plan(RefsMetadataReader metadata, RefsMetadataGraph graph, IReadOnlyList<ulong> nearPage, int count = 1) {
    var pageClusters = Math.Max(1, metadata.PageSize / metadata.ClusterSize);
    var allocator = RefsAllocatorWriter.WindowsMedium(metadata, graph, nearPage)
      ?? throw new NotSupportedException("ReFS: the volume's Medium Allocator is not one this editor writes.");
    var (rowStart, rowLength) = allocator.RangeOfPhysical(nearPage[0])
      ?? throw new NotSupportedException("ReFS: the allocator row holding the table's pages was not found.");
    var pages = new List<(IReadOnlyList<ulong>, IReadOnlyList<ulong>)>();
    for (var candidate = (rowStart + (ulong)pageClusters - 1) / (ulong)pageClusters * (ulong)pageClusters;
         candidate + (ulong)pageClusters <= rowStart + rowLength && pages.Count < count; candidate += (ulong)pageClusters) {
      var run = Enumerable.Range(0, pageClusters).Select(i => candidate + (ulong)i).ToList();
      if (!run.All(allocator.CoversPhysical) || !allocator.AreAllocated(run, expectedAllocated: false)) continue;
      var virtualLcns = new List<ulong>();
      foreach (var p in run) {
        if (!metadata.TryPhysicalToVirtualLcn(p, out var v)) throw new NotSupportedException("ReFS: a new page has no virtual address.");
        virtualLcns.Add(v);
      }
      pages.Add((run, virtualLcns));
    }
    if (pages.Count < count) throw new NotSupportedException("ReFS: no free run for a new metadata page.");
    return new RefsMetadataPageAllocation(allocator, pages);
  }

  /// <summary>Marks every planned page allocated and writes <paramref name="contents"/> (one per page, in order) to them.</summary>
  public void Claim(Stream image, RefsMetadataReader metadata, params byte[][] contents) {
    if (contents.Length != this._pages.Count) throw new ArgumentException("ReFS: one content per planned page.", nameof(contents));
    this.Allocator.SetAllocated(this.AllPhysical.ToList(), allocated: true);
    for (var page = 0; page < contents.Length; ++page) {
      var physical = this._pages[page].Physical;
      for (var i = 0; i < physical.Count; ++i) {
        image.Position = (long)physical[i] * metadata.ClusterSize;
        image.Write(contents[page], i * metadata.ClusterSize, Math.Min(metadata.ClusterSize, contents[page].Length - i * metadata.ClusterSize));
      }
    }
    image.Flush();
  }

  /// <summary><paramref name="page"/> with its self reference (0x20..0x40) set to planned page <paramref name="index"/>'s virtual addresses.</summary>
  public byte[] Stamp(byte[] page, int index = 0) {
    var result = page.ToArray();
    var virtualLcns = this._pages[index].Virtual;
    for (var i = 0; i < 4; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x20 + i * 8), i < virtualLcns.Count ? virtualLcns[i] : 0);
    return result;
  }
}
