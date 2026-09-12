#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal sealed record RefsTreeRow(byte[] Key, byte[] Value, ushort Flags = 0);

internal sealed record RefsCowTreeResult(
  byte[] RootReference,
  ulong RootPhysicalLcn,
  uint SchemaId,
  int Height,
  int LeafPageCount,
  long RowCount,
  IReadOnlyList<ulong> NewPageHeads);

internal enum RefsSeparatorConvention {
  FirstKey,
  LastKey,
}

internal sealed record RefsCowReservedPage(
  RefsAllocatorTier Tier,
  ulong[] PhysicalSlots) {
  public ulong PhysicalHead => this.PhysicalSlots.Length == 0 ? 0 : this.PhysicalSlots[0];
}

internal sealed class RefsCowPoolExhaustedException : IOException {
  public RefsCowPoolExhaustedException(RefsAllocatorTier tier)
    : base($"ReFS {tier} fixed CoW page pool was exhausted before the replacement tree was complete.")
    => this.Tier = tier;

  public RefsAllocatorTier Tier { get; }
}

/// <summary>
/// Reserves native CoW targets without touching the live allocator tree. The
/// corresponding allocation-state changes are deliberately deferred until the
/// transaction has built the replacement allocator root. Allocator-tree CoW
/// uses a fixed pre-reserved pool so its own replacement pages can be marked
/// allocated in the very allocator image that will publish them.
/// </summary>
internal sealed class RefsCowPageStore {
  private readonly Stream _image;
  private readonly RefsMetadataReader _metadata;
  private readonly RefsMetadataGraph _graph;
  private readonly IReadOnlyList<(ulong Start, ulong Count)> _free;
  private readonly HashSet<ulong> _reserved = [];
  private readonly Dictionary<ulong, RefsAllocatorTier> _reservationTiers = [];
  private readonly List<RefsCowReservedPage> _reservedPages = [];
  private readonly Dictionary<RefsAllocatorTier, RefsAllocatorWriter> _writers = [];
  private readonly Dictionary<RefsAllocatorTier, FixedPoolState> _fixedPools = [];

  private sealed class FixedPoolState {
    public FixedPoolState(IReadOnlyList<RefsCowReservedPage> pages) => this.Pages = pages;
    public IReadOnlyList<RefsCowReservedPage> Pages { get; }
    public int Cursor { get; set; }
  }

  public RefsCowPageStore(Stream image, RefsMetadataReader metadata) {
    this._image = image;
    this._metadata = metadata;
    this._graph = new RefsMetadataGraph(image, metadata);
    this._free = RefsAllocatorMap.Read(metadata).Free;
  }

  public IReadOnlySet<ulong> ReservedClusters => this._reserved;

  public IReadOnlyList<ulong> GetReservedClusters(RefsAllocatorTier tier)
    => this._reservationTiers
      .Where(item => item.Value == tier)
      .Select(item => item.Key)
      .OrderBy(lcn => lcn)
      .ToArray();

  public IReadOnlyList<RefsCowReservedPage> GetReservedPages(RefsAllocatorTier tier)
    => this._reservedPages.Where(page => page.Tier == tier).ToArray();

  public IReadOnlyList<RefsCowReservedPage> ReservePages(RefsAllocatorTier tier, int count) {
    if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
    var result = new List<RefsCowReservedPage>(count);
    for (var i = 0; i < count; ++i) result.Add(this.ReserveFreshPage(tier));
    return result;
  }

  public void ReleasePages(IEnumerable<RefsCowReservedPage> pages) {
    ArgumentNullException.ThrowIfNull(pages);
    var release = pages.ToArray();
    foreach (var page in release) {
      if (this._fixedPools.TryGetValue(page.Tier, out var pool)
          && pool.Pages.Any(p => ReferenceEquals(p, page) || p.PhysicalHead == page.PhysicalHead))
        throw new InvalidOperationException("Cannot release a ReFS CoW page while it belongs to an active fixed pool.");
      foreach (var lcn in page.PhysicalSlots) {
        if (!this._reservationTiers.TryGetValue(lcn, out var owner) || owner != page.Tier)
          throw new InvalidOperationException($"ReFS CoW page PLCN 0x{lcn:X} is not reserved by {page.Tier}.");
      }
    }

    foreach (var page in release) {
      foreach (var lcn in page.PhysicalSlots) {
        this._reservationTiers.Remove(lcn);
        this._reserved.Remove(lcn);
      }
      this._reservedPages.RemoveAll(p => p.Tier == page.Tier && p.PhysicalHead == page.PhysicalHead);
    }
  }

