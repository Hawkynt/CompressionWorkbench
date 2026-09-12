#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// Native CoW editor for root #6 / schema 0xe0b0. Existing normal rows can be
/// incremented/decremented, new normal rows are materialized from the active
/// checkpoint clock, and rows are removed when every count/flag reaches zero.
///
/// The Block Refcount tree is sparse: an untracked live data cluster is treated
/// as having one implicit ordinary owner. Adding the first clone therefore
/// materializes count 2, both when the containing 0x400 row is absent and when
/// the row exists for unrelated shared clusters but the target slot is zero.
/// </summary>
internal sealed class RefsCowBlockRefcountEditor {
  private readonly RefsMetadataReader _metadata;
  private readonly RefsCowBTree _tree;

  public RefsCowBlockRefcountEditor(RefsMetadataReader metadata, RefsCowBTree tree) {
    ArgumentNullException.ThrowIfNull(metadata);
    ArgumentNullException.ThrowIfNull(tree);
    this._metadata = metadata;
    this._tree = tree;
  }

  public RefsCowTreeResult IncrementPhysicalReferences(IEnumerable<ulong> physicalLcns)
    => this.AdjustPhysicalReferences(physicalLcns, +1);

  public RefsCowTreeResult DecrementPhysicalReferences(IEnumerable<ulong> physicalLcns)
    => this.AdjustPhysicalReferences(physicalLcns, -1);

  public RefsCowTreeResult AdjustPhysicalReferences(
      IEnumerable<ulong> physicalLcns,
      int deltaPerReference) {
    ArgumentNullException.ThrowIfNull(physicalLcns);
    if (deltaPerReference is not (-1 or +1)) throw new ArgumentOutOfRangeException(nameof(deltaPerReference));
    if (this._metadata.Roots.Count <= 6 || this._metadata.Roots[6].Lcns.Count == 0)
      throw new InvalidDataException("ReFS Block Refcount root #6 is unavailable.");

    var changes = new Dictionary<ulong, Dictionary<int, int>>();
    var any = false;
    foreach (var physical in physicalLcns) {
      any = true;
      if (!this._metadata.TryPhysicalToVirtualLcn(physical, out var virtualLcn))
        throw new InvalidDataException($"ReFS Block Refcount PLCN 0x{physical:X} has no virtual-container address.");
      var rowStart = virtualLcn & ~((ulong)RefsBlockRefcountCodec.EntriesPerRow - 1UL);
      var index = checked((int)(virtualLcn - rowStart));
      if (!changes.TryGetValue(rowStart, out var rowChanges))
        changes[rowStart] = rowChanges = [];
      rowChanges[index] = checked(rowChanges.GetValueOrDefault(index) + deltaPerReference);
    }
    if (!any) throw new ArgumentException("ReFS Block Refcount mutation requires at least one cluster.", nameof(physicalLcns));

    return this._tree.Rewrite(
      this._metadata.Roots[6],
      virtualAddresses: true,
      (rows, comparer) => this.ApplyChanges(rows, comparer, changes));
  }

  private bool ApplyChanges(
      List<RefsTreeRow> rows,
      RefsKeyComparer comparer,
      IReadOnlyDictionary<ulong, Dictionary<int, int>> changes) {
    foreach (var (rowStart, requestedDeltas) in changes.OrderBy(item => item.Key)) {
      var matching = new List<int>();
      for (var i = 0; i < rows.Count; ++i) {
        var candidate = rows[i];
        if (candidate.Key.Length < 16) continue;
        var keyStart = BinaryPrimitives.ReadUInt64LittleEndian(candidate.Key.AsSpan(0, 8));
        var keyCount = BinaryPrimitives.ReadUInt64LittleEndian(candidate.Key.AsSpan(8, 8));
        if (keyStart == rowStart && keyCount == RefsBlockRefcountCodec.EntriesPerRow)
          matching.Add(i);
      }

      if (matching.Count > 1)
        throw new InvalidDataException($"ReFS Block Refcount contains duplicate row key 0x{rowStart:X}+0x400.");

      if (matching.Count == 0) {
        if (requestedDeltas.Values.Any(delta => delta < 0))
          throw new InvalidDataException(
            $"ReFS Block Refcount decrement targets untracked VLCN range 0x{rowStart:X}+0x400.");

        var key = RefsBlockRefcountCodec.BuildKey(rowStart);
        var fresh = RefsBlockRefcountCodec.BuildFreshValue(
          rowStart,
          checked(this._metadata.ActiveCheckpointClock + 1));
        var changed = RefsBlockRefcountCodec.AddCloneReferences(fresh, requestedDeltas);
        rows.Insert(FindInsertion(rows, key, comparer), new RefsTreeRow(key, changed));
        continue;
      }

      var rowIndex = matching[0];
      var row = rows[rowIndex];
      if (!RefsBlockRefcountCodec.TryGetRange(row.Value, out var valueStart, out var valueCount)
          || valueStart != rowStart
          || valueCount != RefsBlockRefcountCodec.EntriesPerRow
          || !RefsBlockRefcountCodec.HasValidTotal(row.Value))
        throw new NotSupportedException(
          $"ReFS Block Refcount range 0x{rowStart:X}+0x400 is not a writable normal 0x820-byte row.");

      var updated = requestedDeltas.Values.All(delta => delta > 0)
        ? RefsBlockRefcountCodec.AddCloneReferences(row.Value, requestedDeltas)
        : RefsBlockRefcountCodec.AdjustCounts(row.Value, requestedDeltas);
      if (RefsBlockRefcountCodec.IsUnflaggedZeroRow(updated))
        rows.RemoveAt(rowIndex);
      else
        rows[rowIndex] = row with { Value = updated };
    }
    return true;
  }

  private static int FindInsertion(
      IReadOnlyList<RefsTreeRow> rows,
      byte[] key,
      RefsKeyComparer comparer) {
    var lo = 0;
    var hi = rows.Count;
    while (lo < hi) {
      var mid = lo + ((hi - lo) >> 1);
      if (comparer.Compare(rows[mid].Key, key) < 0) lo = mid + 1;
      else hi = mid;
    }
    return lo;
  }
}
