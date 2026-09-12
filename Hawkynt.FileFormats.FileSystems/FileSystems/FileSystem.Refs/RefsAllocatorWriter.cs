#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal enum RefsAllocatorTier {
  Medium,
  Container,
  Small,
}

/// <summary>
/// Mutates one of the three ReFS allocator trees. Medium and Container rows are
/// keyed in VLCN space; Small is the bootstrap allocator and is keyed directly
/// by physical LCN. Bitmap rows are 0x818 bytes with bits at +0x18; compact
/// rows are exactly 24 bytes and encode an all-free or all-allocated range.
/// </summary>
internal sealed class RefsAllocatorWriter {
  private const int BitmapOffset = 0x18;
  private const int BitmapBytes = 2048;
  private const ushort PartialFlag = 0x01;
  private const ushort CompactAllocatedFlag = 0x02;
  private const ushort FullyFreeFlag = 0x05;
  private const ushort FullyFreeAlternativeFlag = 0x09;

  private readonly RefsMetadataReader _metadata;
  private readonly RefsMetadataGraph _graph;
  private readonly RefsAllocatorTier _tier;
  private readonly int _rootIndex;
  private readonly bool _physicalAddressing;
  private readonly List<RowState> _rows = [];

  private sealed record RowState(ulong Start, ulong Length, RefsBTreeRow Row);

  public RefsAllocatorWriter(
      RefsMetadataReader metadata,
      RefsMetadataGraph graph,
      RefsAllocatorTier tier = RefsAllocatorTier.Medium) {
    this._metadata = metadata;
    this._graph = graph;
    this._tier = tier;
    this._rootIndex = tier switch {
      RefsAllocatorTier.Medium => 1,
      RefsAllocatorTier.Container => 2,
      RefsAllocatorTier.Small => 12,
      _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };
    this._physicalAddressing = tier == RefsAllocatorTier.Small;
    this.ReloadRows();
  }

  public RefsAllocatorTier Tier => this._tier;

  public bool CoversPhysical(ulong physicalLcn) {
    if (!this.TryToAllocatorLcn(physicalLcn, out var allocatorLcn)) return false;
    return this.Find(allocatorLcn) != null;
  }

  public bool AreAllocated(IEnumerable<ulong> physicalLcns, bool expectedAllocated) {
    foreach (var physical in physicalLcns.Distinct()) {
      if (!this.TryToAllocatorLcn(physical, out var allocatorLcn)) return false;
      var state = this.Find(allocatorLcn);
      if (state == null) return false;
      var index = allocatorLcn - state.Start;
      if (this.ReadAllocated(state.Row.Value, state.Length, index) != expectedAllocated) return false;
    }
    return true;
  }

  /// <summary>
  /// Finds a physically contiguous free run owned by this allocator tier. For
  /// virtual tiers each candidate VLCN is translated independently, so a run is
  /// never allowed to cross a container mapping discontinuity accidentally.
  /// </summary>
  public bool TryFindFreeRun(
      int clusterCount,
      Func<ulong, bool>? physicalPredicate,
      out ulong physicalStartLcn) {
    return this.TryFindBestFreeRun(
      clusterCount,
      physicalPredicate,
      static start => start,
      out physicalStartLcn);
  }

  public bool TryFindFreeRun(int clusterCount, out ulong physicalStartLcn)
    => this.TryFindFreeRun(clusterCount, physicalPredicate: null, out physicalStartLcn);

  /// <summary>
  /// Finds the allocator-owned contiguous free run with the smallest caller
  /// supplied score. This is deliberately allocator-local: ReFS placement must
  /// never choose a physically attractive target that belongs to the wrong
  /// Medium/Container/Small ownership domain.
  /// </summary>
  public bool TryFindBestFreeRun(
      int clusterCount,
      Func<ulong, bool>? physicalPredicate,
      Func<ulong, ulong> score,
      out ulong physicalStartLcn) {
    if (clusterCount <= 0) throw new ArgumentOutOfRangeException(nameof(clusterCount));
    ArgumentNullException.ThrowIfNull(score);

    var found = false;
    var bestStart = 0UL;
    var bestScore = ulong.MaxValue;

    foreach (var state in this._rows) {
      var run = new Queue<ulong>(clusterCount);
      ulong previousPhysical = 0;

      for (ulong index = 0; index < state.Length; ++index) {
        if (this.ReadAllocated(state.Row.Value, state.Length, index)) {
          run.Clear();
          continue;
        }

        var allocatorLcn = checked(state.Start + index);
        if (!this.TryAllocatorToPhysicalLcn(allocatorLcn, out var physical)
            || (physicalPredicate != null && !physicalPredicate(physical))) {
          run.Clear();
          continue;
        }

        if (run.Count > 0 && physical != previousPhysical + 1)
          run.Clear();
        run.Enqueue(physical);
        previousPhysical = physical;
        while (run.Count > clusterCount) run.Dequeue();
        if (run.Count != clusterCount) continue;

        var start = run.Peek();
        var candidateScore = score(start);
        if (found && candidateScore >= bestScore) continue;
        found = true;
        bestStart = start;
        bestScore = candidateScore;
      }
    }

    physicalStartLcn = bestStart;
    return found;
  }