  public void BeginFixedPool(RefsAllocatorTier tier, IReadOnlyList<RefsCowReservedPage> pages) {
    ArgumentNullException.ThrowIfNull(pages);
    if (this._fixedPools.ContainsKey(tier))
      throw new InvalidOperationException($"A ReFS {tier} fixed CoW pool is already active.");
    foreach (var page in pages) {
      if (page.Tier != tier)
        throw new ArgumentException("A ReFS fixed CoW pool cannot mix allocator tiers.", nameof(pages));
      if (page.PhysicalSlots.Length != this._metadata.PageSize / this._metadata.ClusterSize)
        throw new ArgumentException("A ReFS fixed CoW pool page has the wrong geometry.", nameof(pages));
      foreach (var lcn in page.PhysicalSlots)
        if (!this._reservationTiers.TryGetValue(lcn, out var owner) || owner != tier)
          throw new InvalidOperationException($"ReFS fixed-pool PLCN 0x{lcn:X} is not reserved by {tier}.");
    }
    this._fixedPools[tier] = new FixedPoolState(pages.ToArray());
  }

  public int GetFixedPoolConsumed(RefsAllocatorTier tier)
    => this._fixedPools.TryGetValue(tier, out var pool) ? pool.Cursor : 0;

  public void EndFixedPool(RefsAllocatorTier tier) {
    if (!this._fixedPools.Remove(tier))
      throw new InvalidOperationException($"No ReFS {tier} fixed CoW pool is active.");
  }

  public IReadOnlyList<ulong> ReservePage(RefsAllocatorTier tier) {
    if (this._fixedPools.TryGetValue(tier, out var pool)) {
      if (pool.Cursor >= pool.Pages.Count) throw new RefsCowPoolExhaustedException(tier);
      return pool.Pages[pool.Cursor++].PhysicalSlots;
    }
    return this.ReserveFreshPage(tier).PhysicalSlots;
  }

  public void WriteUnpublishedPage(IReadOnlyList<ulong> physicalSlots, ReadOnlySpan<byte> page) {
    if (page.Length != this._metadata.PageSize)
      throw new ArgumentException("ReFS CoW page writes require exactly one metadata page.", nameof(page));
    if (physicalSlots.Count != this._metadata.PageSize / this._metadata.ClusterSize)
      throw new ArgumentException("ReFS CoW page slot count does not match page geometry.", nameof(physicalSlots));
    if (physicalSlots.Any(p => !this._reserved.Contains(p)))
      throw new InvalidOperationException("ReFS CoW writer may only write transaction-reserved clusters.");

    var cursor = 0;
    foreach (var lcn in physicalSlots) {
      var offset = checked((long)lcn * this._metadata.ClusterSize);
      if (offset < 0 || offset > this._image.Length - this._metadata.ClusterSize)
        throw new InvalidDataException("ReFS CoW target lies outside the image.");
      var take = Math.Min(this._metadata.ClusterSize, page.Length - cursor);
      this._image.Position = offset;
      this._image.Write(page.Slice(cursor, take));
      cursor += take;
    }
    if (cursor != page.Length) throw new IOException("ReFS CoW metadata page write was incomplete.");
  }

  private RefsCowReservedPage ReserveFreshPage(RefsAllocatorTier tier) {
    var clusters = this._metadata.PageSize / this._metadata.ClusterSize;
    var writer = this.Writer(tier);
    foreach (var (start, count) in this._free) {
      if (count < (ulong)clusters) continue;
      var end = checked(start + count);
      for (var head = start; head <= end - (ulong)clusters; ++head) {
        var slots = Consecutive(head, clusters);
        if (slots.Any(this._reserved.Contains)) continue;
        if (!slots.All(writer.CoversPhysical)) continue;
        if (!writer.AreAllocated(slots, expectedAllocated: false)) continue;
        foreach (var slot in slots) {
          this._reserved.Add(slot);
          this._reservationTiers[slot] = tier;
        }
        var page = new RefsCowReservedPage(tier, slots);
        this._reservedPages.Add(page);
        return page;
      }
    }
    throw new IOException($"ReFS {tier} allocator has no contiguous free metadata page for native CoW.");
  }

  private RefsAllocatorWriter Writer(RefsAllocatorTier tier) {
    if (!this._writers.TryGetValue(tier, out var writer))
      this._writers[tier] = writer = new RefsAllocatorWriter(this._metadata, this._graph, tier);
    return writer;
  }

