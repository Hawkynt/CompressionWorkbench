#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Fat;

public sealed class FatFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveShrinkable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {

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
      Description: "Marks the image for transaction-based FAT updates (Windows Embedded/CE crash-safe style). Sets BS_Reserved1 = 0x01 in the boot sector."),
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
  public IReadOnlyList<long> CanonicalSizes => [737280, 1474560, 2949120];
  public void Shrink(Stream input, Stream output) =>
    Compression.Registry.ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new FatBlockMover();
    mover.Init(image); // reads only the 512-byte BPB
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
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

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    var stride = Math.Max(1, options.InterleaveStride);

    // Stream-based init: reads only the 512-byte BPB. Avoids loading a
    // multi-GB image into memory.
    var mover = new FatBlockMover();
    mover.Init(archive);

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
      options.MetadataZonePlacement);

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
      // zeroSource: false — same crash-safety rationale as DefragPlannerExecutor.
      // Old bytes become orphan data after the FAT-chain repatch below; they're
      // unreferenced but recoverable until the next allocation reuses the cluster.
      mover.MoveExtent(archive, move.SrcOffset, move.DstOffset, move.Length, zeroSource: false);
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
    foreach (var move in moves) {
      // A single move can span SEVERAL clusters (the planner emits one move per
      // contiguous run). Track every cluster slot the move touches, not just the
      // start, so multi-cluster files/directories relink to the right place.
      var slotCount = (int)((move.Length + clusterBytes - 1) / clusterBytes);
      for (var k = 0; k < slotCount; k++) {
        var src = move.SrcOffset + k * clusterBytes;
        var dst = move.DstOffset + k * clusterBytes;
        if (occupant.TryGetValue(src, out var origin)) {
          occupant[dst] = origin;
          finalOf[origin] = dst;
          if (src != dst) occupant.Remove(src);
        } else {
          // Source slot holds no tracked original (e.g. free staging bytes).
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

      mover.UpdateAllocationScattered(archive, owner, oldClusters, newClusters);

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
        var rebuilt = w.Build(totalSectors: totalSectors);
        archive.Position = 0;
        archive.Write(rebuilt);
        archive.SetLength(rebuilt.Length);
        break;
      }
      case DefragMode.ConsolidateAtEnd: {
        var w = new FatWriter();
        foreach (var (name, data) in files.OrderByDescending(f => f.Data.Length))
          w.AddFile(name, data);
        var rebuilt = w.Build(totalSectors: totalSectors);
        archive.Position = 0;
        archive.Write(rebuilt);
        archive.SetLength(rebuilt.Length);
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
        var rebuilt = w.Build(totalSectors: totalSectors);
        archive.Position = 0;
        archive.Write(rebuilt);
        archive.SetLength(rebuilt.Length);
        break;
      }
      default:
        throw new NotSupportedException($"Unsupported defrag mode: {options.Mode}");
    }
  }

  public string Id => "Fat";
  public string DisplayName => "FAT Filesystem Image";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".img";
  public IReadOnlyList<string> Extensions => [".img", ".ima", ".flp", ".fat"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "FAT12/FAT16/FAT32 filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FatReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FatReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new FatWriter();
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      w.AddFile(input.ArchiveName, input.ReadContent(),
                input.InMemoryContent != null ? null : File.GetLastWriteTime(input.FullPath));

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

    // Fixed image size + cluster on Auto: optimise the cluster size *within* that
    // fixed size to minimise slack waste (e.g. a 1.44 MB floppy packed tightly)
    // instead of falling back to the FATGEN default heuristic.
    if (totalSectors > 0 && clusterBytes == 0) {
      var picked = w.PickClusterForFixedImage(totalSectors, 512, forcedFatType, rootEntries, enableLfn);
      if (picked > 0) clusterBytes = picked;
    }

    var disk = totalSectors > 0
      ? w.Build(totalSectors, requestedClusterSize: clusterBytes, volumeLabel: label,
                forcedFatType: forcedFatType, enableLfn: enableLfn, transactionFat: tfat,
                requestedRootEntries: rootEntries, forceLfn: forceLfn)
      : w.BuildAutoSized(requestedClusterSize: clusterBytes, volumeLabel: label,
                         forcedFatType: forcedFatType, enableLfn: enableLfn, transactionFat: tfat,
                         requestedRootEntries: rootEntries, forceLfn: forceLfn);
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
  /// Add files to an existing FAT image. Current implementation re-builds the image
  /// with all existing files + the new ones — the inherent build-from-scratch design
  /// of <see cref="FatWriter"/> means "add" equals "re-pack" here. Use
  /// <see cref="Remove"/> first to clean up stale slots.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    archive.Position = 0;
    var reader = new FatReader(archive);
    var combined = new FatWriter();
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
      combined.AddFile(entry.Name, reader.Extract(entry));
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      combined.AddFile(input.ArchiveName, input.ReadContent(),
                       input.InMemoryContent != null ? null : File.GetLastWriteTime(input.FullPath));
    var totalSectors = (int)(archive.Length / 512);
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
}
