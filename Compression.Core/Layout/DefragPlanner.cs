#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Layout;

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
  /// <param name="layoutTemplate">Optional fine-grained layout template. When
  /// non-null, the template drives zone assignment via
  /// <see cref="LayoutTemplateResolver"/>; <paramref name="profile"/> /
  /// <paramref name="metadataZone"/> are bypassed. When null (default), the
  /// classic mode/profile/metadata-zone pipeline is used.</param>
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
    MetadataZone metadataZone = MetadataZone.Unchanged,
    LayoutTemplate? layoutTemplate = null,
    IReadOnlySet<string>? movableMetadata = null)
    => Validate(PlanCore(extents, dataOrigin, imageSize, clusterSize, profile, mode,
      interleaveStride, holeSize, holeAt, metadataZone, layoutTemplate, movableMetadata), imageSize);

  /// <summary>
  /// Checks that a plan can be executed without destroying data: every move has
  /// to land inside the image, and no two destinations may overlap. A plan that
  /// breaks either rule is refused rather than run — the caller falls back to a
  /// rebuild. Silently executing one wrote files on top of each other and left
  /// them the right length with the wrong bytes.
  /// </summary>
  private static IReadOnlyList<ClusterMove> Validate(IReadOnlyList<ClusterMove> moves, long imageSize) {
    if (moves.Count == 0) return moves;

    var occupied = new List<(long Start, long End)>();
    foreach (var move in moves) {
      if (move.DstOffset < 0 || move.Length <= 0 || move.DstOffset + move.Length > imageSize)
        throw new InvalidOperationException(
          $"Defragmentation plan places {move.Length:N0} bytes of '{move.FileName}' at " +
          $"{move.DstOffset:N0}, which does not fit inside the {imageSize:N0}-byte image.");

      var start = move.DstOffset;
      var end = move.DstOffset + move.Length;
      foreach (var (otherStart, otherEnd) in occupied)
        if (start < otherEnd && otherStart < end)
          throw new InvalidOperationException(
            $"Defragmentation plan writes '{move.FileName}' to {start:N0}..{end:N0}, " +
            "which another move already claims.");
      occupied.Add((start, end));
    }
    return moves;
  }

  private static IReadOnlyList<ClusterMove> PlanCore(
    IReadOnlyList<DefragBlockInfo> extents,
    long dataOrigin,
    long imageSize,
    int clusterSize,
    LayoutProfile profile,
    DefragMode mode,
    int interleaveStride,
    long holeSize,
    long holeAt,
    MetadataZone metadataZone,
    LayoutTemplate? layoutTemplate,
    IReadOnlySet<string>? movableMetadata) {
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

    // A metadata region the filesystem can repoint is an owner like any other:
    // it can be laid out where the requested zone wants it instead of pinning
    // the layout around wherever mkfs happened to put it. Only regions the
    // format lists are eligible, and only when they arrive as a single run —
    // relinking a scattered structure is a different operation from repointing
    // a contiguous one, and no format here offers it.
    // Only when the caller has asked for a metadata placement: MetadataZone
    // .Unchanged means what it says — the volume's structures stay where they
    // are, and an ordinary defragmentation does not shift the MFT under the
    // user because it happened to be able to.
    var relocatable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (movableMetadata is { Count: > 0 }
        && (metadataZone != MetadataZone.Unchanged || layoutTemplate is not null)) {
      var runsPerRegion = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (var e in extents)
        if (e.Kind == DefragBlockKind.MetadataReserved && e.FileName is { } n && movableMetadata.Contains(n)) {
          runsPerRegion.TryGetValue(n, out var count);
          runsPerRegion[n] = count + 1;
        }
      foreach (var (name, count) in runsPerRegion)
        if (count == 1) relocatable.Add(name);
    }

    foreach (var e in extents) {
      switch (e.Kind) {
        case DefragBlockKind.Used:
          fileExtents.Add(e);
          break;
        case DefragBlockKind.Free:
          freeRegions.Add((e.Offset, e.Length));
          break;
        case DefragBlockKind.MetadataReserved when e.FileName is { } name && relocatable.Contains(name):
          fileExtents.Add(e);
          break;
        case DefragBlockKind.MetadataReserved:
        case DefragBlockKind.Bad:
          forbiddenRaw.Add((e.Offset, e.Offset + e.Length));
          break;
      }
    }
    var forbidden = MergeIntervals(forbiddenRaw);

    // Whatever no extent claims is free, whether or not the map bothered to say
    // so. Several maps report only what is allocated, and the planner then had
    // nowhere to stage a cycle: a layout that merely exchanges two regions was
    // refused for want of somewhere to put one of them for a moment.
    AddUnclaimedSpace(freeRegions, extents, dataOrigin, imageSize);

    if (fileExtents.Count == 0) return [];

    // Planning is superlinear in the number of extents, and a volume whose map
    // reports a run per block — a few gigabytes at one kilobyte each — pushed
    // that into a planning pass that never returned. Past this many the caller
    // rebuilds instead, which costs one pass over the data.
    if (fileExtents.Count > MaxPlannableExtents)
      throw new InvalidOperationException(
        $"Defragmentation cannot be planned in place: {fileExtents.Count:N0} extents exceed the " +
        $"{MaxPlannableExtents:N0} this planner resolves; rebuild the volume instead.");

    // Group extents by file name.
    var byFile = new Dictionary<string, List<DefragBlockInfo>>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in fileExtents) {
      var key = e.FileName ?? "<unknown>";
      if (!byFile.TryGetValue(key, out var list))
        byFile[key] = list = [];
      list.Add(e);
    }

    // NOTE: extents are intentionally kept in the order the extent map yielded
    // them, which is CHAIN / logical order (the map walks each owner's cluster
    // or block chain run by run). Sorting by physical offset would scramble a
    // fragmented owner's logical sequence, so when the executor relinks the
    // packed run it would carry the owner's content out of order — corrupting a
    // file's bytes and, for a directory, its entry list. Packing in chain order
    // produces a single ascending contiguous run that reads back correctly,
    // which is exactly what "fusing the parts of one owner together" requires.

    // Compute per-file total size.
    var fileSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, exts) in byFile)
      fileSizes[name] = exts.Sum(e => e.Length);

    if (mode == DefragMode.CarveHole)
      return PlanCarveHole(byFile, fileSizes, fileExtents, dataOrigin, imageSize, clusterSize, freeRegions, holeSize, holeAt);

    // Layout-template path takes precedence over the classic metadata-zone /
    // profile pipeline. The template captures (a) per-zone byte ranges, (b)
    // filters that route files to zones, (c) sort keys applied within each
    // zone. Files matching no zone fall through per the template's leftover
    // strategy. The classic mode/profile path is used as a fallback only
    // when the template is null.
    if (layoutTemplate is not null) {
      return PlanFromTemplate(
        byFile, fileSizes, extents,
        dataOrigin, imageSize, clusterSize,
        freeRegions, forbidden, mode, layoutTemplate);
    }

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
        // A relocatable structure belongs with the directories: both are the
        // volume's own bookkeeping, and a request to gather metadata at the
        // front means the MFT and the bitmaps too, not only the folders.
        if (IsDirectoryExtent(name) || relocatable.Contains(name)) {
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

  /// <summary>
  /// Adds every stretch of [<paramref name="dataOrigin" />,
  /// <paramref name="imageSize" />) that no extent claims to
  /// <paramref name="freeRegions" />, so a map that lists only allocated space
  /// still gives the planner room to work in.
  /// </summary>
  private static void AddUnclaimedSpace(
      List<(long Offset, long Length)> freeRegions,
      IReadOnlyList<DefragBlockInfo> extents,
      long dataOrigin,
      long imageSize) {
    if (imageSize <= dataOrigin) return;

    var claimed = new List<(long Start, long End)>();
    foreach (var e in extents) {
      if (e.Kind == DefragBlockKind.Free || e.Length <= 0) continue;
      claimed.Add((e.Offset, e.Offset + e.Length));
    }
    foreach (var (offset, length) in freeRegions)
      if (length > 0) claimed.Add((offset, offset + length));

    var merged = MergeIntervals(claimed);
    var cursor = dataOrigin;
    foreach (var (start, end) in merged) {
      if (end <= cursor) continue;
      if (start > cursor) freeRegions.Add((cursor, start - cursor));
      cursor = Math.Max(cursor, end);
      if (cursor >= imageSize) return;
    }
    if (cursor < imageSize) freeRegions.Add((cursor, imageSize - cursor));
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

      // Move all fragments to the free region in order, on the cluster grid
      // the data region actually uses.
      foreach (var ext in extents) {
        moves.Add(new ClusterMove(ext.Offset, target, ext.Length, fileName));
        target = AlignUpFrom(target + ext.Length, dataOrigin, clusterSize);
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
    // The "middle" bucket holds Normal AND every owner that isn't one of the
    // three temperature extremes — most importantly Directory-classified
    // extents (FAT subdirectory chains, B-tree dir nodes). Without this, those
    // owners were silently dropped from the layout and never moved, so a
    // fragmented directory could never be fused. Treat them like Normal so they
    // get consolidated inline with file data.
    static bool IsMiddle(DefragBlockClass z)
      => z is not (DefragBlockClass.Hot or DefragBlockClass.Cold or DefragBlockClass.Frozen);

    List<(string FileName, long Size, DefragBlockClass Zone)> ordered;
    if (mode == DefragMode.ConsolidateAtEnd) {
      // Frozen at front (low offsets = leading free), Hot at end.
      ordered = [
        .. classified.Where(f => f.Zone == DefragBlockClass.Frozen).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Cold).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => IsMiddle(f.Zone)).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Hot).OrderBy(f => FileOffset(f.FileName)),
      ];
    } else {
      // ConsolidateAtStart (default): Hot at front, Frozen at end.
      ordered = [
        .. classified.Where(f => f.Zone == DefragBlockClass.Hot).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => IsMiddle(f.Zone)).OrderBy(f => FileOffset(f.FileName)),
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
        EmitFileMoves(moves, byFile[fileName], target, totalSize, fileName, clusterSize, dataOrigin);
        ceiling = target;
      }
    } else {
      var cursor = dataOrigin;
      foreach (var (fileName, totalSize, _) in ordered) {
        var alignedSize = AlignUp(totalSize, clusterSize);
        var target = FindNextSlot(cursor, alignedSize, imageSize, dataOrigin, clusterSize, forbidden);
        if (target < 0) break; // no room
        EmitFileMoves(moves, byFile[fileName], target, totalSize, fileName, clusterSize, dataOrigin);
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
    int clusterSize,
    long dataOrigin) {
    var alreadyCorrect = srcExtents.Count == 1 && srcExtents[0].Offset == targetStart
                         && srcExtents[0].Length == totalSize;
    if (alreadyCorrect) return;
    var target = targetStart;
    foreach (var ext in srcExtents) {
      if (ext.Offset != target || ext.Length != totalSize)
        moves.Add(new ClusterMove(ext.Offset, target, ext.Length, fileName));
      // Cluster boundaries sit at dataOrigin + k * clusterSize, and a data
      // region rarely starts on a multiple of the cluster size — a FAT volume's
      // first data byte follows the reserved sectors, the FATs and the root
      // directory. Aligning absolutely put every run after the first off the
      // grid, and the relink then pointed the chain at the wrong cluster.
      target = AlignUpFrom(target + ext.Length, dataOrigin, clusterSize);
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
    // Middle bucket = Normal plus anything that isn't a temperature extreme,
    // so Directory-classified owners are placed (not silently dropped).
    static bool IsMiddle(DefragBlockClass z)
      => z is not (DefragBlockClass.Hot or DefragBlockClass.Cold or DefragBlockClass.Frozen);
    List<(string FileName, long Size, DefragBlockClass Zone)> ordered;
    if (mode == DefragMode.ConsolidateAtEnd) {
      ordered = [
        .. classified.Where(f => f.Zone == DefragBlockClass.Frozen).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Cold).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => IsMiddle(f.Zone)).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => f.Zone == DefragBlockClass.Hot).OrderBy(f => FileOffset(f.FileName)),
      ];
    } else {
      ordered = [
        .. classified.Where(f => f.Zone == DefragBlockClass.Hot).OrderBy(f => FileOffset(f.FileName)),
        .. classified.Where(f => IsMiddle(f.Zone)).OrderBy(f => FileOffset(f.FileName)),
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
  /// <summary>
  /// Moves past which in-place planning is refused in favour of a rebuild.
  /// </summary>
  private const int MaxPlannableMoves = 4096;

  /// <summary>
  /// Extents past which in-place planning is refused in favour of a rebuild.
  /// </summary>
  private const int MaxPlannableExtents = 65536;

  private static IReadOnlyList<ClusterMove> ResolveDependencies(
    List<ClusterMove> rawMoves,
    List<(long Offset, long Length)> freeRegions,
    int clusterSize) {

    // Remove no-ops (src == dst).
    rawMoves.RemoveAll(m => m.SrcOffset == m.DstOffset);
    if (rawMoves.Count == 0) return [];

    // Each move is checked against every other pending move, so the resolution
    // costs the square of the move count per pass. A volume fragmented into
    // tens of thousands of runs turned that into hours of planning with no
    // output; past this many moves a rebuild is both faster and simpler, and
    // the caller falls back to one.
    if (rawMoves.Count > MaxPlannableMoves)
      throw new InvalidOperationException(
        $"Defragmentation cannot be planned in place: {rawMoves.Count:N0} moves exceed the " +
        $"{MaxPlannableMoves:N0} this planner resolves; rebuild the volume instead.");

    // Simple approach: detect overlapping pairs and break cycles via staging.
    var result = new List<ClusterMove>(rawMoves.Count + rawMoves.Count / 4);
    var pending = new List<ClusterMove>(rawMoves);
    var stagedOnce = new HashSet<(string FileName, long DstOffset)>();
    var resolved = new HashSet<int>();
    // Every pass either commits at least one move or stages one, and a move is
    // staged at most once, so twice the move count bounds the passes. The
    // square of it — what this used to allow — multiplied the per-pass cost
    // into a run that never finished.
    var maxIter = 2 * pending.Count + 8;
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
        // A cycle: each remaining move's destination holds another's source.
        // Lift whichever move occupies the first one's destination into a free
        // region, and let it hop from there to its own target afterwards.
        //
        // Two rules keep this honest. The move that is lifted is the occupant,
        // not the blocked move — staging the blocked one changes nothing about
        // why it is blocked. And a move is lifted at most once: without that,
        // a pair ping-ponged between two offsets until the iteration cap and
        // the bytes ended up wherever the last hop left them, while the
        // filesystem's chain pointed at the planned destination instead.
        var blockedMove = pending[0];
        var occupantIndex = -1;
        for (var j = 1; j < pending.Count; ++j)
          if (Overlaps(blockedMove.DstOffset, blockedMove.Length, pending[j].SrcOffset, pending[j].Length)) {
            occupantIndex = j;
            break;
          }
        if (occupantIndex < 0)
          throw new InvalidOperationException(
            "Defragmentation cannot be planned in place: a move is blocked by something that " +
            "does not move, so no ordering of the remaining moves is safe.");

        var stuck = pending[occupantIndex];
        if (!stagedOnce.Add((stuck.FileName, stuck.DstOffset)))
          throw new InvalidOperationException(
            "Defragmentation cannot be planned in place: the moves keep blocking each other " +
            "after staging, so no safe order exists.");

        // The staging region has to be somewhere no pending move is headed;
        // otherwise lifting one file out of the way just puts it in the next
        // one's path, and the cycle survives with an extra hop in it.
        var stageIdx = FindFreeRegionClearOf(freeRegions, AlignUp(stuck.Length, clusterSize), pending, result);
        if (stageIdx < 0)
          // Nowhere to stage the cycle. Executing the remaining moves in any
          // order overwrites data — which is what happened while this emitted
          // them and hoped the byte mover would cope. Every caller falls back
          // to the rebuild path, which reads each file whole first.
          throw new InvalidOperationException(
            $"Defragmentation cannot be planned in place: {pending.Count} move(s) form a cycle " +
            $"and no free region of {AlignUp(stuck.Length, clusterSize):N0} bytes can stage it.");

        var (stageOff, stageLen) = freeRegions[stageIdx];
        var consumed = AlignUp(stuck.Length, clusterSize);

        // Step 1: the occupant moves out of the way, into the staging region.
        result.Add(new ClusterMove(stuck.SrcOffset, stageOff, stuck.Length, stuck.FileName));
        // Step 2: it hops from there to its own target once that is clear.
        pending[occupantIndex] = new ClusterMove(stageOff, stuck.DstOffset, stuck.Length, stuck.FileName);

        // Update free list: consume staging region, free the occupant's source.
        if (consumed >= stageLen)
          freeRegions.RemoveAt(stageIdx);
        else
          freeRegions[stageIdx] = (stageOff + consumed, stageLen - consumed);
        freeRegions.Add((stuck.SrcOffset, AlignUp(stuck.Length, clusterSize)));
      }
    }

    return result;
  }

  /// <summary>
  /// A free region of at least <paramref name="size" /> bytes that no pending
  /// move is headed for, so parking a run there cannot block anything.
  /// </summary>
  /// <summary>
  /// A free region of at least <paramref name="size" /> bytes that no move is
  /// headed for — neither one still waiting nor one already committed. Staging
  /// into a region another move has claimed only relocates the collision:
  /// whichever runs second overwrites the first.
  /// </summary>
  private static int FindFreeRegionClearOf(
      List<(long Offset, long Length)> freeRegions, long size,
      List<ClusterMove> pending, List<ClusterMove> committed) {
    for (var i = 0; i < freeRegions.Count; ++i) {
      var (offset, length) = freeRegions[i];
      if (length < size) continue;
      var clear = true;
      foreach (var move in pending)
        if (Overlaps(offset, size, move.DstOffset, move.Length)) {
          clear = false;
          break;
        }
      if (clear)
        foreach (var move in committed)
          if (Overlaps(offset, size, move.DstOffset, move.Length)) {
            clear = false;
            break;
          }
      if (clear) return i;
    }
    return -1;
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
        EmitFileMoves(moves, srcExtents, target, totalSize, fileName, clusterSize, dataOrigin);
        ceiling = target;
      }
    } else {
      var cursor = dataOrigin;
      foreach (var (fileName, srcExtents, totalSize) in layoutOrder) {
        var alignedSize = AlignUp(totalSize, clusterSize);
        var target = FindNextSlot(cursor, alignedSize, imageSize, dataOrigin, clusterSize, forbidden);
        if (target < 0) break;
        EmitFileMoves(moves, srcExtents, target, totalSize, fileName, clusterSize, dataOrigin);
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

    var cursor = dataOrigin;   // already the first cluster boundary by definition
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
        target = AlignUpFrom(target + ext.Length, dataOrigin, clusterSize);
      }
      cursor = AlignUpFrom(cursor + dirSize, dataOrigin, clusterSize);

      // Place the children immediately after.
      foreach (var childName in childrenOf[dirName].OrderByDescending(n => dataSizes[n])) {
        var childExtents = dataByFile[childName];
        var childSize = dataSizes[childName];
        target = cursor;
        foreach (var ext in childExtents) {
          if (ext.Offset != target)
            moves.Add(new ClusterMove(ext.Offset, target, ext.Length, childName));
          target = AlignUpFrom(target + ext.Length, dataOrigin, clusterSize);
        }
        cursor = AlignUpFrom(cursor + childSize, dataOrigin, clusterSize);
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
        target = AlignUpFrom(target + ext.Length, dataOrigin, clusterSize);
      }
      cursor = AlignUpFrom(cursor + size, dataOrigin, clusterSize);
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

  // ── Layout-template path ───────────────────────────────────────────────

  /// <summary>
  /// Plans moves driven by a <see cref="LayoutTemplate"/>. Builds an
  /// <see cref="IFilterFileContext"/> per file from the extent map, calls
  /// <see cref="LayoutTemplateResolver.Resolve"/> to assign each file to a
  /// zone, then emits one packed run per zone within the resolved byte
  /// bounds. Files in the leftover bucket are placed per the template's
  /// <see cref="LeftoverStrategy"/>: <c>FillGaps</c> drops them into the
  /// trailing slot after all zones, <c>AppendAtEnd</c> appends them after
  /// the highest zone end.
  /// </summary>
  private static IReadOnlyList<ClusterMove> PlanFromTemplate(
    Dictionary<string, List<DefragBlockInfo>> byFile,
    Dictionary<string, long> fileSizes,
    IReadOnlyList<DefragBlockInfo> extents,
    long dataOrigin, long imageSize, int clusterSize,
    List<(long Offset, long Length)> freeRegions,
    IReadOnlyList<(long Start, long End)> forbidden,
    DefragMode mode,
    LayoutTemplate template) {

    // Build filter contexts from each file. mtime/atime/ctime aren't carried
    // on DefragBlockInfo today — Wave 2 will plumb them through. For now the
    // contexts surface what's available (name, path, size, attributes derived
    // from DefragBlockClass) and leave timestamps null. Filters that compare
    // a missing field always evaluate false, which is the safe default
    // (those files end up in the leftover bucket).
    var fileNames = byFile.Keys.ToList();
    var contexts = new List<IFilterFileContext>(fileNames.Count);
    var allSizes = new List<long>(fileNames.Count);
    foreach (var n in fileNames) allSizes.Add(fileSizes[n]);

    foreach (var n in fileNames) {
      var firstExt = byFile[n][0];
      var nameOnly = ExtractFileName(n);
      var ext = ExtractExtension(nameOnly);
      // Derive a coarse "attributes" bit from the DefragBlockClass when
      // available: Directory => 0x10 (FAT-style dir bit), other classes 0.
      uint attrs = firstExt.Classification == DefragBlockClass.Directory ? 0x10u : 0u;
      contexts.Add(new FilterFileContext {
        Name = nameOnly,
        Path = n,
        Extension = ext,
        Size = fileSizes[n],
        Attributes = attrs,
        AllSizes = allSizes,
      });
    }

    var placements = LayoutTemplateResolver.Resolve(template, contexts, imageSize);

    // Group placements by zone (preserving SortIndex order) so we can emit
    // packed runs.
    var byZone = new Dictionary<string, List<ResolvedFilePlacement>>(StringComparer.Ordinal);
    foreach (var p in placements) {
      if (!byZone.TryGetValue(p.ZoneName, out var list))
        byZone[p.ZoneName] = list = [];
      list.Add(p);
    }
    foreach (var kv in byZone) kv.Value.Sort((a, b) => a.SortIndex.CompareTo(b.SortIndex));

    var moves = new List<ClusterMove>();

    // Place zone-resolved files first, in template order so adjacent zone
    // packing stays predictable. Within a zone, walk sorted placements and
    // emit per-file moves starting at the zone's start offset (clamped to
    // dataOrigin and away from forbidden regions).
    foreach (var zone in template.Zones) {
      if (!byZone.TryGetValue(zone.Name, out var zonePlacements)) continue;
      var (zStart, zEnd) = RangeSpec.Parse(zone.Range).Resolve(imageSize);
      zStart = Math.Max(zStart, dataOrigin);
      var cursor = AlignUpFrom(zStart, dataOrigin, clusterSize);
      foreach (var p in zonePlacements) {
        var fileName = fileNames[p.FileIndex];
        var totalSize = fileSizes[fileName];
        var alignedSize = AlignUp(totalSize, clusterSize);
        // Honor zEnd as a soft ceiling — but if a file doesn't fit in its
        // zone, allow it to spill past the zone (FillGaps semantics for
        // overflows). FindNextSlot keeps it clear of forbidden regions.
        var ceiling = mode == DefragMode.ConsolidateAtEnd ? imageSize : imageSize;
        var target = FindNextSlot(cursor, alignedSize, ceiling, dataOrigin, clusterSize, forbidden);
        if (target < 0) break;
        EmitFileMoves(moves, byFile[fileName], target, totalSize, fileName, clusterSize, dataOrigin);
        cursor = target + alignedSize;
        _ = zEnd; // zEnd is informational only at this layer; planner allows spill
      }
    }

    // Now place leftover files per the template's strategy. They go after
    // the highest used cursor (which already accounts for any spill from
    // zones) — both FillGaps and AppendAtEnd resolve to the same trailing
    // append in this Wave-1 implementation; the difference materialises in
    // Wave 2 when the streaming coordinator gains hole-aware allocation.
    if (byZone.TryGetValue(LayoutTemplateResolver.LeftoverZoneName, out var leftovers)) {
      // Anchor leftover cursor at the highest written offset so far, or at
      // dataOrigin if no zones were placed.
      long startCursor = dataOrigin;
      if (moves.Count > 0) {
        long highest = 0;
        foreach (var m in moves) {
          var end = m.DstOffset + AlignUp(m.Length, clusterSize);
          if (end > highest) highest = end;
        }
        startCursor = Math.Max(startCursor, highest);
      }
      var cursor = AlignUpFrom(startCursor, dataOrigin, clusterSize);
      foreach (var p in leftovers) {
        var fileName = fileNames[p.FileIndex];
        var totalSize = fileSizes[fileName];
        var alignedSize = AlignUp(totalSize, clusterSize);
        var target = FindNextSlot(cursor, alignedSize, imageSize, dataOrigin, clusterSize, forbidden);
        if (target < 0) break;
        EmitFileMoves(moves, byFile[fileName], target, totalSize, fileName, clusterSize, dataOrigin);
        cursor = target + alignedSize;
      }
    }

    return ResolveDependencies(moves, freeRegions, clusterSize);
  }

  private static string ExtractFileName(string fullName) {
    var slash = fullName.LastIndexOf('/');
    return slash < 0 ? fullName : fullName[(slash + 1)..];
  }

  private static string ExtractExtension(string name) {
    var dot = name.LastIndexOf('.');
    if (dot < 0 || dot == name.Length - 1) return string.Empty;
    return name[dot..].ToLowerInvariant();
  }
}