  private static ulong[] Consecutive(ulong head, int count) {
    var result = new ulong[count];
    for (var i = 0; i < count; ++i) result[i] = checked(head + (ulong)i);
    return result;
  }
}

/// <summary>
/// Functional ReFS B+ mutation engine. A logical edit is materialized as a new
/// immutable tree: leaf pages are packed, split or merged as necessary; inner
/// levels are rebuilt recursively; root height grows or shrinks naturally. No
/// page reachable from the active checkpoint is overwritten.
/// </summary>
internal sealed class RefsCowBTree {
  private readonly Stream _image;
  private readonly RefsMetadataReader _metadata;
  private readonly RefsSchemaCatalog _schemas;
  private readonly RefsCowPageStore _store;
  private readonly byte[] _referenceTemplate;
  private readonly HashSet<ulong> _knownRootHeads;

  private sealed record PageLayout(
    byte[] Template,
    ulong PhysicalHead,
    bool IsRoot,
    bool IsInner,
    int NodeOffset,
    int DataStart,
    int IndexStart,
    int IndexEnd,
    IReadOnlyList<RefsTreeRow> Rows);

  private sealed record BuiltChild(
    ulong PhysicalHead,
    byte[] Reference,
    byte[] FirstKey,
    byte[] LastKey,
    int Height,
    int LeafPages,
    long LeafRows);

  public RefsCowBTree(Stream image, RefsMetadataReader metadata, RefsCowPageStore store) {
    this._image = image;
    this._metadata = metadata;
    this._store = store;
    this._schemas = new RefsSchemaCatalog(metadata);
    this._referenceTemplate = this.ReadReferenceTemplate();
    this._knownRootHeads = this.FindKnownRootHeads();
  }

  public RefsCowTreeResult Rewrite(
      RefsPageReference root,
      bool virtualAddresses,
      Func<List<RefsTreeRow>, RefsKeyComparer, bool> edit,
      bool caseSensitiveDirectory = false) {
    ArgumentNullException.ThrowIfNull(edit);
    var rootSlots = this.Resolve(root, virtualAddresses);
    if (rootSlots.Count == 0) throw new InvalidDataException("ReFS tree has no root page.");
    var rootPage = this.ReadPage(rootSlots);
    var rootLayout = ParsePage(rootPage, rootSlots[0], isRoot: true);
    var schemaId = FindRootSchema(rootPage, rootLayout.NodeOffset)
      ?? throw new InvalidDataException("ReFS B+ root does not expose a structurally valid IndexRoot schema descriptor.");
    this._schemas.Get(schemaId);

    RefsUpcaseTable? upcase = null;
    if (schemaId is 0x130 or 0x140 && !caseSensitiveDirectory)
      upcase = RefsUpcaseTable.Load(this._metadata);
    var comparer = new RefsKeyComparer(schemaId, this._schemas, upcase, caseSensitiveDirectory);

    var rows = new List<RefsTreeRow>();
    RefsSeparatorConvention? observedConvention = null;
    this.CollectLeaves(root, virtualAddresses, comparer, rows, ref observedConvention, new HashSet<ulong>());
    rows.Sort((a, b) => comparer.Compare(a.Key, b.Key));
    EnsureStrictOrder(rows, comparer);

    if (!edit(rows, comparer))
      throw new InvalidOperationException("ReFS B+ edit reported no mutation.");
    rows.Sort((a, b) => comparer.Compare(a.Key, b.Key));
    EnsureStrictOrder(rows, comparer);

    var convention = observedConvention;
    var leafTemplate = this.FindNonRootTemplate(isInner: false) ?? rootLayout;
    var innerTemplate = this.FindNonRootTemplate(isInner: true);
    var rootTier = TierFor(rootPage);
    var newHeads = new List<ulong>();

    if (CanPack(rootLayout, rows)) {
      var rootPhysical = this.WritePage(rootLayout, rows, isInner: false, rootTier, virtualAddresses,
        schemaId, leafPageCount: 0, tableRowCount: rows.Count, newHeads);
      var written = this.ReadPhysicalPage(rootPhysical);
      return new RefsCowTreeResult(
        this.BuildReference(rootPhysical, written, virtualAddresses),
        rootPhysical,
        schemaId,
        Height: 0,
        LeafPageCount: 0,
        RowCount: rows.Count,
        NewPageHeads: newHeads);
    }

    convention ??= this.InferGlobalSeparatorConvention();
    var leafGroups = PartitionRows(rows, leafTemplate);
    var level = new List<BuiltChild>(leafGroups.Count);
    foreach (var group in leafGroups) {
      var head = this.WritePage(leafTemplate, group, isInner: false, rootTier, virtualAddresses,
        schemaId, leafPageCount: 0, tableRowCount: 0, newHeads);
      var bytes = this.ReadPhysicalPage(head);
      level.Add(new BuiltChild(
        head,
        this.BuildReference(head, bytes, virtualAddresses),
        group[0].Key,
        group[^1].Key,
        Height: 0,
        LeafPages: 1,
        LeafRows: group.Count));
    }

    var height = 1;
    while (true) {
      var rootRows = MakeInnerRows(level, convention.Value);
      if (CanPack(rootLayout, rootRows)) {
        var rootPhysical = this.WritePage(rootLayout, rootRows, isInner: true, rootTier, virtualAddresses,
          schemaId, leafGroups.Count, rows.Count, newHeads);
        var rootBytes = this.ReadPhysicalPage(rootPhysical);
        return new RefsCowTreeResult(
          this.BuildReference(rootPhysical, rootBytes, virtualAddresses),
          rootPhysical,
          schemaId,
          height,
          leafGroups.Count,
          rows.Count,
          newHeads);
      }

      innerTemplate ??= this.FindNonRootTemplate(isInner: true)
        ?? throw new NotSupportedException(
          "ReFS B+ root growth requires a non-root inner-node layout template; none exists on this volume to derive safely.");
      var innerGroups = PartitionRows(rootRows, innerTemplate);
      var next = new List<BuiltChild>(innerGroups.Count);
      var childCursor = 0;
      foreach (var group in innerGroups) {
        var childCount = group.Count;
        var childSlice = level.GetRange(childCursor, childCount);
        childCursor += childCount;
        var head = this.WritePage(innerTemplate, group, isInner: true, rootTier, virtualAddresses,
          schemaId, 0, 0, newHeads);
        var bytes = this.ReadPhysicalPage(head);
        next.Add(new BuiltChild(
          head,
          this.BuildReference(head, bytes, virtualAddresses),
          childSlice[0].FirstKey,
          childSlice[^1].LastKey,
          Height: height,
          LeafPages: childSlice.Sum(c => c.LeafPages),
          LeafRows: childSlice.Sum(c => c.LeafRows)));
      }
      level = next;
      ++height;
    }
  }

