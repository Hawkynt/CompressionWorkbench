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

  /// <summary>
  /// The row layout measured on volumes Windows Server 2022/2025 formatted: a partial row's
  /// free count (+0x10) is the range length minus the bitmap's population, while +0x16 holds a
  /// value that is not the used count and that Windows leaves unchanged when it frees clusters;
  /// it is kept as found. Changes that would turn a row compact (all free or all allocated) or
  /// touch a compact row are refused, since how Windows rewrites those has not been measured.
  /// </summary>
  private readonly bool _windowsLayout;
  private readonly List<RowState> _rows = [];

  private sealed record RowState(ulong Start, ulong Length, RefsBTreeRow Row);

  public RefsAllocatorWriter(
      RefsMetadataReader metadata,
      RefsMetadataGraph graph,
      RefsAllocatorTier tier = RefsAllocatorTier.Medium,
      bool? physicalAddressing = null) {
    this._metadata = metadata;
    this._graph = graph;
    this._tier = tier;
    this._rootIndex = tier switch {
      RefsAllocatorTier.Medium => 1,
      RefsAllocatorTier.Container => 2,
      RefsAllocatorTier.Small => 12,
      _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };
    // Volumes Windows Server 2022/2025 format key the Medium Allocator by physical LCN (its rows
    // cover exactly the volume's clusters); a caller that has established that passes it.
    this._physicalAddressing = physicalAddressing ?? tier == RefsAllocatorTier.Small;
    this._windowsLayout = physicalAddressing == true && tier == RefsAllocatorTier.Medium;
    this.ReloadRows();
  }

  public RefsAllocatorTier Tier => this._tier;

  public bool CoversPhysical(ulong physicalLcn) {
    if (!this.TryToAllocatorLcn(physicalLcn, out var allocatorLcn)) return false;
    return this.Find(allocatorLcn) != null;
  }

  /// <summary>
  /// The Medium Allocator of a volume Windows formatted: keyed by physical LCN, its rows lying
  /// within the volume's clusters, and showing every one of <paramref name="allocatedClusters"/>
  /// allocated. Null when the volume's allocator is not of that shape.
  /// </summary>
  public static RefsAllocatorWriter? WindowsMedium(RefsMetadataReader metadata, RefsMetadataGraph graph, IReadOnlyCollection<ulong> allocatedClusters) {
    try {
      var writer = new RefsAllocatorWriter(metadata, graph, RefsAllocatorTier.Medium, physicalAddressing: true);
      if (writer._rows.Count == 0 || writer.CoverageEnd > metadata.Header.TotalClusters) return null;
      if (!allocatedClusters.All(writer.CoversPhysical) || !writer.AreAllocated(allocatedClusters, expectedAllocated: true)) return null;
      return writer;
    } catch (Exception e) when (e is InvalidOperationException or InvalidDataException) {
      return null;
    }
  }

  /// <summary>One past the highest allocator LCN any row covers.</summary>
  public ulong CoverageEnd => this._rows.Count == 0 ? 0 : this._rows.Max(static r => r.Start + r.Length);

  /// <summary>The physical range of the row that covers <paramref name="physicalLcn"/>, for a physically addressed allocator; null otherwise.</summary>
  public (ulong Start, ulong Length)? RangeOfPhysical(ulong physicalLcn) {
    if (!this._physicalAddressing) return null;
    return this.Find(physicalLcn) is { } state ? (state.Start, state.Length) : null;
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
    var flipped = 0L;
    foreach (var (row, indices) in requests) {
      var state = this._rows.First(r => ReferenceEquals(r.Row, row));
      flipped += indices.Count(i => this.ReadAllocated(row.Value, state.Length, i) != allocated);
      var value = this.BuildValue(row.Value, state.Length, indices, allocated);
      if (!RefsPageEditor.CanReplaceValue(this._graph, row, value.Length))
        throw new InvalidOperationException(
          $"ReFS {this._tier} Allocator row expansion needs a B+ page split before this allocation can be committed.");
      replacements.Add((row, value));
    }
    var summary = this._windowsLayout ? this.FreeSummary() : null;
    var freeDelta = allocated ? -flipped : flipped;
    if (summary is { } s && ((long)s.Free + freeDelta < 0 || (long)s.Free + freeDelta > (long)s.Total))
      throw new InvalidOperationException($"ReFS {this._tier} Allocator's free-cluster count would leave its range.");

    var changed = new HashSet<ulong>();
    foreach (var (row, value) in replacements)
      changed.Add(RefsPageEditor.ReplaceValue(this._graph, row, value));
    if (summary is { } root && freeDelta != 0) {
      var page = this._graph.ReadPage(root.Page);
      BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x88, 8), (ulong)((long)root.Free + freeDelta));
      this._graph.WritePage(root.Page, page);
      changed.Add(root.Page);
    }
    this._graph.RefreshChecksumPaths(changed);
    this.ReloadRows();
  }

  /// <summary>The root header's free count and the free clusters its rows show (partial rows by their count, compact rows whole), or null without that header.</summary>
  internal (ulong Header, ulong Rows)? FreeCounts() {
    if (this.FreeSummary() is not { } summary) return null;
    ulong rows = 0;
    foreach (var state in this._rows) {
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(state.Row.Value.AsSpan(0x12, 2));
      rows += flags switch {
        PartialFlag => BinaryPrimitives.ReadUInt16LittleEndian(state.Row.Value.AsSpan(0x10, 2)),
        FullyFreeFlag or FullyFreeAlternativeFlag => state.Length,
        _ => 0,
      };
    }
    return (summary.Free, rows);
  }

  /// <summary>
  /// The Medium Allocator's root page keeps the clusters its rows cover (u64 at 0x80, the
  /// volume's cluster count) and how many of them are free (u64 at 0x88) right after its table
  /// descriptor; Windows moves the free count with every allocation and release (measured: +4
  /// after it removed a 4-cluster file, +1 for one 64 KiB cluster), and this writer does the
  /// same. Null when the root page does not hold that header.
  /// </summary>
  private (ulong Page, ulong Total, ulong Free)? FreeSummary() {
    var reference = this._metadata.Roots[this._rootIndex];
    if (reference.Lcns.Count == 0) return null;
    var head = this._metadata.TranslateVirtualLcn(reference.Lcns[0]);
    var page = this._graph.ReadPage(head);
    if (page.Length < 0x90 || BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(0x54, 2)) != 0x28) return null;
    var total = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(0x80, 8));
    var free = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(0x88, 8));
    return total == this._metadata.Header.TotalClusters && free <= total ? (head, total, free) : null;
  }

  private void ReloadRows() {
    this._rows.Clear();
    foreach (var row in this._metadata.WalkRoot(this._rootIndex)) {
      if (row.Value.Length < 24) continue;
      var start = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0, 8));
      var length = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(8, 8));
      if (length == 0 || length > BitmapBytes * 8UL) continue;
      if (!(this._windowsLayout ? IsValidWindowsRow(row.Value, length) : IsValidRow(row.Value, length))) continue;
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
      PartialFlag when value.Length >= BitmapOffset + (int)((rangeLength + 7) / 8)
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
      case PartialFlag when original.Length >= BitmapOffset + (int)((rangeLength + 7) / 8):
        original.AsSpan(BitmapOffset, Math.Min(BitmapBytes, original.Length - BitmapOffset)).CopyTo(bitmap);
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

    if (this._windowsLayout) {
      if (flags != PartialFlag || used == 0 || free == 0)
        throw new InvalidOperationException("ReFS allocator row would change between compact and bitmap form; that rewrite is not made.");
      var row = original.ToArray();
      BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(0x10, 2), checked((ushort)free));
      bitmap.AsSpan(0, row.Length - BitmapOffset).CopyTo(row.AsSpan(BitmapOffset));
      return row;
    }
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

  private static bool IsValidWindowsRow(byte[] value, ulong rangeLength) {
    if (value.Length < 24) return false;
    var free = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x10, 2));
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x12, 2));
    if (flags == PartialFlag) {
      if (value.Length < BitmapOffset + (int)((rangeLength + 7) / 8)) return false;
      var popcount = 0UL;
      for (ulong i = 0; i < rangeLength; ++i)
        if ((value[BitmapOffset + (int)(i >> 3)] & (1 << (int)(i & 7))) != 0) ++popcount;
      return free == rangeLength - popcount;
    }
    // Compact rows: all allocated (free 0) or all free (free = length); kept readable so
    // AreAllocated can answer, never rewritten in this layout.
    return flags == CompactAllocatedFlag ? free == 0 : flags is FullyFreeFlag or FullyFreeAlternativeFlag && free == rangeLength;
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
