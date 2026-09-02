#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Fat;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>https://download.microsoft.com/download/1/6/1/161ba512-40e2-4cc9-843a-923143f3456c/fatgen103.doc</c> — Microsoft "FAT32 File System Specification" (FATGEN 1.03), the canonical FAT12/16/32 spec</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Design_of_the_FAT_file_system</c> — Wikipedia's detailed on-disk reference incl. vendor variants</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/fat</c> — mainline kernel implementation</description></item>
/// </list>
/// </summary>
public sealed class FatFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveShrinkable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs the Convert Archive dialog / CLI exposes for FAT creation:
  /// FAT variant, image size, volume label, cluster size, root-entry count,
  /// long-filename and TFAT/FAT+ toggles. The richer upstream schema covers
  /// every BPB field the writer actually honours, including the legacy DMF
  /// 16-entry root and the Windows-style force-LFN-for-every-entry switch.
  /// Forced variants validate against the cluster-count minimum (FAT16 ≥ 4085,
  /// FAT32 ≥ 65525) and throw if the chosen geometry can't satisfy them.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "FatType",
      DisplayName: "FAT type",
      Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Auto", "FAT12", "FAT16", "FAT32"],
      Description: "Auto selects FAT12/16/32 by cluster count. Force a type when the target system requires it (e.g. FAT32 on a floppy-sized image for a game console)."),
    new FormatOptionDescriptor(
      Key: "ImageSize",
      DisplayName: "Image size",
      Kind: FormatOptionKind.Enum,
      Default: "Auto (fit to files)",
      AllowedValues: [
        // Auto
        "Auto (fit to files)",
        // 3.5" floppies
        "720 KB (3.5\" DD)", "1.44 MB (3.5\" HD)", "1.68 MB (DMF)", "2.88 MB (3.5\" ED)",
        // 5.25" floppies
        "160 KB (5.25\" SS/SD)", "180 KB (5.25\" SS/SD)", "320 KB (5.25\" DS/DD)",
        "360 KB (5.25\" DS/DD)", "1.2 MB (5.25\" HD)",
        // CD / DVD / Blu-ray / HD DVD
        "650 MB (CD)", "700 MB (CD)", "4.7 GB (DVD-5)", "8.5 GB (DVD-9)",
        "25 GB (BD-SL)", "50 GB (BD-DL)", "100 GB (BD-XL)", "128 GB (BD-XL)",
        "15 GB (HD DVD-SL)", "30 GB (HD DVD-DL)",
        // Hard disk / USB card sizes
        "32 MB", "128 MB", "512 MB", "1 GB", "2 GB", "4 GB"],
      Description: "Auto sizes the image to exactly hold the files being stored (recommended). " +
        "Fixed presets match floppy, optical and card formats."),
    new FormatOptionDescriptor(
      Key: "VolumeLabel",
      DisplayName: "Volume label",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Volume name shown by file managers (max 11 chars, ASCII only)."),
    new FormatOptionDescriptor(
      Key: "ClusterSize",
      DisplayName: "Cluster size",
      Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Auto", "512 B", "1 KB", "2 KB", "4 KB", "8 KB", "16 KB", "32 KB", "64 KB"],
      Description: "Allocation unit size. Auto picks the best fit for the image size and FAT type."),
    new FormatOptionDescriptor(
      Key: "RootEntries",
      DisplayName: "Root entries",
      Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Auto", "16 (DMF)", "32", "64", "112", "224", "512"],
      Description: "Max items in the root directory (FAT12/16 only; FAT32 has no limit). " +
        "Microsoft DMF Win95 disks used 16 to reclaim those sectors for data. " +
        "Auto: 224 for FAT12, 512 for FAT16.",
      DependsOn: "FatType=Auto|FAT12|FAT16"),
    new FormatOptionDescriptor(
      Key: "LongFilenames",
      DisplayName: "Long filenames (VFAT)",
      Kind: FormatOptionKind.Boolean,
      Default: "true",
      Description: "VFAT LFN entries preserve mixed-case names and names > 8.3 chars. Disable only for strict DOS 8.3 compatibility (no VFAT)."),
    new FormatOptionDescriptor(
      Key: "ForceLongFilenames",
      DisplayName: "Force LFN for every entry",
      Kind: FormatOptionKind.Boolean,
      Default: "false",
      Description: "Emit a VFAT long-name entry for every file/dir (with a generated 8.3 alias), even names that already fit 8.3 — the way Windows always records a long name. Implies VFAT on.",
      DependsOn: "LongFilenames=true"),
    new FormatOptionDescriptor(
      Key: "TransactionFat",
      DisplayName: "Transaction FAT (TFAT)",
      Kind: FormatOptionKind.Boolean,
      Default: "false",
      Description: "Marks the image for transaction-based FAT updates (Windows Embedded/CE crash-safe style). The marker is the TFAT tag in BS_FilSysType; BS_Reserved1 is left alone, because that is where FAT records an unclean unmount."),
    new FormatOptionDescriptor(
      Key: "FatPlus",
      DisplayName: "FAT+ timestamps",
      Kind: FormatOptionKind.Boolean,
      Default: "false",
      Description: "FAT+: stores sub-second creation-time precision in DIR_CrtTimeTenth (10 ms granularity instead of 2-second rounding)."),
  ];

  /// <summary>
  /// Walks the boot sector + FAT chains and emits the actual on-disk layout
  /// as <see cref="DefragBlockInfo"/>s — one per cluster-chain run per file,
  /// plus the reserved region (boot/FAT/root dir) and the free-cluster set.
  /// Used by the defragment window's block-map preview to show the real
  /// fragmented layout before defrag runs.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => FatExtentMap.Enumerate(image);

  // Canonical FAT image sizes in ascending order: 3.5" floppies, then continuous sizes for
  // hard disks. Shrink picks the smallest that fits the current payload.
    /// <summary>
  /// Gets the canonical sizes.
  /// </summary>