  public RefsCowTreeResult Insert(
      RefsPageReference root,
      bool virtualAddresses,
      RefsTreeRow row,
      bool caseSensitiveDirectory = false)
    => this.Rewrite(root, virtualAddresses, (rows, comparer) => {
      var index = rows.BinarySearch(row, Comparer<RefsTreeRow>.Create((a, b) => comparer.Compare(a.Key, b.Key)));
      if (index >= 0) throw new IOException("ReFS B+ insert key already exists.");
      rows.Insert(~index, row);
      return true;
    }, caseSensitiveDirectory);

  public RefsCowTreeResult Delete(
      RefsPageReference root,
      bool virtualAddresses,
      ReadOnlySpan<byte> key,
      bool caseSensitiveDirectory = false) {
    var keyBytes = key.ToArray();
    return this.Rewrite(root, virtualAddresses, (rows, comparer) => {
      var index = FindKey(rows, keyBytes, comparer);
      if (index < 0) throw new FileNotFoundException("ReFS B+ delete key does not exist.");
      rows.RemoveAt(index);
      return true;
    }, caseSensitiveDirectory);
  }

  public RefsCowTreeResult Upsert(
      RefsPageReference root,
      bool virtualAddresses,
      RefsTreeRow row,
      bool caseSensitiveDirectory = false)
    => this.Rewrite(root, virtualAddresses, (rows, comparer) => {
      var index = FindKey(rows, row.Key, comparer);
      if (index >= 0) rows[index] = row;
      else rows.Insert(~index, row);
      return true;
    }, caseSensitiveDirectory);

