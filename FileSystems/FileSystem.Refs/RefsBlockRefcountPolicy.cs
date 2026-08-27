#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// Functional mutation policy for root #6. It can materialize a previously
/// absent 0x400-cluster row when a singly-owned extent becomes shared, increment
/// existing entries, preserve the two high management flags, and rewrite the
/// TotalRefCount invariant before publishing a CoW replacement root.
/// </summary>
internal sealed class RefsBlockRefcountPolicy {
  private const ulong RangeClusters = 0x400;
  private const int ValueSize = 0x820;
  private const int CountsOffset = 0x1C;
  private const ushort CountMask = 0x3FFF;
  private const ushort FlagsMask = 0xC000;

  private readonly RefsMetadataReader _metadata;
  private readonly RefsCowBTree _btree;

  public RefsBlockRefcountPolicy(RefsMetadataReader metadata, RefsCowBTree btree) {
    this._metadata = metadata;
    this._btree = btree;
  }

  /// <summary>
  /// Adds references to already-live clusters. An untracked cluster is assumed
  /// to have its ordinary single live owner; adding one clone therefore creates
  /// a refcount of 2 rather than 1.
  /// </summary>
  public RefsCowTreeResult IncrementPhysicalReferences(
      IEnumerable<ulong> physicalClusters,
      ushort additionalReferences = 1,
      ushort managementFlags = 0) {
    if (additionalReferences == 0) throw new ArgumentOutOfRangeException(nameof(additionalReferences));
    if ((managementFlags & ~FlagsMask) != 0) throw new ArgumentOutOfRangeException(nameof(managementFlags));
    var requested = BuildRequest(physicalClusters);
    return this.Rewrite(requested, (current, existed) => {
      var flags = (ushort)((current & FlagsMask) | managementFlags);
      var count = current & CountMask;
      var next = existed
        ? checked(count + additionalReferences)
        : checked(1 + additionalReferences);
      if (next > CountMask) throw new OverflowException("ReFS block refcount exceeds its 14-bit on-disk field.");
      return (ushort)(flags | next);
    });
  }

  public RefsCowTreeResult ClonePhysicalReferences(IEnumerable<ulong> physicalClusters)
    => this.IncrementPhysicalReferences(physicalClusters, additionalReferences: 1);

  public RefsCowTreeResult DecrementPhysicalReferences(
      IEnumerable<ulong> physicalClusters,
      ushort references = 1) {
    if (references == 0) throw new ArgumentOutOfRangeException(nameof(references));
    var requested = BuildRequest(physicalClusters);
    return this.Rewrite(requested, (current, existed) => {
      if (!existed) throw new InvalidOperationException("ReFS cannot decrement an untracked shared cluster.");
      var flags = (ushort)(current & FlagsMask);
      var count = current & CountMask;
      if (count < references)
        throw new InvalidDataException("ReFS block-refcount underflow would occur.");
      return (ushort)(flags | (count - references));
    });
  }

