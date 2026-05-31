#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Fat;

public sealed class FatFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveShrinkable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

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
      Description: "VFAT LFN entries preserve mixed-case names and names > 8.3 chars. Disable only for strict DOS 8.3 compatibility."),
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

    if (stride > 1) {
      // Interleaved placement: two-phase approach.
      // Phase 1: Execute all raw byte moves.
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

      // Phase 2: Patch FAT chains per file using UpdateAllocationScattered.
      // Group moves by file, then for each file compute old and new cluster lists.
      // BPB is unchanged by data moves; no need to re-Init.
      var movesByFile = new Dictionary<string, List<ClusterMove>>(StringComparer.OrdinalIgnoreCase);
      foreach (var move in moves) {
        if (!movesByFile.TryGetValue(move.FileName, out var list))
          movesByFile[move.FileName] = list = [];
        list.Add(move);
      }

      // For each file, also gather the original full chain and compute the new chain.
      // We need the original chain (before any moves) to know the start cluster for
      // the directory-entry lookup. Fortunately, extent map gives us the per-file
      // source offsets, and the moves tell us which ones moved.
      var fileIdx = 0;
      foreach (var (fileName, fileMoves) in movesByFile) {
        // Build the mapping from old offset → new offset for this file's blocks.
        var offsetMap = new Dictionary<long, long>();
        foreach (var m in fileMoves)
          offsetMap[m.SrcOffset] = m.DstOffset;

        // Reconstruct the file's old cluster chain from the extent map.
        var fileExtents = extents
          .Where(e => e.Kind == DefragBlockKind.Used &&
                      string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase))
          .OrderBy(e => e.Offset)
          .ToList();

        var oldClusters = new List<int>();
        var newClusters = new List<int>();
        foreach (var ext in fileExtents) {
          var blocks = (int)((ext.Length + mover.ClusterSize - 1) / mover.ClusterSize);
          for (var b = 0; b < blocks; b++) {
            var srcOff = ext.Offset + (long)b * mover.ClusterSize;
            oldClusters.Add(mover.OffsetCluster(srcOff));
            var dstOff = offsetMap.TryGetValue(srcOff, out var mapped) ? mapped : srcOff;
            newClusters.Add(mover.OffsetCluster(dstOff));
          }
        }

        mover.UpdateAllocationScattered(archive, fileName, oldClusters, newClusters);
        // BPB unchanged — no re-Init needed. UpdateAllocationScattered already
        // does targeted writes + per-step flushes.
        fileIdx++;
        options.OnProgress?.Invoke(new DefragProgressEvent(
          Phase: "writing",
          Fraction: 0.5 + 0.5 * fileIdx / movesByFile.Count,
          CurrentReadOffset: -1,
          CurrentWriteOffset: -1,
          ImageSize: imageSize,
          BlockMap: null,
          Status: $"Patching chain {fileIdx} of {movesByFile.Count}: {fileName}"));
      }
    } else {
      // Contiguous placement: shared executor with per-move progress. BPB
      // doesn't change during defrag so no per-move re-Init is needed.
      DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize, reinitAfterMove: null);
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
  public IReadOnlyList<string> Extensions => [".img", ".ima", ".flp"];
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
      w.AddFile(input.ArchiveName, File.ReadAllBytes(input.FullPath),
                File.GetLastWriteTime(input.FullPath));

    var specific = options.FormatSpecific;
    var totalSectors  = ParseImageSizeSectors(specific?.GetValueOrDefault("ImageSize"));
    var clusterBytes  = ParseClusterSizeBytes(specific?.GetValueOrDefault("ClusterSize"));
    var forcedFatType = ParseFatType(specific?.GetValueOrDefault("FatType"));
    var rootEntries   = ParseRootEntries(specific?.GetValueOrDefault("RootEntries"));
    var label         = specific?.GetValueOrDefault("VolumeLabel");
    var enableLfn     = specific?.GetValueOrDefault("LongFilenames") != "false";
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
                requestedRootEntries: rootEntries)
      : w.BuildAutoSized(requestedClusterSize: clusterBytes, volumeLabel: label,
                         forcedFatType: forcedFatType, enableLfn: enableLfn, transactionFat: tfat,
                         requestedRootEntries: rootEntries);
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
      combined.AddFile(input.ArchiveName, File.ReadAllBytes(input.FullPath),
                       File.GetLastWriteTime(input.FullPath));
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
