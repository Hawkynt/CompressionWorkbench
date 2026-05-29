#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Core.Layout;

/// <summary>
/// A single planned cluster move: source offset, destination offset, byte
/// length, and the file name the extent belongs to. Produced by
/// <see cref="DefragPlanner"/> and consumed by <see cref="IFilesystemBlockMover"/>.
/// </summary>
public sealed record class ClusterMove(
  long SrcOffset,
  long DstOffset,
  long Length,
  string FileName);

/// <summary>
/// Planner-driven defrag engine. Given the current extent layout (from
/// <see cref="IFilesystemExtentMap.EnumerateExtents"/>), a
/// <see cref="LayoutProfile"/>, and the image geometry, computes an ordered
/// list of <see cref="ClusterMove"/>s that transform the current layout into
/// the target layout.
///
/// <para>Algorithm overview (Performance profile):</para>
/// <list type="number">
/// <item>Classify files into zones (Hot / Normal / Cold / Frozen) based on
/// modification time or listing order.</item>
/// <item>Compute target offsets: Hot at front, then Normal, Cold, Frozen at
/// end. Within each zone, files are ordered largest-first to minimise
/// fragmentation of remaining free space.</item>
/// <item>Build a dependency graph: move A depends on B if A's target
/// overlaps B's current position.</item>
/// <item>Topological sort the moves. For cycles (A→B→A), use a free region
/// as temporary staging — one extra move per cycle.</item>
/// <item>Return the ordered list.</item>
/// </list>
///
/// <para>Quick profile: only consolidates per-file fragments (makes each
/// file contiguous) without global rearrangement.</para>
/// </summary>
public static class DefragPlanner {

  /// <summary>
  /// Plans moves for the given extent map and layout profile.
  /// </summary>
  /// <param name="extents">Current on-disk layout from
  /// <see cref="IFilesystemExtentMap.EnumerateExtents"/>.</param>
  /// <param name="dataOrigin">Byte offset where the data region begins
  /// (metadata region ends). Moves never target offsets below this.</param>
  /// <param name="imageSize">Total image size in bytes.</param>
  /// <param name="clusterSize">Cluster (allocation unit) size in bytes.</param>
  /// <param name="profile">Layout strategy.</param>
  /// <param name="mode">Defrag mode (ConsolidateAtStart, ConsolidateAtEnd, etc.).</param>
  /// <param name="interleaveStride">Block interleave factor (1 = contiguous).
  /// When &gt; 1, each file's Kth block is placed at (start + K*stride*clusterSize)
  /// and files are assigned to round-robin lanes within the stride.</param>
  /// <param name="holeSize">Size of the hole to create (0 = no hole).</param>
  /// <param name="holeAt">Byte offset at which to create the hole (-1 = no hole).</param>
  /// <param name="metadataZone">Controls where metadata and directory extents are placed.
  /// <see cref="MetadataZone.Unchanged"/> (default) preserves current positions.</param>
  /// <returns>Ordered list of moves to execute.</returns>
  public static IReadOnlyList<ClusterMove> Plan(
    IReadOnlyList<DefragBlockInfo> extents,
    long dataOrigin,
    long imageSize,
    int clusterSize,
    LayoutProfile profile,
    DefragMode mode,
    int interleaveStride = 1,
    long holeSize = 0,
    long holeAt = -1,
    MetadataZone metadataZone = MetadataZone.Unchanged) {
    ArgumentNullException.ThrowIfNull(extents);
    if (clusterSize <= 0) throw new ArgumentOutOfRangeException(nameof(clusterSize));
    if (interleaveStride < 1 || interleaveStride > 256)
      throw new ArgumentOutOfRangeException(nameof(interleaveStride), "InterleaveStride must be between 1 and 256.");

    // Separate into file extents, free regions, and forbidden (metadata/bad).
    // MetadataReserved and Bad regions are immovable AND target offsets must
    // never overlap them — otherwise files get written on top of directory
    // structures or bad blocks.
    var fileExtents = new List<DefragBlockInfo>();
    var freeRegions = new List<(long Offset, long Length)>();
    var forbiddenRaw = new List<(long Start, long End)>();
    foreach (var e in extents) {
      switch (e.Kind) {
        case DefragBlockKind.Used:
          fileExtents.Add(e);
          break;
        case DefragBlockKind.Free:
          freeRegions.Add((e.Offset, e.Length));
          break;
        case DefragBlockKind.MetadataReserved:
        case DefragBlockKind.Bad:
          forbiddenRaw.Add((e.Offset, e.Offset + e.Length));
          break;
      }
    }
    var forbidden = MergeIntervals(forbiddenRaw);

    if (fileExtents.Count == 0) return [];

    // Group extents by file name.
    var byFile = new Dictionary<string, List<DefragBlockInfo>>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in fileExtents) {
      var key = e.FileName ?? "<unknown>";
      if (!byFile.TryGetValue(key, out var list))
        byFile[key] = list = [];
      list.Add(e);
    }

    // Sort each file's extents by offset.
    foreach (var list in byFile.Values)
      list.Sort((a, b) => a.Offset.CompareTo(b.Offset));