  private void CollectLeaves(
      RefsPageReference reference,
      bool virtualAddresses,
      RefsKeyComparer comparer,
      List<RefsTreeRow> output,
      ref RefsSeparatorConvention? convention,
      HashSet<ulong> visited) {
    var slots = this.Resolve(reference, virtualAddresses);
    if (slots.Count == 0 || !visited.Add(slots[0])) return;
    var page = this.ReadPage(slots);
    var layout = ParsePage(page, slots[0], this._knownRootHeads.Contains(slots[0]));
    if (!layout.IsInner) {
      output.AddRange(layout.Rows);
      return;
    }

    foreach (var row in layout.Rows) {
      var childReference = RefsPageReference.Parse(row.Value);
      if (childReference.Lcns.Count == 0)
        throw new InvalidDataException("ReFS inner B+ row does not contain a child page reference.");
      var childRows = new List<RefsTreeRow>();
      var childConvention = convention;
      this.CollectLeaves(childReference, virtualAddresses, comparer, childRows, ref childConvention, visited);
      if (childRows.Count == 0)
        throw new InvalidDataException("ReFS inner B+ row references an empty child subtree.");
      childRows.Sort((a, b) => comparer.Compare(a.Key, b.Key));
      var matchesFirst = comparer.Compare(row.Key, childRows[0].Key) == 0;
      var matchesLast = comparer.Compare(row.Key, childRows[^1].Key) == 0;
      var local = matchesFirst && !matchesLast
        ? RefsSeparatorConvention.FirstKey
        : matchesLast && !matchesFirst
          ? RefsSeparatorConvention.LastKey
          : matchesFirst && matchesLast
            ? childConvention
            : null;
      if (local.HasValue) {
        if (convention.HasValue && convention.Value != local.Value)
          throw new InvalidDataException("ReFS B+ tree mixes incompatible separator-key conventions.");
        convention = local;
      }
      output.AddRange(childRows);
    }
  }

  private RefsSeparatorConvention InferGlobalSeparatorConvention() {
    foreach (var oid in new[] { 0x07UL, 0x08UL }) {
      if (!TryGetObjectRoot(this._metadata, oid, out var upcaseRoot)) continue;
      try {
        var comparer = new RefsKeyComparer(0xE090, this._schemas);
        var rows = new List<RefsTreeRow>();
        RefsSeparatorConvention? convention = null;
        this.CollectLeaves(upcaseRoot, true, comparer, rows, ref convention, new HashSet<ulong>());
        if (convention.HasValue) return convention.Value;
      } catch (Exception e) when (e is InvalidDataException or NotSupportedException) { }
    }
    throw new NotSupportedException(
      "ReFS B+ split requires a proven separator-key convention; no existing multi-level table exposes one on this volume.");
  }

  private ulong WritePage(
      PageLayout template,
      IReadOnlyList<RefsTreeRow> rows,
      bool isInner,
      RefsAllocatorTier tier,
      bool virtualAddresses,
      uint schemaId,
      long leafPageCount,
      long tableRowCount,
      List<ulong> newHeads) {
    var slots = this._store.ReservePage(tier);
    var page = Pack(template, rows, isInner, schemaId, leafPageCount, tableRowCount);
    StampSelfAddress(page, slots, virtualAddresses, this._metadata);
    this._store.WriteUnpublishedPage(slots, page);
    newHeads.Add(slots[0]);
    return slots[0];
  }

