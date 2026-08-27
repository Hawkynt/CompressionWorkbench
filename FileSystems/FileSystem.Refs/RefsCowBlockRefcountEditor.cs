#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// Native CoW editor for the populated Block Refcount tree. Existing normal
/// rows can be incremented/decremented and are removed when every count/flag
/// reaches zero. Creating a brand-new 0x820 row remains fail-closed because the
/// +0x10 modification-stamp creation rule is not yet proven.
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
      (rows, _) => ApplyChanges(rows, changes));
  }

  private static bool ApplyChanges(
      List<RefsTreeRow> rows,
      IReadOnlyDictionary<ulong, Dictionary<int, int>> changes) {
    foreach (var (rowStart, deltas) in changes.OrderBy(item => item.Key)) {
      var matching = new List<int>();
      for (var i = 0; i < rows.Count; ++i) {
        var candidate = rows[i];
        if (candidate.Key.Length < 16) continue;
        var keyStart = BinaryPrimitives.ReadUInt64LittleEndian(candidate.Key.AsSpan(0, 8));
        var keyCount = BinaryPrimitives.ReadUInt64LittleEndian(candidate.Key.AsSpan(8, 8));
        if (keyStart == rowStart && keyCount == RefsBlockRefcountCodec.EntriesPerRow)
          matching.Add(i);
      }

      if (matching.Count == 0) {
        if (deltas.Values.Any(delta => delta < 0))
          throw new InvalidDataException(
            $"ReFS Block Refcount decrement targets untracked VLCN range 0x{rowStart:X}+0x400.");
        throw new NotSupportedException(
          $"ReFS Block Refcount row creation for VLCN range 0x{rowStart:X}+0x400 requires the unresolved +0x10 creation-stamp rule.");
      }
      if (matching.Count != 1)
        throw new InvalidDataException($"ReFS Block Refcount contains duplicate row key 0x{rowStart:X}+0x400.");

      var rowIndex = matching[0];
      var row = rows[rowIndex];
      if (!RefsBlockRefcountCodec.TryGetRange(row.Value, out var valueStart, out var valueCount)
          || valueStart != rowStart
          || valueCount != RefsBlockRefcountCodec.EntriesPerRow
          || !RefsBlockRefcountCodec.HasValidTotal(row.Value))
        throw new NotSupportedException(
          $"ReFS Block Refcount range 0x{rowStart:X}+0x400 is not a writable normal 0x820-byte row.");

      var changed = RefsBlockRefcountCodec.AdjustCounts(row.Value, deltas);
      if (RefsBlockRefcountCodec.IsUnflaggedZeroRow(changed))
        rows.RemoveAt(rowIndex);
      else
        rows[rowIndex] = row with { Value = changed };
    }
    return true;
  }
}