    // Compute per-file total size.
    var fileSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, exts) in byFile)
      fileSizes[name] = exts.Sum(e => e.Length);

    if (mode == DefragMode.CarveHole)
      return PlanCarveHole(byFile, fileSizes, fileExtents, dataOrigin, imageSize, clusterSize, freeRegions, holeSize, holeAt);

    // When metadata zone placement is requested, separate metadata/directory
    // extents from file-data extents and apply zone-based ordering.
    if (metadataZone != MetadataZone.Unchanged) {
      // Collect metadata-reserved extents from the original extent list.
      var metadataExtents = new List<DefragBlockInfo>();
      foreach (var e in extents)
        if (e.Kind == DefragBlockKind.MetadataReserved)
          metadataExtents.Add(e);

      // Separate directory extents from file-data extents.
      var dirByFile = new Dictionary<string, List<DefragBlockInfo>>(StringComparer.OrdinalIgnoreCase);
      var dataByFile = new Dictionary<string, List<DefragBlockInfo>>(StringComparer.OrdinalIgnoreCase);
      var dirSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
      var dataSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

      foreach (var (name, exts) in byFile) {
        if (IsDirectoryExtent(name)) {
          dirByFile[name] = exts;
          dirSizes[name] = fileSizes[name];
        } else {
          dataByFile[name] = exts;
          dataSizes[name] = fileSizes[name];
        }
      }

      return PlanMetadataZone(metadataExtents, dirByFile, dirSizes, dataByFile, dataSizes,
        dataOrigin, imageSize, clusterSize, freeRegions, forbidden, mode, metadataZone);
    }

    return profile switch {
      LayoutProfile.Quick => PlanQuick(byFile, fileSizes, dataOrigin, imageSize, clusterSize, freeRegions),
      _ => interleaveStride > 1
        ? PlanInterleaved(byFile, fileSizes, dataOrigin, imageSize, clusterSize, freeRegions, mode, interleaveStride)
        : PlanPerformance(byFile, fileSizes, dataOrigin, imageSize, clusterSize, freeRegions, forbidden, mode),
    };
  }

  // ── Quick profile: per-file consolidation only ─────────────────────────

  private static IReadOnlyList<ClusterMove> PlanQuick(
    Dictionary<string, List<DefragBlockInfo>> byFile,
    Dictionary<string, long> fileSizes,
    long dataOrigin, long imageSize, int clusterSize,
    List<(long Offset, long Length)> freeRegions) {
    var moves = new List<ClusterMove>();

    foreach (var (fileName, extents) in byFile) {
      if (extents.Count <= 1) continue; // already contiguous

      // Find a free region large enough to hold the entire file.
      var totalSize = fileSizes[fileName];
      var alignedSize = AlignUp(totalSize, clusterSize);
      var freeIdx = FindFreeRegion(freeRegions, alignedSize);
      if (freeIdx < 0) continue; // no room — skip this file

      var (freeOff, freeLen) = freeRegions[freeIdx];
      var target = freeOff;

      // Move all fragments to the free region in order.
      foreach (var ext in extents) {
        moves.Add(new ClusterMove(ext.Offset, target, ext.Length, fileName));
        target = AlignUp(target + ext.Length, clusterSize);
      }

      // Update free list: consume the used portion.
      var consumed = AlignUp(totalSize, clusterSize);
      if (consumed >= freeLen) {
        freeRegions.RemoveAt(freeIdx);
      } else {
        freeRegions[freeIdx] = (freeOff + consumed, freeLen - consumed);
      }

      // The old positions become free (add them back so subsequent files can use them).
      foreach (var ext in extents)
        freeRegions.Add((ext.Offset, AlignUp(ext.Length, clusterSize)));
    }

    return ResolveDependencies(moves, freeRegions, clusterSize);
  }

  // ── Performance profile: full zone-based rearrangement ─────────────────

  private static IReadOnlyList<ClusterMove> PlanPerformance(
    Dictionary<string, List<DefragBlockInfo>> byFile,
    Dictionary<string, long> fileSizes,
    long dataOrigin, long imageSize, int clusterSize,
    List<(long Offset, long Length)> freeRegions,
    IReadOnlyList<(long Start, long End)> forbidden,
    DefragMode mode) {

    // Step 1: Classify files into zones.
    var classified = ClassifyFiles(byFile, fileSizes);

    // Step 2: Determine zone order based on mode.
    // Within each zone, order by CURRENT on-disk offset rather than size.
    // Zone classification picks the region (front vs back) — but within a zone
    // we want files that are already near their target to stay put. Sorting by
    // size triggered unnecessary swaps (e.g. a large file at the end of the
    // image and a small file at the start, both Hot, would get swapped just to
    // put the bigger one first). Offset-sort makes the planner emit O(moved
    // files) instead of O(zone reorderings).
    long FileOffset(string n) => byFile[n][0].Offset;
    List<(string FileName, long Size, DefragBlockClass Zone)> ordered;
    if (mode == DefragMode.ConsolidateAtEnd) {
      // Frozen at front (low offsets = leading free), Hot at end.
      ordered = [
        .. classified.Where(f => f.Zone == DefragBlockClass.Frozen).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Cold).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Normal).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Hot).OrderBy(f => FileOffset(f.FileName)),
      ];
    } else {
      // ConsolidateAtStart (default): Hot at front, Frozen at end.
      ordered = [
        .. classified.Where(f => f.Zone == DefragBlockClass.Hot).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Normal).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Cold).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Frozen).OrderBy(f => FileOffset(f.FileName)),
      ];
    }

    // Step 3: Assign target offsets. For each file find the next slot of
    // sufficient size that doesn't overlap any forbidden (metadata/bad) region.
    // Cluster boundaries are measured RELATIVE to dataOrigin (CP/M et al. have
    // a data-region origin that isn't a multiple of clusterSize from 0).
    var moves = new List<ClusterMove>();

    if (mode == DefragMode.ConsolidateAtEnd) {
      // Place files in reverse so the LAST file in the ordered list lands at
      // the highest valid offset.
      var ceiling = AlignDownFrom(imageSize, dataOrigin, clusterSize);
      for (var i = ordered.Count - 1; i >= 0; i--) {
        var (fileName, totalSize, _) = ordered[i];
        var alignedSize = AlignUp(totalSize, clusterSize);
        var target = FindPrevSlot(ceiling, alignedSize, dataOrigin, clusterSize, forbidden);
        if (target < 0) break; // no room — leave remaining files where they are
        EmitFileMoves(moves, byFile[fileName], target, totalSize, fileName, clusterSize);
        ceiling = target;
      }
    } else {
      var cursor = dataOrigin;
      foreach (var (fileName, totalSize, _) in ordered) {
        var alignedSize = AlignUp(totalSize, clusterSize);
        var target = FindNextSlot(cursor, alignedSize, imageSize, dataOrigin, clusterSize, forbidden);
        if (target < 0) break; // no room
        EmitFileMoves(moves, byFile[fileName], target, totalSize, fileName, clusterSize);
        cursor = target + alignedSize;
      }
    }

    return ResolveDependencies(moves, freeRegions, clusterSize);
  }

  /// <summary>
  /// Emit moves for one file at <paramref name="targetStart"/>, walking its
  /// extents in order. Skips no-op moves (extent already at the right place).
  /// </summary>
  private static void EmitFileMoves(
    List<ClusterMove> moves,
    List<DefragBlockInfo> srcExtents,
    long targetStart,
    long totalSize,
    string fileName,
    int clusterSize) {
    var alreadyCorrect = srcExtents.Count == 1 && srcExtents[0].Offset == targetStart
                         && srcExtents[0].Length == totalSize;
    if (alreadyCorrect) return;
    var target = targetStart;
    foreach (var ext in srcExtents) {
      if (ext.Offset != target || ext.Length != totalSize)
        moves.Add(new ClusterMove(ext.Offset, target, ext.Length, fileName));
      target = AlignUp(target + ext.Length, clusterSize);
    }
  }

  /// <summary>
  /// Finds the smallest cluster-aligned offset >= <paramref name="startFrom"/>
  /// where a <paramref name="length"/>-byte range fits without overlapping any
  /// forbidden interval and stays within [<paramref name="dataOrigin"/>, <paramref name="imageSize"/>).
  /// Cluster boundaries are at <paramref name="dataOrigin"/> + k * clusterSize.
  /// Returns -1 if no such slot exists.
  /// </summary>
  private static long FindNextSlot(long startFrom, long length, long imageSize,
                                    long dataOrigin, int clusterSize,
                                    IReadOnlyList<(long Start, long End)> forbidden) {
    var candidate = AlignUpFrom(Math.Max(startFrom, dataOrigin), dataOrigin, clusterSize);
    while (candidate + length <= imageSize) {
      long? bumpTo = null;
      foreach (var (fStart, fEnd) in forbidden) {
        if (fEnd <= candidate) continue;            // forbidden entirely before us — skip
        if (fStart >= candidate + length) break;    // forbidden entirely after — done
        bumpTo = fEnd;                              // overlap — must jump past this region
        break;
      }
      if (bumpTo == null) return candidate;
      candidate = AlignUpFrom(bumpTo.Value, dataOrigin, clusterSize);
    }
    return -1;
  }

  /// <summary>
  /// Finds the largest cluster-aligned offset where a <paramref name="length"/>-byte
  /// range ends at or before <paramref name="endAt"/>, doesn't overlap any
  /// forbidden interval, and stays at or above <paramref name="minStart"/>.
  /// Cluster boundaries are at <paramref name="minStart"/> + k * clusterSize.
  /// Returns -1 if no such slot exists.
  /// </summary>
  private static long FindPrevSlot(long endAt, long length, long minStart, int clusterSize,
                                    IReadOnlyList<(long Start, long End)> forbidden) {
    var candidate = AlignDownFrom(endAt - length, minStart, clusterSize);
    while (candidate >= minStart) {
      long? dropTo = null;
      // Walk forbidden in reverse to find the first one that overlaps [candidate, candidate+length).
      for (var i = forbidden.Count - 1; i >= 0; i--) {
        var (fStart, fEnd) = forbidden[i];
        if (fStart >= candidate + length) continue; // forbidden after us — skip
        if (fEnd <= candidate) break;               // forbidden before us — done
        dropTo = fStart;                            // overlap — must end below this region
        break;
      }
      if (dropTo == null) return candidate;
      candidate = AlignDownFrom(dropTo.Value - length, minStart, clusterSize);
    }
    return -1;
  }

  /// <summary>Align <paramref name="value"/> up to the next cluster boundary on the grid <c>baseOffset + k*alignment</c>.</summary>
  private static long AlignUpFrom(long value, long baseOffset, long alignment) {
    if (alignment <= 1) return value;
    if (value <= baseOffset) return baseOffset;
    var rel = value - baseOffset;
    return baseOffset + (rel + alignment - 1) / alignment * alignment;
  }

  /// <summary>Align <paramref name="value"/> down to the previous cluster boundary on the grid <c>baseOffset + k*alignment</c>.</summary>
  private static long AlignDownFrom(long value, long baseOffset, long alignment) {
    if (alignment <= 1) return value;
    if (value <= baseOffset) return baseOffset;
    var rel = value - baseOffset;
    return baseOffset + rel / alignment * alignment;
  }

  /// <summary>
  /// Sort + merge overlapping/adjacent half-open intervals. Linear in input size.
  /// </summary>
  private static List<(long Start, long End)> MergeIntervals(List<(long Start, long End)> raw) {
    if (raw.Count <= 1) return raw;
    raw.Sort((a, b) => a.Start.CompareTo(b.Start));
    var merged = new List<(long Start, long End)>(raw.Count);
    var cur = raw[0];
    for (var i = 1; i < raw.Count; i++) {
      var (s, e) = raw[i];
      if (s <= cur.End) {
        if (e > cur.End) cur = (cur.Start, e);
      } else {
        merged.Add(cur);
        cur = (s, e);
      }
    }
    merged.Add(cur);
    return merged;
  }

  // ── Interleaved placement ───────────────────────────────────────────────

  /// <summary>
  /// Plans moves for interleaved placement with stride S. Files are assigned
  /// to "lanes" 0..S-1 in round-robin order (largest-first within each zone).
  /// File i (in lane L) has its Kth block placed at:
  ///   dataOrigin + (baseSlot + K*S) * clusterSize
  /// where baseSlot is the cumulative lane offset for that file within its lane.
  ///
  /// <para>Algorithm:
  /// 1. Classify and order files (same as Performance profile).
  /// 2. Assign files round-robin to lanes 0..S-1.
  /// 3. Per lane, track a cursor (in cluster units) that advances by 1 for each
  ///    block of each file in that lane. The actual cluster index for lane L,
  ///    intra-lane position P is: L + P*S.
  /// 4. Verify total fits in available data region.
  /// 5. Emit per-block moves from each file's source extents to the scattered
  ///    target clusters.</para>
  /// </summary>
  private static IReadOnlyList<ClusterMove> PlanInterleaved(
    Dictionary<string, List<DefragBlockInfo>> byFile,
    Dictionary<string, long> fileSizes,
    long dataOrigin, long imageSize, int clusterSize,
    List<(long Offset, long Length)> freeRegions,
    DefragMode mode,
    int stride) {

    // Step 1: Classify files into zones (same as Performance).
    var classified = ClassifyFiles(byFile, fileSizes);

    // Step 2: Determine zone order. Within each zone, sort by current offset
    // (not by size) to minimise moves — same rationale as PlanPerformance.
    long FileOffset(string n) => byFile[n][0].Offset;
    List<(string FileName, long Size, DefragBlockClass Zone)> ordered;
    if (mode == DefragMode.ConsolidateAtEnd) {
      ordered = [
        .. classified.Where(f => f.Zone == DefragBlockClass.Frozen).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Cold).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Normal).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Hot).OrderBy(f => FileOffset(f.FileName)),
      ];
    } else {
      ordered = [
        .. classified.Where(f => f.Zone == DefragBlockClass.Hot).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Normal).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Cold).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Frozen).OrderBy(f => FileOffset(f.FileName)),
      ];
    }

    // Step 3: Assign files round-robin to lanes 0..stride-1.
    // Per-lane cursor tracks how many blocks have been placed in that lane.
    var laneCursors = new long[stride]; // intra-lane block index per lane
    var dataOriginCluster = AlignUp(dataOrigin, clusterSize);
    var availableClusters = (imageSize - dataOriginCluster) / clusterSize;

    // For ConsolidateAtEnd, shift every lane's base toward the tail so the
    // entire interleaved layout lands near imageSize instead of dataOrigin.
    // We compute the longest per-lane usage and offset all lanes uniformly.
    var laneBlockCounts = new long[stride];
    {
      var probeLane = 0;
      foreach (var (_, size, _) in ordered) {
        var blocks = (size + clusterSize - 1) / clusterSize;
        if (blocks > 0) {
          laneBlockCounts[probeLane % stride] += blocks;
          probeLane++;
        }
      }
    }
    var maxLaneBlocks = 0L;
    for (var i = 0; i < stride; i++) if (laneBlockCounts[i] > maxLaneBlocks) maxLaneBlocks = laneBlockCounts[i];
    var endShift = mode == DefragMode.ConsolidateAtEnd
      ? Math.Max(0, availableClusters - maxLaneBlocks * stride) / stride
      : 0;

    // Pre-compute the target cluster indices for each file. Each file is
    // assigned to a lane, and its blocks go at lane + (cursor + endShift)*stride.
    var fileTargets = new List<(string FileName, long[] TargetOffsets)>();
    var laneIdx = 0;
    foreach (var (fileName, totalSize, _) in ordered) {
      var blockCount = (totalSize + clusterSize - 1) / clusterSize;
      if (blockCount == 0) {
        fileTargets.Add((fileName, []));
        continue;
      }

      var lane = laneIdx % stride;
      laneIdx++;

      var targets = new long[blockCount];
      for (var k = 0; k < blockCount; k++) {
        var clusterIndex = lane + (endShift + laneCursors[lane] + k) * stride;
        if (clusterIndex >= availableClusters)
          throw new InvalidOperationException(
            $"Interleaved layout with stride {stride} requires {clusterIndex + 1} clusters " +
            $"but only {availableClusters} are available. Reduce the stride or free more space.");
        targets[k] = dataOriginCluster + clusterIndex * clusterSize;
      }
      laneCursors[lane] += blockCount;
      fileTargets.Add((fileName, targets));
    }

    // Step 4: Emit per-block moves from source extents to target offsets.
    var moves = new List<ClusterMove>();
    foreach (var (fileName, targetOffsets) in fileTargets) {
      if (targetOffsets.Length == 0) continue;
      var srcExtents = byFile[fileName];

      // Flatten source extents into per-block source offsets.
      var srcOffsets = new List<long>();
      foreach (var ext in srcExtents) {
        var blocks = (ext.Length + clusterSize - 1) / clusterSize;
        for (var b = 0; b < blocks; b++)
          srcOffsets.Add(ext.Offset + b * clusterSize);
      }

      // Emit one move per block (src → target). Skip blocks that are already
      // in the right place.
      var count = Math.Min(srcOffsets.Count, targetOffsets.Length);
      for (var k = 0; k < count; k++) {
        if (srcOffsets[k] == targetOffsets[k]) continue;
        moves.Add(new ClusterMove(srcOffsets[k], targetOffsets[k], clusterSize, fileName));
      }
    }

    return ResolveDependencies(moves, freeRegions, clusterSize);
  }

  // ── CarveHole: reserve a contiguous free region ────────────────────────

  /// <summary>
  /// Plans moves to carve a contiguous free hole of <paramref name="holeSize"/>
  /// bytes at <paramref name="holeAt"/>. When <paramref name="holeAt"/> is -1
  /// the hole is placed immediately after the last live extent (auto mode).
  /// Any live extents overlapping [holeStart, holeStart+holeSize) are relocated
  /// to free regions outside the hole.
  /// </summary>
  private static IReadOnlyList<ClusterMove> PlanCarveHole(
    Dictionary<string, List<DefragBlockInfo>> byFile,
    Dictionary<string, long> fileSizes,
    List<DefragBlockInfo> fileExtents,
    long dataOrigin, long imageSize, int clusterSize,
    List<(long Offset, long Length)> freeRegions,
    long holeSize, long holeAt) {

    if (holeSize <= 0)
      throw new ArgumentException("HoleSize must be positive for CarveHole.");

    var alignedHoleSize = AlignUp(holeSize, clusterSize);

    // Determine hole start.
    long holeStart;
    if (holeAt < 0) {
      // Auto: place hole immediately after the last live extent.
      var lastEnd = fileExtents.Max(e => e.Offset + e.Length);
      holeStart = AlignUp(lastEnd, clusterSize);
    } else {
      holeStart = AlignUp(holeAt, clusterSize);
    }

    var holeEnd = holeStart + alignedHoleSize;

    // Validate the hole fits within the image.
    if (holeEnd > imageSize)
      throw new InvalidOperationException(
        $"Carved hole [{holeStart}..{holeEnd}) exceeds image size {imageSize}.");

    // Find all live extents that overlap the hole region.
    var overlapping = new List<DefragBlockInfo>();
    foreach (var ext in fileExtents) {
      if (Overlaps(ext.Offset, ext.Length, holeStart, alignedHoleSize))
        overlapping.Add(ext);
    }

    if (overlapping.Count == 0)
      return []; // hole region is already free — nothing to move

    // Build a list of free regions that are outside the hole. Start from the
    // existing free regions and exclude any portion that falls inside the hole.
    var safeFree = new List<(long Offset, long Length)>();
    foreach (var (off, len) in freeRegions) {
      var fEnd = off + len;
      // Clip to exclude the hole region.
      if (fEnd <= holeStart || off >= holeEnd) {
        // Entirely outside — keep as is.
        safeFree.Add((off, len));
      } else {
        // Partially overlapping — keep the portions outside.
        if (off < holeStart)
          safeFree.Add((off, holeStart - off));
        if (fEnd > holeEnd)
          safeFree.Add((holeEnd, fEnd - holeEnd));
      }
    }

    // Also consider the space freed by extents that will be moved out of the
    // hole but only the portions outside the hole.
    // (Their old positions become free after the move, but portions inside the
    // hole are part of the hole itself.)

    var moves = new List<ClusterMove>();

    foreach (var ext in overlapping) {
      var needed = AlignUp(ext.Length, clusterSize);
      var freeIdx = FindFreeRegion(safeFree, needed);
      if (freeIdx < 0)
        throw new InvalidOperationException(
          $"Cannot carve hole: no free region of {needed} bytes found outside [{holeStart}..{holeEnd}) " +
          $"to relocate extent '{ext.FileName}' at offset {ext.Offset} (length {ext.Length}).");

      var (freeOff, freeLen) = safeFree[freeIdx];
      moves.Add(new ClusterMove(ext.Offset, freeOff, ext.Length, ext.FileName ?? "<unknown>"));

      // Consume the used portion of the free region.
      if (needed >= freeLen) {
        safeFree.RemoveAt(freeIdx);
      } else {
        safeFree[freeIdx] = (freeOff + needed, freeLen - needed);
      }

      // The old position becomes free (outside the hole portion).
      var extEnd = ext.Offset + needed;
      if (ext.Offset < holeStart) {
        safeFree.Add((ext.Offset, Math.Min(holeStart, extEnd) - ext.Offset));
      }
      if (extEnd > holeEnd) {
        safeFree.Add((Math.Max(holeEnd, ext.Offset), extEnd - Math.Max(holeEnd, ext.Offset)));
      }
    }

    return ResolveDependencies(moves, freeRegions, clusterSize);
  }

  // ── Zone classification ────────────────────────────────────────────────

  private static List<(string FileName, long Size, DefragBlockClass Zone)> ClassifyFiles(
    Dictionary<string, List<DefragBlockInfo>> byFile,
    Dictionary<string, long> fileSizes) {
    var result = new List<(string, long, DefragBlockClass)>(byFile.Count);

    // Try mtime-based classification first.
    var mtimeAvailable = false;
    var mtimes = new Dictionary<string, DefragBlockClass>(StringComparer.OrdinalIgnoreCase);

    // If Classification is set on any extent, use that.
    foreach (var (name, extents) in byFile) {
      var cls = extents.FirstOrDefault(e => e.Classification.HasValue)?.Classification;
      if (cls.HasValue) {
        mtimes[name] = cls.Value;
        mtimeAvailable = true;
      }
    }

    if (!mtimeAvailable) {
      // Fall back to listing-order quartile classification.
      var names = byFile.Keys.ToList();
      for (var i = 0; i < names.Count; i++) {
        var q = names.Count <= 1 ? 0 : i * 4 / names.Count;
        mtimes[names[i]] = q switch {
          0 => DefragBlockClass.Hot,
          1 => DefragBlockClass.Normal,
          2 => DefragBlockClass.Cold,
          _ => DefragBlockClass.Frozen,
        };
      }
    }

    foreach (var (name, size) in fileSizes) {
      var zone = mtimes.GetValueOrDefault(name, DefragBlockClass.Normal);
      result.Add((name, size, zone));
    }

    return result;
  }

  // ── Dependency resolution with cycle breaking ──────────────────────────

  /// <summary>
  /// Resolves move dependencies using a simple topological sort. When move A's
  /// target overlaps move B's source (and vice versa), the cycle is broken by
  /// staging one of the extents in a free region.
  /// </summary>
  private static IReadOnlyList<ClusterMove> ResolveDependencies(
    List<ClusterMove> rawMoves,
    List<(long Offset, long Length)> freeRegions,
    int clusterSize) {

    // Remove no-ops (src == dst).
    rawMoves.RemoveAll(m => m.SrcOffset == m.DstOffset);
    if (rawMoves.Count == 0) return [];

    // Simple approach: detect overlapping pairs and break cycles via staging.
    var result = new List<ClusterMove>(rawMoves.Count + rawMoves.Count / 4);
    var pending = new List<ClusterMove>(rawMoves);
    var resolved = new HashSet<int>();
    var maxIter = pending.Count * pending.Count + 1;
    var iter = 0;

    while (pending.Count > 0 && iter++ < maxIter) {
      var progress = false;
      for (var i = pending.Count - 1; i >= 0; i--) {
        var move = pending[i];
        // Check if any other pending move's source overlaps our target.
        var blocked = false;
        for (var j = 0; j < pending.Count; j++) {
          if (i == j) continue;
          var other = pending[j];
          if (Overlaps(move.DstOffset, move.Length, other.SrcOffset, other.Length)) {
            blocked = true;
            break;
          }
        }

        if (!blocked) {
          result.Add(move);
          pending.RemoveAt(i);
          progress = true;
        }
      }

      if (!progress && pending.Count > 0) {
        // Cycle detected — break it by staging the first pending move's source
        // in a free region.
        var stuck = pending[0];
        var stageIdx = FindFreeRegion(freeRegions, AlignUp(stuck.Length, clusterSize));
        if (stageIdx < 0) {
          // No free region available — just emit the moves in order and hope
          // the caller's byte-level move engine handles overlap safely.
          result.AddRange(pending);
          pending.Clear();
          break;
        }

        var (stageOff, stageLen) = freeRegions[stageIdx];
        var consumed = AlignUp(stuck.Length, clusterSize);

        // Step 1: move source → staging
        result.Add(new ClusterMove(stuck.SrcOffset, stageOff, stuck.Length, stuck.FileName));
        // Step 2: move staging → target (will be unblocked after other moves)
        pending[0] = new ClusterMove(stageOff, stuck.DstOffset, stuck.Length, stuck.FileName);

        // Update free list: consume staging region, free source region.
        if (consumed >= stageLen)
          freeRegions.RemoveAt(stageIdx);
        else
          freeRegions[stageIdx] = (stageOff + consumed, stageLen - consumed);
        freeRegions.Add((stuck.SrcOffset, AlignUp(stuck.Length, clusterSize)));
      }
    }

    return result;
  }

  // ── Metadata zone placement ────────────────────────────────────────────

  /// <summary>
  /// Determines whether a file name looks like a directory entry. Uses
  /// heuristic name patterns: names containing path separators, or names
  /// that are well-known directory identifiers (e.g. "root dir", ".").
  /// </summary>
  private static bool IsDirectoryExtent(string name) {
    // Trailing "/" is the convention extent maps use to mark directory data
    // (e.g. FAT subdirectory cluster chains). Unambiguous — a file named
    // "DIR1/MAKEFILE" inside a subdir has no trailing slash, so it stays a file.
    if (name.EndsWith('/')) return true;
    // Common directory-like sentinel names some extent maps still emit.
    var lower = name.ToLowerInvariant();
    if (lower.Contains("root dir") || lower.Contains("directory")
        || lower == "." || lower == "..")
      return true;
    return false;
  }

  /// <summary>
  /// Plans moves with metadata zone placement. Separates the extent list into
  /// metadata-reserved, directory, and file-data groups, then arranges them
  /// according to the chosen <see cref="MetadataZone"/>. Honors
  /// <paramref name="mode"/> and the supplied <paramref name="forbidden"/> regions
  /// so files never overwrite reserved metadata.
  /// </summary>
  private static IReadOnlyList<ClusterMove> PlanMetadataZone(
    List<DefragBlockInfo> metadataExtents,
    Dictionary<string, List<DefragBlockInfo>> dirByFile,
    Dictionary<string, long> dirSizes,
    Dictionary<string, List<DefragBlockInfo>> dataByFile,
    Dictionary<string, long> dataSizes,
    long dataOrigin, long imageSize, int clusterSize,
    List<(long Offset, long Length)> freeRegions,
    IReadOnlyList<(long Start, long End)> forbidden,
    DefragMode mode,
    MetadataZone zone) {

    // Sort each group by CURRENT offset (not by size) so files near their
    // target position stay put. Same minimum-moves rationale as PlanPerformance.
    var orderedDirs = dirByFile.Keys.OrderBy(n => dirByFile[n][0].Offset).ToList();
    var orderedData = dataByFile.Keys.OrderBy(n => dataByFile[n][0].Offset).ToList();

    // BeforeContent gets its own grouping logic (each dir followed by its
    // children); the other zones share the same "build a flat layoutOrder"
    // pipeline below.
    if (zone == MetadataZone.BeforeContent)
      return PlanBeforeContent(metadataExtents, dirByFile, dirSizes, dataByFile, dataSizes,
        dataOrigin, imageSize, clusterSize, freeRegions, mode);

    var layoutOrder = new List<(string Name, List<DefragBlockInfo> Extents, long Size)>();
    switch (zone) {
      case MetadataZone.Front:
        // [dirs][data sorted by size desc]
        foreach (var n in orderedDirs) layoutOrder.Add((n, dirByFile[n], dirSizes[n]));
        foreach (var n in orderedData) layoutOrder.Add((n, dataByFile[n], dataSizes[n]));
        break;

      case MetadataZone.Back:
        // [data sorted by size desc][dirs]
        foreach (var n in orderedData) layoutOrder.Add((n, dataByFile[n], dataSizes[n]));
        foreach (var n in orderedDirs) layoutOrder.Add((n, dirByFile[n], dirSizes[n]));
        break;

      case MetadataZone.Middle: {
        // [data-first-half][dirs][data-second-half]
        var halfIdx = orderedData.Count / 2;
        for (var i = 0; i < halfIdx; i++)
          layoutOrder.Add((orderedData[i], dataByFile[orderedData[i]], dataSizes[orderedData[i]]));
        foreach (var n in orderedDirs) layoutOrder.Add((n, dirByFile[n], dirSizes[n]));
        for (var i = halfIdx; i < orderedData.Count; i++)
          layoutOrder.Add((orderedData[i], dataByFile[orderedData[i]], dataSizes[orderedData[i]]));
        break;
      }
    }

    // Pack files into slots honoring mode + forbidden regions. Same loop as
    // PlanPerformance: cursor walks dataOrigin → imageSize for ConsolidateAtStart
    // / FillHolesLazy, or imageSize → dataOrigin for ConsolidateAtEnd.
    var moves = new List<ClusterMove>();
    if (mode == DefragMode.ConsolidateAtEnd) {
      var ceiling = AlignDownFrom(imageSize, dataOrigin, clusterSize);
      for (var i = layoutOrder.Count - 1; i >= 0; i--) {
        var (fileName, srcExtents, totalSize) = layoutOrder[i];
        var alignedSize = AlignUp(totalSize, clusterSize);
        var target = FindPrevSlot(ceiling, alignedSize, dataOrigin, clusterSize, forbidden);
        if (target < 0) break;
        EmitFileMoves(moves, srcExtents, target, totalSize, fileName, clusterSize);
        ceiling = target;
      }
    } else {
      var cursor = dataOrigin;
      foreach (var (fileName, srcExtents, totalSize) in layoutOrder) {
        var alignedSize = AlignUp(totalSize, clusterSize);
        var target = FindNextSlot(cursor, alignedSize, imageSize, dataOrigin, clusterSize, forbidden);
        if (target < 0) break;
        EmitFileMoves(moves, srcExtents, target, totalSize, fileName, clusterSize);
        cursor = target + alignedSize;
      }
    }

    return ResolveDependencies(moves, freeRegions, clusterSize);
  }

  /// <summary>
  /// BeforeContent placement: each directory is placed immediately before
  /// its children's data, then groups are laid out contiguously. Directories
  /// without children are placed after all groups. Files without a parent
  /// directory in the extent map are placed at the end.
  /// <para>When <paramref name="mode"/> is <see cref="DefragMode.ConsolidateAtEnd"/>,
  /// the entire packed sequence is shifted so it ends flush against
  /// <paramref name="imageSize"/> rather than starting at
  /// <paramref name="dataOrigin"/>. The relative order within the sequence
  /// (dir → its children → next dir → …) is preserved.</para>
  /// </summary>
  private static IReadOnlyList<ClusterMove> PlanBeforeContent(
    List<DefragBlockInfo> metadataExtents,
    Dictionary<string, List<DefragBlockInfo>> dirByFile,
    Dictionary<string, long> dirSizes,
    Dictionary<string, List<DefragBlockInfo>> dataByFile,
    Dictionary<string, long> dataSizes,
    long dataOrigin, long imageSize, int clusterSize,
    List<(long Offset, long Length)> freeRegions,
    DefragMode mode) {

    // Build parent→children mapping. A file's parent is determined by
    // stripping the last path component. For flat names (no "/"), the
    // parent is the root directory.
    var childrenOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    var assignedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var dirName in dirByFile.Keys)
      childrenOf[dirName] = [];

    foreach (var fileName in dataByFile.Keys) {
      var lastSlash = fileName.LastIndexOf('/');
      var parentDir = lastSlash > 0 ? fileName[..lastSlash] : "";
      // Try to find the parent directory in dirByFile.
      foreach (var dirName in dirByFile.Keys) {
        if (dirName.Equals(parentDir, StringComparison.OrdinalIgnoreCase)
            || (parentDir == "" && IsDirectoryExtent(dirName))) {
          childrenOf[dirName].Add(fileName);
          assignedFiles.Add(fileName);
          break;
        }
      }
      // If no parent directory match, leave unassigned (will be appended at end).
    }

    var cursor = AlignUp(dataOrigin, clusterSize);
    var moves = new List<ClusterMove>();

    // Lay out each directory followed by its children.
    foreach (var dirName in dirByFile.Keys.OrderByDescending(n => dirSizes[n])) {
      var dirExtents = dirByFile[dirName];
      var dirSize = dirSizes[dirName];

      // Place the directory extents.
      var target = cursor;
      foreach (var ext in dirExtents) {
        if (ext.Offset != target)
          moves.Add(new ClusterMove(ext.Offset, target, ext.Length, dirName));
        target = AlignUp(target + ext.Length, clusterSize);
      }
      cursor = AlignUp(cursor + dirSize, clusterSize);

      // Place the children immediately after.
      foreach (var childName in childrenOf[dirName].OrderByDescending(n => dataSizes[n])) {
        var childExtents = dataByFile[childName];
        var childSize = dataSizes[childName];
        target = cursor;
        foreach (var ext in childExtents) {
          if (ext.Offset != target)
            moves.Add(new ClusterMove(ext.Offset, target, ext.Length, childName));
          target = AlignUp(target + ext.Length, clusterSize);
        }
        cursor = AlignUp(cursor + childSize, clusterSize);
      }
    }

    // Place unassigned files (those without a parent directory in the extent map).
    foreach (var fileName in dataByFile.Keys.OrderByDescending(n => dataSizes[n])) {
      if (assignedFiles.Contains(fileName)) continue;
      var extents = dataByFile[fileName];
      var size = dataSizes[fileName];
      var target = cursor;
      foreach (var ext in extents) {
        if (ext.Offset != target)
          moves.Add(new ClusterMove(ext.Offset, target, ext.Length, fileName));
        target = AlignUp(target + ext.Length, clusterSize);
      }
      cursor = AlignUp(cursor + size, clusterSize);
    }

    // ConsolidateAtEnd: the layout above was built starting at `dataOrigin`.
    // Rebuild the move list with the destination cursor anchored at the END
    // of the image instead — relative order (dir → its children → next dir)
    // is preserved; only the base address shifts. Without this step,
    // BeforeContent silently ignored ConsolidateAtEnd and always packed
    // toward the start of the image.
    if (mode == DefragMode.ConsolidateAtEnd) {
      var packedEnd = cursor;
      var ceiling = AlignDownFrom(imageSize, dataOrigin, clusterSize);
      var packedStart = AlignUp(dataOrigin, clusterSize);
      var shift = ceiling - packedEnd;
      if (shift > 0) {
        moves.Clear();
        var emitCursor = packedStart + shift;
        foreach (var dirName in dirByFile.Keys.OrderByDescending(n => dirSizes[n])) {
          foreach (var ext in dirByFile[dirName]) {
            if (ext.Offset != emitCursor)
              moves.Add(new ClusterMove(ext.Offset, emitCursor, ext.Length, dirName));
            emitCursor = AlignUp(emitCursor + ext.Length, clusterSize);
          }
          foreach (var childName in childrenOf[dirName].OrderByDescending(n => dataSizes[n])) {
            foreach (var ext in dataByFile[childName]) {
              if (ext.Offset != emitCursor)
                moves.Add(new ClusterMove(ext.Offset, emitCursor, ext.Length, childName));
              emitCursor = AlignUp(emitCursor + ext.Length, clusterSize);
            }
          }
        }
        foreach (var fileName in dataByFile.Keys.OrderByDescending(n => dataSizes[n])) {
          if (assignedFiles.Contains(fileName)) continue;
          foreach (var ext in dataByFile[fileName]) {
            if (ext.Offset != emitCursor)
              moves.Add(new ClusterMove(ext.Offset, emitCursor, ext.Length, fileName));
            emitCursor = AlignUp(emitCursor + ext.Length, clusterSize);
          }
        }
      }
    }

    return ResolveDependencies(moves, freeRegions, clusterSize);
  }

  // ── Utilities ──────────────────────────────────────────────────────────

  private static bool Overlaps(long aOff, long aLen, long bOff, long bLen)
    => aOff < bOff + bLen && bOff < aOff + aLen;

  private static int FindFreeRegion(List<(long Offset, long Length)> free, long needed) {
    for (var i = 0; i < free.Count; i++)
      if (free[i].Length >= needed)
        return i;
    return -1;
  }

  private static long AlignUp(long value, long alignment)
    => alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
}