  private static byte[] Pack(
      PageLayout layout,
      IReadOnlyList<RefsTreeRow> rows,
      bool isInner,
      uint schemaId,
      long leafPageCount,
      long tableRowCount) {
    var serialized = rows.Select(SerializeRow).ToArray();
    var rowBytes = serialized.Sum(r => r.Length);
    var indexBytes = checked(rows.Count * 4);
    if (rowBytes + indexBytes > layout.IndexEnd - layout.DataStart)
      throw new InvalidOperationException("ReFS B+ page packing exceeded its node capacity.");

    var result = layout.Template.ToArray();
    result.AsSpan(layout.DataStart, layout.IndexEnd - layout.DataStart).Clear();
    var cursor = layout.DataStart;
    var indexStart = layout.IndexEnd - indexBytes;
    for (var i = 0; i < serialized.Length; ++i) {
      serialized[i].CopyTo(result, cursor);
      var relative = cursor - layout.NodeOffset;
      if ((uint)relative > ushort.MaxValue)
        throw new InvalidOperationException("ReFS B+ row offset exceeds the 16-bit index encoding.");
      BinaryPrimitives.WriteUInt32LittleEndian(
        result.AsSpan(indexStart + i * 4, 4),
        0xFFFF0000U | (uint)relative);
      cursor += serialized[i].Length;
    }

    var flags = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(layout.NodeOffset + 0x0C, 4));
    flags = isInner ? flags | 0x100U : flags & ~0x100U;
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(layout.NodeOffset + 0x0C, 4), flags);
    BinaryPrimitives.WriteUInt32LittleEndian(
      result.AsSpan(layout.NodeOffset + 0x10, 4), checked((uint)(indexStart - layout.NodeOffset)));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(layout.NodeOffset + 0x14, 4), checked((uint)rows.Count));

    if (layout.IsRoot && TryFindRootDescriptor(result, layout.NodeOffset, schemaId, out var descriptor)) {
      BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(descriptor + 0x18, 8), checked((ulong)leafPageCount));
      BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(descriptor + 0x20, 8), checked((ulong)tableRowCount));
    }
    return result;
  }

  private byte[] BuildReference(ulong physicalHead, ReadOnlySpan<byte> page, bool virtualAddresses) {
    var reference = this._referenceTemplate.ToArray();
    var count = this._metadata.PageSize / this._metadata.ClusterSize;
    for (var i = 0; i < 4; ++i) {
      ulong lcn = 0;
      if (i < count) {
        var physical = checked(physicalHead + (ulong)i);
        if (virtualAddresses) {
          if (!this._metadata.TryPhysicalToVirtualLcn(physical, out lcn))
            throw new InvalidDataException($"ReFS CoW PLCN 0x{physical:X} has no VLCN mapping.");
        } else {
          lcn = physical;
        }
      }
      BinaryPrimitives.WriteUInt64LittleEndian(reference.AsSpan(i * 8, 8), lcn);
    }
    RefsChecksum.RefreshPageReference(reference, page);
    return reference;
  }

  private PageLayout? FindNonRootTemplate(bool isInner) {
    var graph = new RefsMetadataGraph(this._image, this._metadata);
    foreach (var info in graph.Pages) {
      if (this._knownRootHeads.Contains(info.PhysicalHead)) continue;
      try {
        var page = graph.ReadPage(info.PhysicalHead);
        var layout = ParsePage(page, info.PhysicalHead, isRoot: false);
        if (layout.IsInner == isInner) return layout;
      } catch (InvalidDataException) { }
    }
    return null;
  }

  private HashSet<ulong> FindKnownRootHeads() {
    var result = new HashSet<ulong>();
    for (var i = 0; i < this._metadata.Roots.Count; ++i) {
      var reference = this._metadata.Roots[i];
      if (reference.Lcns.Count == 0) continue;
      try {
        var physical = i is 7 or 8 or 12
          ? reference.Lcns[0]
          : this._metadata.TranslateVirtualLcn(reference.Lcns[0]);
        result.Add(physical);
      } catch (InvalidDataException) { }
    }
    try {
      foreach (var row in this._metadata.WalkRoot(0)) {
        if (row.Value.Length < 0x20 + this._metadata.PageReferenceSize) continue;
        var reference = RefsPageReference.Parse(row.Value.AsSpan(0x20));
        if (reference.Lcns.Count == 0) continue;
        try { result.Add(this._metadata.TranslateVirtualLcn(reference.Lcns[0])); }
        catch (InvalidDataException) { }
      }
    } catch (InvalidDataException) { }
    return result;
  }

  private byte[] ReadReferenceTemplate() {
    var checkpoint = this.ReadPhysicalPage(this._metadata.ActiveCheckpointLcn);
    var flags = ReadU32(checkpoint, 0x78);
    var count = checked((int)ReadU32(checkpoint, 0x90));
    var offsetsBase = (flags & 0x0200) != 0 ? checked((int)ReadU32(checkpoint, 0x94)) : 0x94;
    for (var i = 0; i < count; ++i) {
      var offset = checked((int)ReadU32(checkpoint, offsetsBase + i * 4));
      if (offset <= 0 || offset + this._metadata.PageReferenceSize > checkpoint.Length) continue;
      return checkpoint.AsSpan(offset, checked((int)this._metadata.PageReferenceSize)).ToArray();
    }
    throw new InvalidDataException("ReFS checkpoint exposes no page-reference template.");
  }

  private IReadOnlyList<ulong> Resolve(RefsPageReference reference, bool virtualAddresses) {
    var result = new ulong[reference.Lcns.Count];
    for (var i = 0; i < result.Length; ++i)
      result[i] = virtualAddresses ? this._metadata.TranslateVirtualLcn(reference.Lcns[i]) : reference.Lcns[i];
    return result;
  }

  private byte[] ReadPage(IReadOnlyList<ulong> slots) {
    var result = new byte[this._metadata.PageSize];
    var cursor = 0;
    foreach (var lcn in slots) {
      var offset = checked((long)lcn * this._metadata.ClusterSize);
      if (offset < 0 || offset > this._image.Length - this._metadata.ClusterSize)
        throw new InvalidDataException("ReFS B+ page lies outside the image.");
      var take = Math.Min(this._metadata.ClusterSize, result.Length - cursor);
      this._image.Position = offset;
      this._image.ReadExactly(result.AsSpan(cursor, take));
      cursor += take;
    }
    if (cursor != result.Length) throw new InvalidDataException("ReFS B+ page is incomplete.");
    return result;
  }

  private byte[] ReadPhysicalPage(ulong head) {
    var count = this._metadata.PageSize / this._metadata.ClusterSize;
    var slots = new ulong[count];
    for (var i = 0; i < count; ++i) slots[i] = checked(head + (ulong)i);
    return this.ReadPage(slots);
  }

  private static PageLayout ParsePage(byte[] page, ulong physicalHead, bool isRoot) {
    if (page.Length < 0x80 || !page.AsSpan(0, 4).SequenceEqual("MSB+"u8))
      throw new InvalidDataException("ReFS B+ page does not contain MSB+.");
    var nodeOffset = checked(0x50 + (int)ReadU32(page, 0x50));
    if (nodeOffset < 0x50 || nodeOffset + 0x28 > page.Length)
      throw new InvalidDataException("ReFS B+ node header lies outside its page.");
    var dataStart = checked(nodeOffset + (int)ReadU32(page, nodeOffset + 0x00));
    var indexStart = checked(nodeOffset + (int)ReadU32(page, nodeOffset + 0x10));
    var indexEnd = checked(nodeOffset + (int)ReadU32(page, nodeOffset + 0x20));
    if (dataStart < nodeOffset || indexStart < dataStart || indexEnd < indexStart || indexEnd > page.Length
        || ((indexEnd - indexStart) & 3) != 0)
      throw new InvalidDataException("ReFS B+ node bounds are malformed.");

    var rows = new List<RefsTreeRow>();
    for (var p = indexStart; p < indexEnd; p += 4) {
      var encoded = ReadU32(page, p);
      var rowOffset = nodeOffset + (int)(encoded & 0xFFFF);
      if (rowOffset < dataStart || rowOffset + 16 > indexStart)
        throw new InvalidDataException("ReFS B+ row index points outside the data area.");
      var rowSize = checked((int)ReadU32(page, rowOffset));
      var keyOffset = ReadU16(page, rowOffset + 4);
      var keyLength = ReadU16(page, rowOffset + 6);
      var flags = ReadU16(page, rowOffset + 8);
      var valueOffset = ReadU16(page, rowOffset + 10);
      var valueLength = ReadU16(page, rowOffset + 12);
      if (rowSize < 16 || rowOffset + rowSize > indexStart
          || keyOffset + keyLength > rowSize || valueOffset + valueLength > rowSize)
        throw new InvalidDataException("ReFS B+ row is malformed.");
      rows.Add(new RefsTreeRow(
        page.AsSpan(rowOffset + keyOffset, keyLength).ToArray(),
        page.AsSpan(rowOffset + valueOffset, valueLength).ToArray(),
        flags));
    }

    var inner = (ReadU32(page, nodeOffset + 0x0C) & 0x100) != 0;
    return new PageLayout(page.ToArray(), physicalHead, isRoot, inner, nodeOffset, dataStart, indexStart, indexEnd, rows);
  }

  private static List<List<RefsTreeRow>> PartitionRows(IReadOnlyList<RefsTreeRow> rows, PageLayout layout) {
    var result = new List<List<RefsTreeRow>>();
    var current = new List<RefsTreeRow>();
    var bytes = 0;
    foreach (var row in rows) {
      var size = SerializeRow(row).Length + 4;
      if (size > layout.IndexEnd - layout.DataStart)
        throw new InvalidOperationException("A single ReFS B+ row exceeds page capacity.");
      if (current.Count > 0 && bytes + size > layout.IndexEnd - layout.DataStart) {
        result.Add(current);
        current = [];
        bytes = 0;
      }
      current.Add(row);
      bytes += size;
    }
    if (current.Count > 0 || rows.Count == 0) result.Add(current);
    return result;
  }

  private static bool CanPack(PageLayout layout, IReadOnlyList<RefsTreeRow> rows) {
    var bytes = rows.Sum(r => SerializeRow(r).Length + 4);
    return bytes <= layout.IndexEnd - layout.DataStart;
  }

  private static byte[] SerializeRow(RefsTreeRow row) {
    var length = checked(16 + row.Key.Length + row.Value.Length);
    if (length > ushort.MaxValue) throw new InvalidOperationException("ReFS B+ row exceeds 16-bit length fields.");
    var result = new byte[length];
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), checked((uint)length));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4, 2), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6, 2), checked((ushort)row.Key.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8, 2), row.Flags);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10, 2), checked((ushort)(16 + row.Key.Length)));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12, 2), checked((ushort)row.Value.Length));
    row.Key.CopyTo(result, 16);
    row.Value.CopyTo(result, 16 + row.Key.Length);
    return result;
  }

  private static List<RefsTreeRow> MakeInnerRows(
      IReadOnlyList<BuiltChild> children,
      RefsSeparatorConvention convention) {
    var result = new List<RefsTreeRow>(children.Count);
    foreach (var child in children)
      result.Add(new RefsTreeRow(
        (convention == RefsSeparatorConvention.FirstKey ? child.FirstKey : child.LastKey).ToArray(),
        child.Reference.ToArray(),
        0));
    return result;
  }

  private static void EnsureStrictOrder(IReadOnlyList<RefsTreeRow> rows, RefsKeyComparer comparer) {
    for (var i = 1; i < rows.Count; ++i)
      if (comparer.Compare(rows[i - 1].Key, rows[i].Key) >= 0)
        throw new IOException("ReFS B+ mutation produced duplicate or out-of-order keys.");
  }

  private static int FindKey(IReadOnlyList<RefsTreeRow> rows, ReadOnlySpan<byte> key, RefsKeyComparer comparer) {
    var lo = 0;
    var hi = rows.Count - 1;
    while (lo <= hi) {
      var mid = lo + ((hi - lo) >> 1);
      var cmp = comparer.Compare(rows[mid].Key, key);
      if (cmp == 0) return mid;
      if (cmp < 0) lo = mid + 1;
      else hi = mid - 1;
    }
    return ~lo;
  }

  private static uint? FindRootSchema(byte[] page, int nodeOffset) {
    for (var p = 0x50; p + 0x28 <= nodeOffset; p += 4) {
      if (ReadU32(page, p) != 0x28) continue;
      var schema = ReadU16(page, p + 0x0C);
      if (schema is >= 0x0004 and <= 0xE140) return schema;
    }
    return null;
  }

  private static bool TryFindRootDescriptor(byte[] page, int nodeOffset, uint schemaId, out int offset) {
    for (var p = 0x50; p + 0x28 <= nodeOffset; p += 4) {
      if (ReadU32(page, p) != 0x28 || ReadU16(page, p + 0x0C) != schemaId) continue;
      offset = p;
      return true;
    }
    offset = 0;
    return false;
  }

  private static void StampSelfAddress(
      Span<byte> page,
      IReadOnlyList<ulong> physicalSlots,
      bool virtualAddresses,
      RefsMetadataReader metadata) {
    for (var i = 0; i < 4; ++i) {
      ulong lcn = 0;
      if (i < physicalSlots.Count) {
        if (virtualAddresses) {
          if (!metadata.TryPhysicalToVirtualLcn(physicalSlots[i], out lcn))
            throw new InvalidDataException($"ReFS CoW target PLCN 0x{physicalSlots[i]:X} has no virtual address.");
        } else {
          lcn = physicalSlots[i];
        }
      }
      BinaryPrimitives.WriteUInt64LittleEndian(page.Slice(0x20 + i * 8, 8), lcn);
    }
  }

  private static RefsAllocatorTier TierFor(ReadOnlySpan<byte> rootPage) {
    if (rootPage.Length < 0x50) return RefsAllocatorTier.Medium;
    var tableId = BinaryPrimitives.ReadUInt64LittleEndian(rootPage.Slice(0x48, 8));
    return tableId switch {
      0x0B or 0x0C or 0x20 => RefsAllocatorTier.Container,
      0x22 => RefsAllocatorTier.Small,
      _ => RefsAllocatorTier.Medium,
    };
  }

  private static bool TryGetObjectRoot(RefsMetadataReader metadata, ulong oid, out RefsPageReference root) {
    try {
      foreach (var row in metadata.WalkRoot(0)) {
        if (row.Key.Length < 16 || row.Value.Length < 0x20 + metadata.PageReferenceSize) continue;
        if (BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(8, 8)) != oid) continue;
        var candidate = RefsPageReference.Parse(row.Value.AsSpan(0x20));
        if (candidate.Lcns.Count == 0) continue;
        root = candidate;
        return true;
      }
    } catch (InvalidDataException) { }
    root = RefsPageReference.Empty;
    return false;
  }

  private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset)
    => offset >= 0 && offset + 4 <= bytes.Length
      ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4))
      : throw new InvalidDataException("ReFS B+ field lies outside its page.");

  private static ushort ReadU16(ReadOnlySpan<byte> bytes, int offset)
    => offset >= 0 && offset + 2 <= bytes.Length
      ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2))
      : throw new InvalidDataException("ReFS B+ field lies outside its page.");
}
