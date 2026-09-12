#pragma warning disable CS1591

namespace FileSystem.Refs;

internal sealed record RefsCowAllocatorPublication(
  RefsAllocatorTier Tier,
  int RootIndex,
  RefsCowTreeResult Tree,
  IReadOnlyList<ulong> AccountedPhysicalClusters,
  int ConvergenceAttempts);

/// <summary>
/// Builds a replacement allocator tree that already accounts for every CoW
/// cluster that will become reachable with the same checkpoint, including the
/// replacement allocator tree's own pages. This closes the allocator/CoW
/// self-reference without ever mutating the active allocator in place.
/// </summary>
internal sealed class RefsCowAllocatorPublisher {
  private const int MaxConvergenceAttempts = 16;

  private readonly Stream _image;
  private readonly RefsMetadataReader _metadata;
  private readonly RefsCowPageStore _store;
  private readonly RefsMetadataGraph _graph;

  public RefsCowAllocatorPublisher(Stream image, RefsMetadataReader metadata, RefsCowPageStore store) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(metadata);
    ArgumentNullException.ThrowIfNull(store);
    this._image = image;
    this._metadata = metadata;
    this._store = store;
    this._graph = new RefsMetadataGraph(image, metadata);
  }

  public RefsCowAllocatorPublication Publish(RefsAllocatorTier tier) {
    var rootIndex = RootIndex(tier);
    var root = this._metadata.Roots[rootIndex];
    if (root.Lcns.Count == 0)
      throw new InvalidDataException($"ReFS {tier} allocator root #{rootIndex} is empty.");

    // Reservations that existed before the allocator starts rebuilding are the
    // actual transaction payload: data/metadata pages built by higher layers.
    // Pool pages are added separately below so unused speculative pool pages can
    // be released without accidentally becoming permanent allocations.
    var transactionReservations = this._store.GetReservedClusters(tier).ToHashSet();
    if (transactionReservations.Count == 0)
      throw new InvalidOperationException(
        $"ReFS {tier} allocator publication was requested without any transaction reservation to publish.");

    var tableId = TableId(tier);
    var livePageCount = this._graph.Pages.Count(page => page.TableId == tableId);
    var pool = this._store.ReservePages(tier, Math.Max(1, livePageCount)).ToList();
    var tree = new RefsCowBTree(this._image, this._metadata, this._store);
    var virtualAddresses = tier != RefsAllocatorTier.Small;

    for (var attempt = 1; attempt <= MaxConvergenceAttempts; ++attempt) {
      var accounted = transactionReservations
        .Concat(pool.SelectMany(page => page.PhysicalSlots))
        .Distinct()
        .OrderBy(lcn => lcn)
        .ToArray();

      RefsCowTreeResult? result = null;
      var exhausted = false;
      var consumed = 0;
      this._store.BeginFixedPool(tier, pool);
      try {
        result = tree.Rewrite(
          root,
          virtualAddresses,
          (rows, _) => this.ApplyAllocatedBits(rows, tier, accounted));
        consumed = this._store.GetFixedPoolConsumed(tier);
      } catch (RefsCowPoolExhaustedException e) when (e.Tier == tier) {
        exhausted = true;
        consumed = this._store.GetFixedPoolConsumed(tier);
      } finally {
        this._store.EndFixedPool(tier);
      }

      if (exhausted) {
        // Compact-free rows may expand to 2 KiB bitmaps and increase the B+
        // page count. Grow the pool, include the new pages in the next allocator
        // image, then rebuild from the still-active root.
        var growBy = Math.Max(1, Math.Max(pool.Count, consumed));
        pool.AddRange(this._store.ReservePages(tier, growBy));
        continue;
      }

      if (result == null || consumed <= 0)
        throw new IOException($"ReFS {tier} allocator CoW rebuild produced no replacement pages.");

      if (consumed < pool.Count) {
        // The initial upper bound can be larger than the packed replacement.
        // Drop unused unpublished tail pages and rebuild on the exact consumed
        // prefix so the final allocator does not leak speculative reservations.
        var unused = pool.Skip(consumed).ToArray();
        this._store.ReleasePages(unused);
        pool = pool.Take(consumed).ToList();
        continue;
      }

      if (consumed != pool.Count)
        throw new IOException("ReFS allocator fixed-pool accounting became inconsistent.");

      var replacementRoot = RefsPageReference.Parse(result.RootReference);
      RefsAllocatorRootVerifier.RequireAllocated(this._metadata, replacementRoot, tier, accounted);
      return new RefsCowAllocatorPublication(tier, rootIndex, result, accounted, attempt);
    }

    throw new IOException(
      $"ReFS {tier} allocator CoW page-pool sizing did not converge after {MaxConvergenceAttempts} attempts.");
  }

  private bool ApplyAllocatedBits(
      List<RefsTreeRow> rows,
      RefsAllocatorTier tier,
      IReadOnlyList<ulong> physicalClusters) {
    var allocatorTargets = new List<ulong>(physicalClusters.Count);
    foreach (var physical in physicalClusters) {
      if (tier == RefsAllocatorTier.Small) {
        allocatorTargets.Add(physical);
        continue;
      }
      if (!this._metadata.TryPhysicalToVirtualLcn(physical, out var virtualLcn))
        throw new InvalidDataException(
          $"ReFS CoW target PLCN 0x{physical:X} has no VLCN mapping for the {tier} allocator.");
      allocatorTargets.Add(virtualLcn);
    }
    allocatorTargets.Sort();

    var ranges = new List<(int RowIndex, ulong Start, ulong Length)>();
    for (var i = 0; i < rows.Count; ++i) {
      if (!RefsAllocatorRowCodec.TryGetRange(rows[i].Value, out var start, out var length)) continue;
      if (!RefsAllocatorRowCodec.IsStructurallyValid(rows[i].Value, length))
        throw new InvalidDataException($"ReFS {tier} allocator row 0x{start:X}+0x{length:X} is inconsistent.");
      ranges.Add((i, start, length));
    }
    ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
    if (ranges.Count == 0)
      throw new InvalidDataException($"ReFS {tier} allocator tree has no writable allocation rows.");

    var edits = new Dictionary<int, List<ulong>>();
    foreach (var target in allocatorTargets.Distinct()) {
      var range = Find(ranges, target)
        ?? throw new InvalidDataException(
          $"ReFS {tier} allocator replacement does not cover allocation LCN 0x{target:X}.");
      if (!edits.TryGetValue(range.RowIndex, out var indices))
        edits[range.RowIndex] = indices = [];
      indices.Add(target - range.Start);
    }

    foreach (var (rowIndex, indices) in edits) {
      var row = rows[rowIndex];
      if (!RefsAllocatorRowCodec.TryGetRange(row.Value, out _, out var length))
        throw new InvalidDataException("ReFS allocator row disappeared during CoW mutation.");
      var value = RefsAllocatorRowCodec.SetAllocated(
        row.Value,
        length,
        indices,
        allocated: true,
        tier,
        this._metadata.Header.MinorVersion);
      rows[rowIndex] = row with { Value = value };
    }
    return edits.Count > 0;
  }

  private static (int RowIndex, ulong Start, ulong Length)? Find(
      IReadOnlyList<(int RowIndex, ulong Start, ulong Length)> ranges,
      ulong lcn) {
    var lo = 0;
    var hi = ranges.Count - 1;
    while (lo <= hi) {
      var mid = lo + ((hi - lo) >> 1);
      var range = ranges[mid];
      if (lcn < range.Start) { hi = mid - 1; continue; }
      if (lcn - range.Start >= range.Length) { lo = mid + 1; continue; }
      return range;
    }
    return null;
  }

  private static int RootIndex(RefsAllocatorTier tier)
    => tier switch {
      RefsAllocatorTier.Medium => 1,
      RefsAllocatorTier.Container => 2,
      RefsAllocatorTier.Small => 12,
      _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

  private static ulong TableId(RefsAllocatorTier tier)
    => tier switch {
      RefsAllocatorTier.Medium => 0x21,
      RefsAllocatorTier.Container => 0x20,
      RefsAllocatorTier.Small => 0x22,
      _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };
}