  private RefsCowTreeResult Rewrite(
      IReadOnlyDictionary<ulong, Dictionary<int, ulong>> request,
      Func<ushort, bool, ushort> mutate) {
    if (this._metadata.Roots.Count <= 6 || this._metadata.Roots[6].Lcns.Count == 0)
      throw new NotSupportedException("ReFS Block Refcount root #6 is unavailable.");

    return this._btree.Rewrite(this._metadata.Roots[6], virtualAddresses: true, (rows, comparer) => {
      var byStart = new Dictionary<ulong, int>();
      for (var i = 0; i < rows.Count; ++i) {
        if (!TryParseKey(rows[i].Key, out var start)) continue;
        byStart[start] = i;
      }

      foreach (var (rangeStart, entries) in request.OrderBy(p => p.Key)) {
        RefsTreeRow row;
        var rowExists = byStart.TryGetValue(rangeStart, out var rowIndex);
        if (rowExists) {
          row = rows[rowIndex];
          if (row.Value.Length != ValueSize)
            throw new NotSupportedException(
              $"ReFS Block Refcount row 0x{rangeStart:X} uses unsupported {row.Value.Length}-byte value form.");
          ValidateValue(row.Value, rangeStart);
        } else {
          var key = BuildKey(rangeStart);
          var value = BuildEmptyValue(rangeStart, this._metadata.ActiveCheckpointClock);
          row = new RefsTreeRow(key, value, 0);
          rowIndex = ~FindInsertion(rows, key, comparer);
          if (rowIndex < 0) throw new IOException("ReFS refcount row key unexpectedly already exists.");
          rows.Insert(rowIndex, row);
          byStart[rangeStart] = rowIndex;
          // insertion shifts following indices; rebuild the map once for safety.
          byStart.Clear();
          for (var i = 0; i < rows.Count; ++i)
            if (TryParseKey(rows[i].Key, out var s)) byStart[s] = i;
          rowIndex = byStart[rangeStart];
        }

        var updated = row.Value.ToArray();
        foreach (var (slot, _) in entries) {
          var offset = CountsOffset + slot * 2;
          var current = BinaryPrimitives.ReadUInt16LittleEndian(updated.AsSpan(offset, 2));
          var existed = rowExists && (current & CountMask) != 0;
          var next = mutate(current, existed);
          BinaryPrimitives.WriteUInt16LittleEndian(updated.AsSpan(offset, 2), next);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(updated.AsSpan(0x10, 8), this._metadata.ActiveCheckpointClock + 1);
        RefreshTotal(updated);
        rows[rowIndex] = row with { Value = updated };
      }
      return true;
    });
  }

  private IReadOnlyDictionary<ulong, Dictionary<int, ulong>> BuildRequest(IEnumerable<ulong> physicalClusters) {
    ArgumentNullException.ThrowIfNull(physicalClusters);
    var result = new Dictionary<ulong, Dictionary<int, ulong>>();
    foreach (var physical in physicalClusters.Distinct()) {
      if (!this._metadata.TryPhysicalToVirtualLcn(physical, out var vlcn))
        throw new InvalidDataException($"ReFS shared data PLCN 0x{physical:X} has no VLCN mapping.");
      var rangeStart = vlcn & ~(RangeClusters - 1);
      var index = checked((int)(vlcn - rangeStart));
      if (!result.TryGetValue(rangeStart, out var entries)) result[rangeStart] = entries = [];
      entries[index] = physical;
    }
    if (result.Count == 0) throw new ArgumentException("No ReFS clusters were supplied.", nameof(physicalClusters));
    return result;
  }

  private static byte[] BuildKey(ulong start) {
    var key = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(0, 8), start);
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8, 8), RangeClusters);
    return key;
  }

  private static byte[] BuildEmptyValue(ulong start, ulong stamp) {
    var value = new byte[ValueSize];
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x00, 8), start);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x08, 8), RangeClusters);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x10, 8), stamp);
    return value;
  }

  private static bool TryParseKey(byte[] key, out ulong start) {
    start = 0;
    if (key.Length < 16 || BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8, 8)) != RangeClusters) return false;
    start = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(0, 8));
    return true;
  }

  private static void ValidateValue(byte[] value, ulong expectedStart) {
    if (BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(0x00, 8)) != expectedStart
        || BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(0x08, 8)) != RangeClusters)
      throw new InvalidDataException("ReFS Block Refcount value does not echo its key range.");
    uint total = 0;
    for (ulong i = 0; i < RangeClusters; ++i)
      total = checked(total + (uint)(BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(CountsOffset + checked((int)i) * 2, 2)) & CountMask));
    if (BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(0x18, 4)) != total)
      throw new InvalidDataException("ReFS Block Refcount TotalRefCount does not match its per-cluster array.");
  }

  private static void RefreshTotal(Span<byte> value) {
    uint total = 0;
    for (ulong i = 0; i < RangeClusters; ++i)
      total = checked(total + (uint)(BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(CountsOffset + checked((int)i) * 2, 2)) & CountMask));
    BinaryPrimitives.WriteUInt32LittleEndian(value.Slice(0x18, 4), total);
  }

  private static int FindInsertion(IReadOnlyList<RefsTreeRow> rows, byte[] key, RefsKeyComparer comparer) {
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
}