  public IReadOnlyList<ulong> AllocateRun(
      int clusterCount,
      Func<ulong, bool>? physicalPredicate = null) {
    if (!this.TryFindFreeRun(clusterCount, physicalPredicate, out var start))
      throw new InvalidOperationException(
        $"ReFS {this._tier} Allocator has no physically contiguous free run of {clusterCount:N0} cluster(s) matching the placement constraints.");

    var slots = Consecutive(start, clusterCount);
    this.SetAllocated(slots, allocated: true);
    return slots;
  }

  public void FreeRun(ulong physicalStartLcn, int clusterCount) {
    if (clusterCount <= 0) throw new ArgumentOutOfRangeException(nameof(clusterCount));
    this.SetAllocated(Consecutive(physicalStartLcn, clusterCount), allocated: false);
  }

  public void SetAllocated(IEnumerable<ulong> physicalLcns, bool allocated) {
    var requests = new Dictionary<RefsBTreeRow, List<ulong>>();
    foreach (var physical in physicalLcns.Distinct()) {
      if (!this.TryToAllocatorLcn(physical, out var allocatorLcn))
        throw new InvalidOperationException(
          $"ReFS {this._tier} Allocator cannot address physical LCN 0x{physical:X}.");
      var state = this.Find(allocatorLcn)
        ?? throw new InvalidOperationException(
          $"ReFS {this._tier} Allocator does not cover {(this._physicalAddressing ? "LCN" : "VLCN")} 0x{allocatorLcn:X}.");
      if (!requests.TryGetValue(state.Row, out var list)) requests[state.Row] = list = [];
      list.Add(allocatorLcn - state.Start);
    }
    if (requests.Count == 0) return;

    var replacements = new List<(RefsBTreeRow Row, byte[] Value)>();
    foreach (var (row, indices) in requests) {
      var state = this._rows.First(r => ReferenceEquals(r.Row, row));
      var value = this.BuildValue(row.Value, state.Length, indices, allocated);
      if (!RefsPageEditor.CanReplaceValue(this._graph, row, value.Length))
        throw new InvalidOperationException(
          $"ReFS {this._tier} Allocator row expansion needs a B+ page split before this allocation can be committed.");
      replacements.Add((row, value));
    }

    var changed = new HashSet<ulong>();
    foreach (var (row, value) in replacements)
      changed.Add(RefsPageEditor.ReplaceValue(this._graph, row, value));
    this._graph.RefreshChecksumPaths(changed);
    this.ReloadRows();
  }

