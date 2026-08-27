#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal readonly record struct RefsBlockRefcountEntry(
  ushort ReferenceCount,
  bool DedupMetadata,
  bool DedupManaged) {
  public bool IsShared => this.ReferenceCount > 1 || this.DedupManaged || this.DedupMetadata;
}

/// <summary>
/// Read/write view of root #6 (Block Refcount, schema 0xe0b0). The table is
/// sparse: an untracked cluster is owned solely by the allocation trees. A
/// tracked row covers 0x400 VLCNs and stores one u16 reference-count/flag word
/// per cluster plus the sum of the low 14-bit counts at value+0x18.
/// </summary>
internal sealed class RefsBlockRefcount {
  private const int EntriesPerRow = 0x400;
  private const int NormalValueSize = 0x820;
  private const int EntriesOffset = 0x1C;
  private const ushort CountMask = 0x3FFF;
  private const ushort DedupMetadataMask = 0x4000;
  private const ushort DedupManagedMask = 0x8000;

  private readonly RefsMetadataReader _metadata;
  private readonly RefsMetadataGraph? _graph;
  private readonly List<RowState> _rows = [];

  private sealed record RowState(ulong StartVirtualLcn, ulong ClusterCount, RefsBTreeRow Row);

  public RefsBlockRefcount(RefsMetadataReader metadata)
    : this(metadata, null) { }

  public RefsBlockRefcount(RefsMetadataReader metadata, RefsMetadataGraph? graph) {
    this._metadata = metadata;
    this._graph = graph;
    this.ReloadRows();
  }

  public bool HasTrackedRanges => this._rows.Count > 0;

  public bool TryGetPhysical(ulong physicalLcn, out RefsBlockRefcountEntry entry) {
    if (!this._metadata.TryPhysicalToVirtualLcn(physicalLcn, out var virtualLcn)) {
      entry = default;
      return false;
    }
    return this.TryGetVirtual(virtualLcn, out entry);
  }

  public bool TryGetVirtual(ulong virtualLcn, out RefsBlockRefcountEntry entry) {
    var row = this.Find(virtualLcn);
    if (row == null) {
      entry = default;
      return false;
    }

    var index = checked((int)(virtualLcn - row.StartVirtualLcn));
    entry = Decode(ReadRaw(row.Row.Value, index));
    return true;
  }

  public bool IsSharedPhysicalRange(ulong physicalStartLcn, uint clusterCount) {
    for (ulong i = 0; i < clusterCount; ++i)
      if (this.TryGetPhysical(checked(physicalStartLcn + i), out var entry) && entry.IsShared)
        return true;
    return false;
  }

  /// <summary>
  /// Removes one live stream reference from every supplied physical cluster and
  /// returns the clusters whose allocator bit may now be cleared.
  ///
  /// An untracked cluster has no sharing state and can be released immediately.
  /// A normal tracked cluster is releasable when its count reaches zero. Dedup
  /// metadata/store entries remain allocator-owned even at reference count zero:
  /// those flag bits describe an independent dedup-engine ownership class.
  /// </summary>
  public IReadOnlyList<ulong> DetachPhysicalReferences(IEnumerable<ulong> physicalLcns) {
    if (this._graph == null)
      throw new InvalidOperationException("ReFS Block Refcount mutation requires a metadata graph.");

    var safeToFree = new List<ulong>();
    var edits = new Dictionary<RowState, Dictionary<int, ushort>>();

    foreach (var physical in physicalLcns.Distinct()) {
      if (!this._metadata.TryPhysicalToVirtualLcn(physical, out var virtualLcn)) {
        safeToFree.Add(physical);
        continue;
      }

      var row = this.Find(virtualLcn);
      if (row == null) {
        safeToFree.Add(physical);
        continue;
      }

      var index = checked((int)(virtualLcn - row.StartVirtualLcn));
      var raw = ReadRaw(row.Row.Value, index);
      var decoded = Decode(raw);
      if (decoded.ReferenceCount == 0) {
        if (!decoded.DedupManaged && !decoded.DedupMetadata)
          safeToFree.Add(physical);
        continue;
      }

      var newCount = checked((ushort)(decoded.ReferenceCount - 1));
      var updated = (ushort)((raw & ~CountMask) | newCount);
      if (!edits.TryGetValue(row, out var rowEdits)) edits[row] = rowEdits = [];
      rowEdits[index] = updated;

      if (newCount == 0 && !decoded.DedupManaged && !decoded.DedupMetadata)
        safeToFree.Add(physical);
    }

    if (edits.Count == 0) return safeToFree;

    var replacements = new List<(RefsBTreeRow Row, byte[] Value)>();
    foreach (var (state, rowEdits) in edits) {
      var value = state.Row.Value.ToArray();
      foreach (var (index, raw) in rowEdits)
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(EntriesOffset + index * 2, 2), raw);
      RefreshTotal(value);
      if (!RefsPageEditor.CanReplaceValue(this._graph, state.Row, value.Length))
        throw new InvalidOperationException("ReFS Block Refcount update unexpectedly requires a B+ page split.");
      replacements.Add((state.Row, value));
    }

    var changed = new HashSet<ulong>();
    foreach (var (row, value) in replacements)
      changed.Add(RefsPageEditor.ReplaceValue(this._graph, row, value));
    this._graph.RefreshChecksumPaths(changed);
    this.ReloadRows();
    return safeToFree;
  }

  private void ReloadRows() {
    this._rows.Clear();
    foreach (var row in this._metadata.WalkRoot(6)) {
      if (row.Key.Length < 16 || row.Value.Length < NormalValueSize) continue;
      var start = BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(0, 8));
      var count = BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(8, 8));
      if (count != EntriesPerRow) continue;
      if (BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0, 8)) != start) continue;
      if (BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(8, 8)) != count) continue;
      if (!HasValidTotal(row.Value)) continue;
      this._rows.Add(new RowState(start, count, row));
    }
    this._rows.Sort((a, b) => a.StartVirtualLcn.CompareTo(b.StartVirtualLcn));
  }

  private RowState? Find(ulong virtualLcn) {
    var lo = 0;
    var hi = this._rows.Count - 1;
    while (lo <= hi) {
      var mid = lo + ((hi - lo) >> 1);
      var row = this._rows[mid];
      if (virtualLcn < row.StartVirtualLcn) { hi = mid - 1; continue; }
      if (virtualLcn >= row.StartVirtualLcn + row.ClusterCount) { lo = mid + 1; continue; }
      return row;
    }
    return null;
  }

  private static ushort ReadRaw(byte[] value, int index)
    => BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(EntriesOffset + index * 2, 2));

  private static RefsBlockRefcountEntry Decode(ushort raw)
    => new(
      (ushort)(raw & CountMask),
      (raw & DedupMetadataMask) != 0,
      (raw & DedupManagedMask) != 0);

  private static bool HasValidTotal(byte[] value) {
    if (value.Length < NormalValueSize) return false;
    uint sum = 0;
    for (var i = 0; i < EntriesPerRow; ++i)
      sum = checked(sum + (uint)(ReadRaw(value, i) & CountMask));
    return sum == BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(0x18, 4));
  }

  private static void RefreshTotal(byte[] value) {
    uint sum = 0;
    for (var i = 0; i < EntriesPerRow; ++i)
      sum = checked(sum + (uint)(ReadRaw(value, i) & CountMask));
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x18, 4), sum);
  }
}