public IReadOnlyList<long> CanonicalSizes => [737280, 1474560, 2949120];
    /// <summary>
  /// Performs the shrink operation.
  /// </summary>
public void Shrink(Stream input, Stream output) =>
    Compression.Registry.ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
    /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new FatBlockMover();
    mover.Init(image); // reads only the 512-byte BPB
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new FatBlockMover();
    mover.Init(image); // reads only the 512-byte BPB
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  /// <summary>
  /// Rebuilds <paramref name="archive"/> in place so every file occupies a contiguous
  /// cluster run. Outer byte size is preserved — writes to the same stream at the same
  /// length. Equivalent to <see cref="Defragment(Stream, DefragOptions)"/> with
  /// <see cref="DefragMode.ConsolidateAtStart"/>.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware FAT defragmentor. Supports both a planner-driven in-place path
  /// (using <see cref="DefragPlanner"/> + <see cref="FatBlockMover"/>) and the
  /// legacy rebuild path (using <see cref="DefragRebuilder"/>).
  ///
  /// <para>The planner-driven path is used for <see cref="DefragMode.ConsolidateAtStart"/>,
  /// <see cref="DefragMode.ConsolidateAtEnd"/>, <see cref="DefragMode.FillHolesLazy"/>,
  /// and <see cref="DefragMode.CarveHole"/>. Falls back to the rebuild path on
  /// error.</para>
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    // Try the planner-driven path for supported modes.
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch {
        // Fall back to rebuild path on any error.
        archive.Position = 0;
      }
    }

    // Legacy rebuild path (fallback).
    DefragmentWithRebuild(archive, options);
  }

  // ── Planner-driven defrag path ─────────────────────────────────────────

  /// <summary>
  /// Lays the volume out again in place, with no rebuild behind it.
  /// </summary>
  /// <remarks>
  /// TFAT is this layout with a tag and a pair of sequence numbers on top, so
  /// it moves its clusters with this and puts its own markers back afterwards.
  /// A caller that needs the rebuild as a fallback wants
  /// <see cref="Defragment(Stream, DefragOptions)" /> instead.
  /// </remarks>
  public void DefragmentInPlace(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    this.DefragmentWithPlanner(archive, options);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var stride = Math.Max(1, options.InterleaveStride);

    // Stream-based init: reads only the 512-byte BPB. Avoids loading a
    // multi-GB image into memory.
    var mover = new FatBlockMover();
    mover.Init(archive);

    // Bound the planner at the VBR-declared volume size (end of the cluster heap),
    // NOT archive.Length. A FAT volume can sit inside a larger partition window or a
    // padded image (e.g. a default-sized volume formatted into a big partition); using
    // archive.Length would make ConsolidateAtEnd target the padding past the cluster
    // heap and move data outside the volume, corrupting it. For a tightly-built raw
    // image the two coincide to within one (unusable) cluster.
    var volumeBytes = mover.FirstDataByte + (long)mover.TotalDataClusters * mover.ClusterSize;
    var imageSize = volumeBytes > 0 ? Math.Min(volumeBytes, archive.Length) : archive.Length;

    // Emit scanning progress with the pre-defrag block map. The extent map
    // walks the archive directly (also streaming).
    var extents = FatExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "scanning",
      Fraction: 0,
      CurrentReadOffset: 0,
      CurrentWriteOffset: -1,
      ImageSize: imageSize,
      BlockMap: extents,
      Status: "Analysing layout"));

    // Compute the planned moves.
    var profile = options.Profile;
    // Quick profile: per-file consolidation only.
    // Performance profile: full zone-based rearrangement.
    var moves = DefragPlanner.Plan(
      extents,
      mover.FirstDataByte,
      imageSize,
      mover.ClusterSize,
      profile,
      options.Mode,
      stride,
      options.HoleSize,
      options.HoleAt,
      options.MetadataZonePlacement,
      layoutTemplate: null,
      // On FAT32 the root directory is an ordinary chain the BPB names, so a
      // metadata placement can move it like any other owner.
      movableMetadata: mover.RelocatableMetadata,
      // This descriptor runs its own move loop; it does understand runs held
      // outside the volume, so the planner may reach for that when a full
      // volume leaves nowhere on disk to park one.
      allowMemoryStaging: mover.SupportsHeldRuns);

    if (moves.Count == 0) {
      // Already defragmented — emit complete event.
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "complete",
        Fraction: 1,
        CurrentReadOffset: -1,
        CurrentWriteOffset: -1,
        ImageSize: imageSize,
        BlockMap: extents,
        Status: "Already defragmented"));
      return;
    }

    // Two-phase execution for BOTH contiguous (stride == 1) and interleaved
    // (stride > 1) placement:
    //   Phase 1 — execute every raw byte move in the planner's dependency order
    //             (staging moves included), leaving the FAT/dir metadata stale.
    //   Phase 2 — for each OWNER (file OR subdirectory), relink its complete
    //             cluster chain once via UpdateAllocationScattered and repatch
    //             the parent dirent + (for directories) the '.'/'..' pointers.
    //
    // A single per-owner relink is essential for FRAGMENTED owners: a file or
    // directory whose data spans several non-adjacent runs produces several
    // ClusterMoves, but its chain must be rewritten as ONE chain. The old
    // per-move UpdateAllocationAfterMove treated each run as an independent
    // file, which truncated multi-run directories (losing entries) and broke
    // multi-run files. Fusing the runs into one contiguous chain happens here.

    // Somewhere to hold a run whose destination is still occupied when the
    // volume has no free cluster to park it in. Only allocated if the plan asks.
    using var staging = moves.Any(m => m.Staging == DefragStaging.Park)
      ? new DefragStagingBuffer(options.StagingMemoryBudgetBytes)
      : null;

    // Phase 1: raw byte moves, in planner order.
    for (var i = 0; i < moves.Count; i++) {
      var move = moves[i];
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "writing",
        Fraction: (double)i / moves.Count * 0.5,
        CurrentReadOffset: move.SrcOffset,
        CurrentWriteOffset: move.DstOffset,
        ImageSize: imageSize,
        BlockMap: null,
        Status: $"Moving block {i + 1} of {moves.Count}: {move.FileName}"));

      switch (move.Staging) {
        case DefragStaging.Park:
          staging!.Park(archive, move.StagingSlot, move.SrcOffset, move.Length);
          break;
        case DefragStaging.Unpark:
          staging!.Unpark(archive, move.StagingSlot, move.DstOffset);
          break;
        default:
          // zeroSource: false — same crash-safety rationale as DefragPlannerExecutor.
          // Old bytes become orphan data after the FAT-chain repatch below; they're
          // unreferenced but recoverable until the next allocation reuses the cluster.
          mover.MoveExtent(archive, move.SrcOffset, move.DstOffset, move.Length, zeroSource: false);
          break;
      }
    }

    // Phase 2: per-owner chain relink + dir-pointer repatch. Group the original
    // extents by owner so each owner's full chain is rebuilt exactly once.
    var extentsByOwner = new Dictionary<string, List<DefragBlockInfo>>(StringComparer.OrdinalIgnoreCase);
    var originalClusterOffsets = new HashSet<long>();
    foreach (var e in extents) {
      if (e.Kind != DefragBlockKind.Used) continue;
      var owner = e.FileName ?? "<unknown>";
      if (!extentsByOwner.TryGetValue(owner, out var list))
        extentsByOwner[owner] = list = [];
      list.Add(e);
      var blocks = (int)((e.Length + mover.ClusterSize - 1) / mover.ClusterSize);
      for (var b = 0; b < blocks; b++)
        originalClusterOffsets.Add(e.Offset + (long)b * mover.ClusterSize);
    }

    // Where did each ORIGINAL cluster's bytes finally land? Simulate the byte
    // moves in planner order — exactly mirroring phase 1 — tracking which
    // original offset currently occupies each cluster slot. A move src→dst takes
    // whatever bytes are at `src` right now and relocates them to `dst`; if
    // those bytes belong to an original cluster, that cluster's final location
    // becomes `dst`. This correctly handles plain shuffles, slot swaps, and the
    // planner's staging hops (src → free-stage → dst) without any heuristics.
    var occupant = new Dictionary<long, long>(); // cluster slot offset → original offset currently there
    foreach (var off in originalClusterOffsets) occupant[off] = off;
    var finalOf = new Dictionary<long, long>();  // original offset → its final cluster offset
    foreach (var off in originalClusterOffsets) finalOf[off] = off;
    var clusterBytes = (long)mover.ClusterSize;
    // Clusters whose bytes are out of the volume entirely, waiting to be put
    // down: while held they occupy no slot, which is the whole point of holding
    // them, so the simulation has to take them off the map and put them back.
    var held = new Dictionary<(int Slot, int Index), long>();

    foreach (var move in moves) {
      // A single move can span SEVERAL clusters (the planner emits one move per
      // contiguous run). Track every cluster slot the move touches, not just the
      // start, so multi-cluster files/directories relink to the right place.
      var slotCount = (int)((move.Length + clusterBytes - 1) / clusterBytes);

      if (move.Staging == DefragStaging.Park) {
        for (var k = 0; k < slotCount; k++) {
          var src = move.SrcOffset + k * clusterBytes;
          if (!occupant.Remove(src, out var origin)) continue;
          held[(move.StagingSlot, k)] = origin;
        }
        continue;
      }

      if (move.Staging == DefragStaging.Unpark) {
        for (var k = 0; k < slotCount; k++) {
          var dst = move.DstOffset + k * clusterBytes;
          if (held.Remove((move.StagingSlot, k), out var origin)) {
            occupant[dst] = origin;
            finalOf[origin] = dst;
          } else {
            occupant[dst] = dst;
          }
        }
        continue;
      }

      // Read every source slot before writing any destination. A run that
      // shifts by less than its own length has slots that are both: writing as
      // we went made a later slot read the origin the earlier one had just put
      // there, and the owner's clusters came back in an order that was not the
      // order its chain is in — the file kept every byte and read as noise.
      var origins = new long[slotCount];
      for (var k = 0; k < slotCount; k++) {
        var src = move.SrcOffset + k * clusterBytes;
        origins[k] = occupant.TryGetValue(src, out var origin) ? origin : -1;
      }

      for (var k = 0; k < slotCount; k++)
        occupant.Remove(move.SrcOffset + k * clusterBytes);

      for (var k = 0; k < slotCount; k++) {
        var dst = move.DstOffset + k * clusterBytes;
        if (origins[k] >= 0) {
          occupant[dst] = origins[k];
          finalOf[origins[k]] = dst;
        } else {
          // Source slot held no tracked original (e.g. free staging bytes).
          occupant[dst] = dst;
        }
      }
    }
    long FinalOffset(long srcOff) => finalOf.TryGetValue(srcOff, out var f) ? f : srcOff;
    // Owners that actually have at least one block moved.
    var movedOwners = new HashSet<string>(moves.Select(m => m.FileName), StringComparer.OrdinalIgnoreCase);

    // Old-first-cluster → new-first-cluster remap for every DIRECTORY whose
    // start cluster changed. After all chains are relinked, a single tree walk
    // uses this to fix the '.' self-pointers and '..' parent-pointers so the
    // on-disk tree stays internally consistent (fsck / Windows clean).
    var dirStartRemap = new Dictionary<int, int>();

    // Relink DIRECTORIES before FILES, and shallower directories before deeper
    // ones. PatchDirectoryEntriesStream walks the tree from the root following
    // the live FAT chains + parent dirents, so a directory's chain and its
    // parent-dirent pointer must already be correct before we patch any entry
    // that lives INSIDE it (a child file, or a deeper subdirectory). Depth =
    // number of path separators; directories (owner ends with '/') sort first.
    static int Depth(string owner) => owner.Count(c => c == '/');
    var orderedOwners = extentsByOwner.Keys
      .Where(movedOwners.Contains)
      .OrderBy(o => o.EndsWith('/') ? 0 : 1)   // directories before files
      .ThenBy(Depth)                            // shallow before deep
      .ThenBy(o => o, StringComparer.OrdinalIgnoreCase)
      .ToList();

    // Every cluster any owner ends up on. An owner's old clusters routinely
    // become another owner's new ones, so this is what keeps the per-owner
    // relink from freeing a chain that has just been written.
    // A relocated structure is repointed rather than relinked: what names the
    // FAT32 root is the BPB, not a directory entry, so it takes no part in the
    // per-owner chain rebuild. Its first move's source and its last move's
    // destination bracket the whole journey, staging hops included.
    var relocatable = mover.RelocatableMetadata;
    var metadataFrom = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    var metadataTo = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    var metadataLength = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var move in moves) {
      if (!relocatable.Contains(move.FileName)) continue;
      if (!metadataFrom.ContainsKey(move.FileName)) metadataFrom[move.FileName] = move.SrcOffset;
      metadataTo[move.FileName] = move.DstOffset;
      metadataLength[move.FileName] = move.Length;
    }

    // Every cluster any owner ends up on. An owner's old clusters routinely
    // become another owner's new ones, so this is what keeps the per-owner
    // relink from freeing a chain that has just been written.
    var clustersLiveAfterMove = new HashSet<int>();
    // A relocated structure's new clusters are live for the same reason: they
    // are usually where some file used to be, and freeing them would leave the
    // root directory chained through a cluster marked free.
    foreach (var (owner, target) in metadataTo) {
      var blocks = (int)((metadataLength[owner] + mover.ClusterSize - 1) / mover.ClusterSize);
      for (var b = 0; b < blocks; b++)
        clustersLiveAfterMove.Add(mover.OffsetCluster(target + (long)b * mover.ClusterSize));
    }
    foreach (var owner in orderedOwners)
      foreach (var ext in extentsByOwner[owner]) {
        var blocks = (int)((ext.Length + mover.ClusterSize - 1) / mover.ClusterSize);
        for (var b = 0; b < blocks; b++)
          clustersLiveAfterMove.Add(mover.OffsetCluster(FinalOffset(ext.Offset + (long)b * mover.ClusterSize)));
      }

    var ownerIdx = 0;
    foreach (var owner in orderedOwners) {
      // Extents arrive from the extent map in CHAIN order (the map walks each
      // owner's cluster chain run by run). Preserve that order — sorting by
      // offset would scramble a fragmented owner's logical sequence and the
      // relinked chain would carry its clusters in the wrong order, truncating
      // or corrupting the content (lost directory entries / shuffled file data).
      var ownerExtents = extentsByOwner[owner];

      var oldClusters = new List<int>();
      var newClusters = new List<int>();
      foreach (var ext in ownerExtents) {
        var blocks = (int)((ext.Length + mover.ClusterSize - 1) / mover.ClusterSize);
        for (var b = 0; b < blocks; b++) {
          var srcOff = ext.Offset + (long)b * mover.ClusterSize;
          oldClusters.Add(mover.OffsetCluster(srcOff));
          newClusters.Add(mover.OffsetCluster(FinalOffset(srcOff)));
        }
      }
      if (oldClusters.Count == 0) continue;

      mover.UpdateAllocationScattered(archive, owner, oldClusters, newClusters, clustersLiveAfterMove);

      if (owner.EndsWith('/') && oldClusters[0] != newClusters[0])
        dirStartRemap[oldClusters[0]] = newClusters[0];

      ++ownerIdx;
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "writing",
        Fraction: 0.5 + 0.5 * ownerIdx / Math.Max(1, movedOwners.Count),
        CurrentReadOffset: -1,
        CurrentWriteOffset: -1,
        ImageSize: imageSize,
        BlockMap: null,
        Status: $"Patching chain {ownerIdx} of {movedOwners.Count}: {owner}"));
    }

    // After every chain is relinked, repatch the '.' / '..' self/parent
    // pointers of relocated subdirectories so the on-disk tree is consistent.
    if (dirStartRemap.Count > 0)
      mover.RepatchDotEntries(archive, dirStartRemap);

    // The root's chain is written last: doing it before the per-owner relink
    // meant the files' own relink freed the clusters the root had just been
    // given, and the volume came back with its root chained through free space.
    if (metadataFrom.Count > 0) {
      var liveRanges = new List<(long Offset, long Length)>();
      foreach (var cluster in clustersLiveAfterMove)
        liveRanges.Add((mover.ClusterOffset(cluster), mover.ClusterSize));
      foreach (var (owner, source) in metadataFrom)
        mover.UpdateMetadataAfterMove(archive, owner, source, metadataTo[owner],
          metadataLength[owner], liveRanges);
    }

    // Emit complete event with post-defrag block map. Re-walk the archive
    // directly — no need to load it into memory.
    archive.Position = 0;
    var postExtents = FatExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "complete",
      Fraction: 1,
      CurrentReadOffset: -1,
      CurrentWriteOffset: -1,
      ImageSize: imageSize,
      BlockMap: postExtents,
      Status: $"Defragmentation complete — {moves.Count} move(s) executed"));
  }

  // ── Legacy rebuild path ────────────────────────────────────────────────

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var originalLength = archive.Length;
    var reader = new FatReader(archive);
    var files = reader.Entries.Where(e => !e.IsDirectory)
                              .Select(e => (Name: e.Name, Data: reader.Extract(e)))
                              .ToList();
    var totalSectors = (int)(originalLength / 512);

    switch (options.Mode) {
      case DefragMode.ConsolidateAtStart:
      case DefragMode.FillHolesLazy: {
        // FAT's writer is always start-packed; both modes converge to the same layout.
        var w = new FatWriter();
        foreach (var (name, data) in files) w.AddFile(name, data);
        WriteVolume(archive, w, totalSectors);
        break;
      }
      case DefragMode.ConsolidateAtEnd: {
        var w = new FatWriter();
        foreach (var (name, data) in files.OrderByDescending(f => f.Data.Length))
          w.AddFile(name, data);
        WriteVolume(archive, w, totalSectors);
        break;
      }
      case DefragMode.CarveHole: {
        if (options.HoleSize <= 0)
          throw new ArgumentException("HoleSize must be positive for CarveHole.", nameof(options));
        var totalLive = files.Sum(f => (long)f.Data.Length);
        if (totalLive + options.HoleSize > originalLength)
          throw new ArgumentException(
            $"Image is too small for the carved hole: live {totalLive} + hole {options.HoleSize} > image {originalLength}.",
            nameof(options));
        // Pack at start; trailing free space then includes the requested hole.
        var w = new FatWriter();
        foreach (var (name, data) in files) w.AddFile(name, data);
        WriteVolume(archive, w, totalSectors);
        break;
      }
      default:
        throw new NotSupportedException($"Unsupported defrag mode: {options.Mode}");
    }
  }

  /// <summary>
  /// Writes a freshly laid-out volume over <paramref name="archive" />. The
  /// build goes straight to the stream: a byte[] cannot hold more than two
  /// gigabytes, so building the image in memory first made this fallback throw
  /// on exactly the volumes that most need it.
  /// </summary>
  private static void WriteVolume(Stream archive, FatWriter writer, int totalSectors) {
    var scratch = Path.GetTempFileName();
    try {
      using (var staged = File.Open(scratch, FileMode.Open, FileAccess.ReadWrite)) {
        try {
          staged.SetLength(0);
          writer.BuildTo(staged, totalSectors: totalSectors);
        } catch (InvalidOperationException) {
          // The same files that fit the old volume can need one cluster more
          // once the metadata is laid out afresh — a different cluster size or
          // FAT length shifts the first data sector. A defragmentation must
          // never fail for that reason, so the volume is sized to its contents.
          staged.Position = 0;
          staged.SetLength(0);
          writer.BuildToStreaming(staged, requestedTotalSectors: totalSectors);
        }
      }

      using (var staged = File.OpenRead(scratch)) {
        archive.Position = 0;
        archive.SetLength(staged.Length);
        staged.CopyTo(archive);
        archive.Flush();
      }
    } finally {
      try { File.Delete(scratch); } catch { /* scratch file already gone */ }
    }
  }

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Fat";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "FAT Filesystem Image";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  // R/W: add/remove edit the FAT, clusters and directory in place (FatModifier /
  // FatRemover); existing files and the boot sector stay byte-identical. A verified
  // rebuild is only a structural-edge-case fallback. See FormatCapabilities.cs.
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".img";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".img", ".ima", ".flp", ".fat"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "FAT12/FAT16/FAT32 filesystem image";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FatReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FatReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single FAT entry as a forward-only stream that walks its
  /// cluster chain one cluster at a time, wrapped in a
  /// <see cref="BoundedEntryStream"/> sized to the entry's logical size.
  /// Reads past <c>entry.Size</c> return 0 — the cluster-tail slack is
  /// physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new FatReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      if (e.StartCluster < 2 || e.Size <= 0)
        return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
      var chain = r.OpenChainStream(e);
      return new BoundedEntryStream(chain, e.Size, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>
  /// Native in-memory single-entry extraction. Buffers the bounded
  /// <see cref="OpenEntry"/> stream into a fresh byte array — never reads
  /// past the entry's logical size.
  /// </summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    // A seekable target takes the streaming route: the writer then places file
    // data by seek instead of holding it, so the volume is bounded by the disk
    // rather than by what a byte[] can address.
    if (output.CanSeek && TotalInputBytes(inputs) > StreamingCreateThreshold) {
      this.CreateFromStreams(output, AsStreamingInputs(inputs), options);
      return;
    }

    var w = new FatWriter();
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      w.AddFile(input.ArchiveName, input.ReadContent(),
                input.InMemoryContent != null ? null : File.GetLastWriteTime(input.FullPath));
    BuildAndWrite(output, w, options);
  }

  /// <summary>
  /// Two-pass streaming Create: pre-known per-input sizes drive the FAT
  /// geometry choice in pass 1, then pass 2 streams each input's bytes from
  /// its <see cref="StreamingArchiveInput.OpenStream"/> factory straight
  /// into the pre-allocated cluster run. Peak memory is bounded by the
  /// cluster size + a 64 KB copy buffer — independent of total file size.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new FatWriter();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream, null);
    }
    BuildAndWrite(output, w, options, streaming: true);
  }

  private static void BuildAndWrite(Stream output, FatWriter w, FormatCreateOptions options, bool streaming = false) {

    var specific = options.FormatSpecific;
    var totalSectors  = ParseImageSizeSectors(specific?.GetValueOrDefault("ImageSize"));
    var clusterBytes  = ParseClusterSizeBytes(specific?.GetValueOrDefault("ClusterSize"));
    var forcedFatType = ParseFatType(specific?.GetValueOrDefault("FatType"));
    var rootEntries   = ParseRootEntries(specific?.GetValueOrDefault("RootEntries"));
    var label         = specific?.GetValueOrDefault("VolumeLabel");
    var enableLfn     = specific?.GetValueOrDefault("LongFilenames") != "false";
    var forceLfn      = specific?.GetValueOrDefault("ForceLongFilenames") == "true";
    if (forceLfn) enableLfn = true; // force-LFN implies VFAT is on
    var tfat          = specific?.GetValueOrDefault("TransactionFat") == "true";
    var fatPlus       = specific?.GetValueOrDefault("FatPlus") == "true";
    _ = fatPlus; // modTime always written; DIR_CrtTimeTenth provides sub-second precision
    // Universal compact --minimal flag: size the image to the smallest valid
    // geometry that holds the data (tight FAT12 + minimal root). Honoured only
    // on the auto-size path; an explicit ImageSize still wins.
    var minimal       = specific?.GetValueOrDefault("MinimalGeometry") == "true";

    // Fixed image size + cluster on Auto: optimise the cluster size *within* that
    // fixed size to minimise slack waste (e.g. a 1.44 MB floppy packed tightly)
    // instead of falling back to the FATGEN default heuristic.
    if (totalSectors > 0 && clusterBytes == 0) {
      var picked = w.PickClusterForFixedImage(totalSectors, 512, forcedFatType, rootEntries, enableLfn);
      if (picked > 0) clusterBytes = picked;
    }

    // Streaming needs a writable+seekable stream; the CreateFromStreams contract
    // already promises this through the pipeline. An explicit ImageSize is passed
    // through so the requested geometry is honoured rather than auto-fitted.
    // Streaming inputs go through the streaming writer. A buffered create stays
    // on Build()/BuildAutoSized(), which is what honours the minimal-geometry and
    // format-specific knobs; Create() diverts a large payload to the streaming
    // path before it ever gets here.
    if (streaming && output.CanSeek) {
      w.BuildToStreaming(output, requestedClusterSize: clusterBytes, volumeLabel: label,
                         forcedFatType: forcedFatType, enableLfn: enableLfn, transactionFat: tfat,
                         requestedRootEntries: rootEntries, forceLfn: forceLfn,
                         requestedTotalSectors: totalSectors);
      return;
    }

    // A fixed size goes through the streaming writer too whenever the target can
    // seek: Build() materialises the whole volume as one byte[], which caps it at
    // the ~2 GB array limit and makes every advertised size at or above that
    // throw. BuildTo() sets the stream length and writes only the metadata
    // regions, so free space stays sparse and large volumes cost nothing.
    if (totalSectors > 0 && output.CanSeek) {
      w.BuildTo(output, totalSectors, requestedClusterSize: clusterBytes, volumeLabel: label,
                forcedFatType: forcedFatType, enableLfn: enableLfn, transactionFat: tfat,
                requestedRootEntries: rootEntries, forceLfn: forceLfn);
      return;
    }

    var disk = totalSectors > 0
      ? w.Build(totalSectors, requestedClusterSize: clusterBytes, volumeLabel: label,
                forcedFatType: forcedFatType, enableLfn: enableLfn, transactionFat: tfat,
                requestedRootEntries: rootEntries, forceLfn: forceLfn)
      : w.BuildAutoSized(requestedClusterSize: clusterBytes, volumeLabel: label,
                         forcedFatType: forcedFatType, enableLfn: enableLfn, transactionFat: tfat,
                         requestedRootEntries: rootEntries, forceLfn: forceLfn, minimal: minimal);
    output.Write(disk);
  }

  private static int ParseRootEntries(string? s) => s?.Trim() switch {
    "16 (DMF)" => 16,
    "32"  => 32,
    "64"  => 64,
    "112" => 112,
    "224" => 224,
    "512" => 512,
    _     => 0,  // Auto
  };

  private static int ParseFatType(string? s) => s?.Trim() switch {
    "FAT12" => 12,
    "FAT16" => 16,
    "FAT32" => 32,
    _       => 0,  // Auto
  };

  private static int ParseImageSizeSectors(string? s) => s?.Trim() switch {
    // 3.5" floppies
    "720 KB (3.5\" DD)"    => 1440,
    "1.44 MB (3.5\" HD)"   => 2880,
    "1.68 MB (DMF)"        => 3360,
    "2.88 MB (3.5\" ED)"   => 5760,
    // 5.25" floppies
    "160 KB (5.25\" SS/SD)" => 320,
    "180 KB (5.25\" SS/SD)" => 360,
    "320 KB (5.25\" DS/DD)" => 640,
    "360 KB (5.25\" DS/DD)" => 720,
    "1.2 MB (5.25\" HD)"    => 2400,
    // Optical
    "650 MB (CD)"   => 1330200,
    "700 MB (CD)"   => 1433600,
    "4.7 GB (DVD-5)"  => 9830400,
    "8.5 GB (DVD-9)"  => 17825792,
    "25 GB (BD-SL)"   => 52428800,
    "50 GB (BD-DL)"   => 104857600,
    "100 GB (BD-XL)"  => 209715200,
    "128 GB (BD-XL)"  => 268435456,
    "15 GB (HD DVD-SL)" => 31457280,
    "30 GB (HD DVD-DL)" => 62914560,
    // Generic sizes
    "32 MB"   => 65536,
    "128 MB"  => 262144,
    "512 MB"  => 1048576,
    "1 GB"    => 2097152,
    "2 GB"    => 4194304,
    "4 GB"    => 8388608,
    _         => 0,  // "Auto (fit to files)" or anything else → auto-size
  };

  private static int ParseClusterSizeBytes(string? s) => s?.Trim() switch {
    "512 B"  => 512,
    "1 KB"   => 1024,
    "2 KB"   => 2048,
    "4 KB"   => 4096,
    "8 KB"   => 8192,
    "16 KB"  => 16384,
    "32 KB"  => 32768,
    "64 KB"  => 65536,
    _        => 0,  // 0 → auto
  };

  /// <summary>
  /// Adds (or replaces by name) files in an existing FAT image. The common case is a
  /// genuine in-place edit via <see cref="FatModifier"/>: free clusters are allocated
  /// from the FAT, the data is written into them, the cluster chain is linked in every
  /// FAT copy and a directory entry is inserted — existing files, their clusters and the
  /// boot sector stay byte-identical and the image keeps its length. Structural cases the
  /// in-place path does not handle (nested sub-directory targets, a full root directory,
  /// insufficient free space) fall back to the verified <see cref="FatWriter"/> rebuild.
  /// </summary>
  /// <summary>
  /// Largest image the in-place editors can work on. FatModifier and FatRemover
  /// mutate a byte[] copy of the whole volume, which a FAT32 image is under no
  /// obligation to fit in. Past this, the edit is applied by a streaming rebuild
  /// instead -- correct, just not in-place.
  /// </summary>
  private const long MaxBufferedImageBytes = 1L << 31;

  /// <summary>
  /// Applies an edit by reading every surviving entry out of <paramref name="archive" />
  /// and writing a fresh volume of the same declared size back over it. Used when the
  /// image is too large to buffer; memory scales with the content, not the volume.
  /// </summary>
  private static void RebuildInPlaceStreaming(
      Stream archive,
      IReadOnlyList<(string Name, byte[] Data, DateTime? Mtime)> additions,
      ISet<string>? drop) {
    var totalSectors = (int)Math.Min(int.MaxValue, archive.Length / 512);
    var combined = new FatWriter();

    archive.Position = 0;
    var reader = new FatReader(archive, leaveOpen: true);
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory)) {
      if (drop != null && (drop.Contains(entry.Name) || drop.Contains(Path.GetFileName(entry.Name))))
        continue;
      combined.AddFile(entry.Name, reader.Extract(entry));
    }
    foreach (var (name, data, mtime) in additions)
      combined.AddFile(name, data, mtime);

    // The new volume is laid out in scratch first. Truncating the archive and
    // building straight into it destroyed the volume whenever the build
    // refused — a file that does not fit used to cost the caller everything
    // that was already on the disk.
    var scratch = Path.GetTempFileName();
    try {
      using (var staged = File.Open(scratch, FileMode.Open, FileAccess.ReadWrite))
        combined.BuildTo(staged, totalSectors);

      using (var staged = File.OpenRead(scratch)) {
        archive.Position = 0;
        archive.SetLength(staged.Length);
        staged.CopyTo(archive);
        archive.Flush();
      }
    } finally {
      try { File.Delete(scratch); } catch { /* scratch file already gone */ }
    }
  }

    /// <summary>
  /// Adds the supplied entry to the target container.
  /// </summary>