  private void ReloadRows() {
    this._rows.Clear();
    foreach (var row in this._metadata.WalkRoot(this._rootIndex)) {
      if (row.Value.Length < 24) continue;
      var start = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0, 8));
      var length = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(8, 8));
      if (length == 0 || length > BitmapBytes * 8UL) continue;
      if (!IsValidRow(row.Value, length)) continue;
      this._rows.Add(new RowState(start, length, row));
    }
    this._rows.Sort((a, b) => a.Start.CompareTo(b.Start));
  }

  private bool TryToAllocatorLcn(ulong physicalLcn, out ulong allocatorLcn) {
    if (this._physicalAddressing) {
      allocatorLcn = physicalLcn;
      return true;
    }
    return this._metadata.TryPhysicalToVirtualLcn(physicalLcn, out allocatorLcn);
  }

  private bool TryAllocatorToPhysicalLcn(ulong allocatorLcn, out ulong physicalLcn) {
    if (this._physicalAddressing) {
      physicalLcn = allocatorLcn;
      return true;
    }

    try {
      physicalLcn = this._metadata.TranslateVirtualLcn(allocatorLcn);
      return true;
    } catch (InvalidDataException) {
      physicalLcn = 0;
      return false;
    }
  }

  private RowState? Find(ulong allocatorLcn) {
    var lo = 0;
    var hi = this._rows.Count - 1;
    while (lo <= hi) {
      var mid = lo + ((hi - lo) >> 1);
      var row = this._rows[mid];
      if (allocatorLcn < row.Start) { hi = mid - 1; continue; }
      if (allocatorLcn - row.Start >= row.Length) { lo = mid + 1; continue; }
      return row;
    }
    return null;
  }

  private bool ReadAllocated(byte[] value, ulong rangeLength, ulong index) {
    if (index >= rangeLength) throw new InvalidDataException("ReFS allocator index lies outside its row range.");
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x12, 2));
    return flags switch {
      PartialFlag when value.Length >= BitmapOffset + BitmapBytes
        => (value[BitmapOffset + checked((int)(index >> 3))] & (1 << checked((int)(index & 7)))) != 0,
      CompactAllocatedFlag => true,
      FullyFreeFlag or FullyFreeAlternativeFlag => false,
      _ => throw new InvalidDataException($"ReFS allocator row has unsupported flags 0x{flags:X4}/size {value.Length}.")
    };
  }

  private byte[] BuildValue(
      byte[] original,
      ulong rangeLength,
      IReadOnlyList<ulong> indices,
      bool allocated) {
    if (rangeLength > BitmapBytes * 8UL)
      throw new InvalidOperationException(
        $"ReFS {this._tier} Allocator row length {rangeLength:N0} exceeds the decoded bitmap capacity.");

    var bitmap = new byte[BitmapBytes];
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(0x12, 2));
    switch (flags) {
      case PartialFlag when original.Length >= BitmapOffset + BitmapBytes:
        original.AsSpan(BitmapOffset, BitmapBytes).CopyTo(bitmap);
        break;
      case CompactAllocatedFlag:
        bitmap.AsSpan().Fill(0xFF);
        break;
      case FullyFreeFlag:
      case FullyFreeAlternativeFlag:
        break;
      default:
        throw new InvalidDataException($"ReFS allocator row has unsupported flags 0x{flags:X4}/size {original.Length}.");
    }

    foreach (var index in indices) {
      if (index >= rangeLength) throw new InvalidOperationException("ReFS allocator bit lies outside its row range.");
      var byteIndex = checked((int)(index >> 3));
      var mask = (byte)(1 << (int)(index & 7));
      if (allocated) bitmap[byteIndex] |= mask;
      else bitmap[byteIndex] &= unchecked((byte)~mask);
    }

    var used = 0;
    for (ulong i = 0; i < rangeLength; ++i)
      if ((bitmap[(int)(i >> 3)] & (1 << (int)(i & 7))) != 0) ++used;
    var free = checked((int)rangeLength - used);
    if (used > ushort.MaxValue || free > ushort.MaxValue)
      throw new InvalidOperationException("ReFS allocator counts exceed their on-disk fields.");

    if (used == 0 || free == 0) {
      var compact = new byte[24];
      original.AsSpan(0, Math.Min(24, original.Length)).CopyTo(compact);
      BinaryPrimitives.WriteUInt16LittleEndian(compact.AsSpan(0x10, 2), checked((ushort)free));
      BinaryPrimitives.WriteUInt16LittleEndian(compact.AsSpan(0x12, 2), used == 0 ? FullyFreeFlag : CompactAllocatedFlag);
      BinaryPrimitives.WriteUInt16LittleEndian(compact.AsSpan(0x14, 2), this.CompactHeaderSize());
      BinaryPrimitives.WriteUInt16LittleEndian(compact.AsSpan(0x16, 2), checked((ushort)used));
      return compact;
    }

    var result = new byte[BitmapOffset + BitmapBytes];
    original.AsSpan(0, Math.Min(BitmapOffset, original.Length)).CopyTo(result);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x10, 2), checked((ushort)free));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x12, 2), PartialFlag);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x14, 2), this.BitmapHeaderSize());
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x16, 2), checked((ushort)used));
    bitmap.CopyTo(result, BitmapOffset);
    return result;
  }

  private static bool IsValidRow(byte[] value, ulong rangeLength) {
    if (value.Length < 24) return false;
    var free = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x10, 2));
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x12, 2));
    var used = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x16, 2));
    if ((ulong)free + used != rangeLength) return false;

    if (flags == PartialFlag) {
      if (value.Length < BitmapOffset + BitmapBytes) return false;
      var popcount = 0;
      for (ulong i = 0; i < rangeLength; ++i)
        if ((value[BitmapOffset + (int)(i >> 3)] & (1 << (int)(i & 7))) != 0) ++popcount;
      return popcount == used;
    }

    if (flags == CompactAllocatedFlag) return free == 0 && used == rangeLength;
    if (flags is FullyFreeFlag or FullyFreeAlternativeFlag) return used == 0 && free == rangeLength;
    return false;
  }

  private ushort CompactHeaderSize()
    => this._tier == RefsAllocatorTier.Medium && this._metadata.Header.MinorVersion >= 7
      ? (ushort)0x0200
      : (ushort)0x0100;

  private ushort BitmapHeaderSize()
    => this._tier == RefsAllocatorTier.Medium && this._metadata.Header.MinorVersion >= 7
      ? (ushort)0x0218
      : (ushort)0x0118;

  private static ulong[] Consecutive(ulong start, int count) {
    var result = new ulong[count];
    for (var i = 0; i < count; ++i) result[i] = checked(start + (ulong)i);
    return result;
  }
}