public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var items0 = inputs.Where(i => !i.IsDirectory)
      .Select(i => (Name: i.ArchiveName, Data: i.ReadContent(),
                    Mtime: i.InMemoryContent != null ? (DateTime?)null : File.GetLastWriteTime(i.FullPath)))
      .ToList();
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      RebuildInPlaceStreaming(archive, items0, drop: null);
      return;
    }

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var original = ms.ToArray();

    var items = inputs.Where(i => !i.IsDirectory)
      .Select(i => (Name: i.ArchiveName, Data: i.ReadContent(),
                    Mtime: i.InMemoryContent != null ? (DateTime?)null : File.GetLastWriteTime(i.FullPath)))
      .ToList();

    // Try the genuine in-place edit on a copy; commit only if every input succeeds so a
    // structural limit leaves the source untouched and the rebuild path takes over.
    var work = (byte[])original.Clone();
    var inPlace = true;
    try {
      foreach (var (name, data, mtime) in items)
        FatModifier.AddFile(work, name, data, mtime);
    } catch (Exception ex) when (ex is NotSupportedException or IOException
                                 or InvalidDataException or InvalidOperationException) {
      inPlace = false;
    }
    if (inPlace) {
      archive.Position = 0;
      archive.Write(work, 0, work.Length);
      archive.SetLength(work.Length);
      return;
    }

    // Fallback: verified rebuild from the untouched original.
    var reader = new FatReader(new MemoryStream(original, writable: false));
    var combined = new FatWriter();
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
      combined.AddFile(entry.Name, reader.Extract(entry));
    foreach (var (name, data, mtime) in items)
      combined.AddFile(name, data, mtime);
    var totalSectors = (int)(original.Length / 512);
    var rebuilt = combined.Build(totalSectors: totalSectors);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Removes files from an existing FAT image with full secure wipe (cluster bytes,
  /// cluster-tip slack, directory entries, FAT chain entries). No forensic recovery
  /// of the removed content is possible from the resulting bytes.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      RebuildInPlaceStreaming(archive, [], new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase));
      return;
    }

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var name in entryNames)
      FatRemover.Remove(image, name);
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  /// <summary>
  /// Zeros all unused space in the FAT image: free clusters, cluster-tip slack,
  /// and optionally deleted directory entries. Uses the generic
  /// <see cref="UnusedSpaceWiper"/> driven by the FAT extent map plus a
  /// directory-entry-based file-size lookup for cluster-tip precision.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    // Build file-size lookup from directory entries for cluster-tip wiping.
    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new FatReader(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory)
            sizeMap[entry.Name] = entry.Size;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        // If reader fails, skip cluster-tip wiping.
        fileSizeLookup = null;
      }
    }

    // Enumerate extents — this gives us the cluster-aligned layout.
    image.Position = 0;
    var extents = FatExtentMap.Enumerate(image);

    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }
  /// <summary>
  /// Turns buffered inputs into streaming ones. Only a length is needed to lay a
  /// volume out; reading each input into a byte[] first caps the volume at what
  /// an array can hold even though the writer places file data by seek.
  /// </summary>
  private static List<Compression.Registry.Streaming.StreamingArchiveInput> AsStreamingInputs(
      IReadOnlyList<ArchiveInputInfo> inputs) {
    var result = new List<Compression.Registry.Streaming.StreamingArchiveInput>();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      var size = info.InMemoryContent?.LongLength
                 ?? (File.Exists(info.FullPath) ? new FileInfo(info.FullPath).Length : 0L);
      result.Add(new Compression.Registry.Streaming.StreamingArchiveInput(
        info.ArchiveName, size, false,
        () => info.InMemoryContent is { } bytes
          ? new MemoryStream(bytes, writable: false)
          : File.OpenRead(info.FullPath)));
    }
    return result;
  }

  /// <summary>
  /// Payload above which creation takes the streaming route. Below it the
  /// buffered writer is used, which is what honours the format-specific options
  /// (NTFS compression, explicit geometry) the streaming path cannot express.
  /// </summary>
  private const long StreamingCreateThreshold = 1024L * 1024 * 1024;

  /// <summary>Total bytes the inputs will contribute to the volume.</summary>
  private static long TotalInputBytes(IReadOnlyList<ArchiveInputInfo> inputs) {
    var total = 0L;
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      try {
        total += i.InMemoryContent?.LongLength
                 ?? (File.Exists(i.FullPath) ? new FileInfo(i.FullPath).Length : 0L);
      } catch { /* unreadable input — the writer will report it */ }
    }
    return total;
  }

}
