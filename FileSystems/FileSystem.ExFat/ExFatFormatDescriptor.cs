#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ExFat;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/windows/win32/fileio/exfat-specification</c> — Microsoft's official exFAT file system specification</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/exfat</c> — mainline kernel implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/ExFAT</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class ExFatFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// Zeros all unused space in the exFAT image: free clusters, cluster-tip slack
  /// (the bytes between a file's real size and the end of its last allocated
  /// cluster), and any gaps outside the reserved/FAT/heap-used regions. Driven
  /// by the generic <see cref="UnusedSpaceWiper"/> over the exFAT extent map,
  /// with a directory-entry-based file-size lookup for cluster-tip precision.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new ExFatReader(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory)
            sizeMap[entry.Name] = entry.Size;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = ExFatExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunables surfaced by the Convert Archive dialog / CLI for exFAT creation:
  /// image size (Auto / floppy-to-card presets), volume label (written as a
  /// Volume Label Directory Entry, type 0x83), and cluster size. Auto sizing
  /// runs the layout optimiser over the file set; an empty label still emits
  /// the entry with character count 0 to match Windows' format.com behaviour.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "ImageSize",
      DisplayName: "Image size",
      Kind: FormatOptionKind.Enum,
      Default: "Auto (fit to files)",
      AllowedValues: ["Auto (fit to files)", "32 MB", "128 MB", "256 MB", "512 MB", "1 GB", "2 GB", "4 GB", "16 GB", "32 GB", "128 GB"],
      Description: "Total image capacity. Auto sizes the image to exactly hold the files (recommended)."),
    new FormatOptionDescriptor(
      Key: "VolumeLabel",
      DisplayName: "Volume label",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Volume name (max 15 chars, Unicode)."),
    new FormatOptionDescriptor(
      Key: "ClusterSize",
      DisplayName: "Cluster size",
      Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Auto", "4 KB", "8 KB", "16 KB", "32 KB", "64 KB", "128 KB"],
      Description: "Allocation unit size. Auto picks the size that minimises slack + FAT overhead " +
        "for the files being stored. Larger clusters reduce FAT overhead but waste more space per file."),
  ];

  /// <summary>
  /// Walks the VBR + FAT + cluster heap and yields the actual on-disk
  /// layout — VBR/backup VBR + FAT region as MetadataReserved, allocation
  /// bitmap + up-case table as MetadataReserved, every file's cluster-chain
  /// run (or the contiguous range when <c>NoFatChain</c> is set) as Used,
  /// and the un-owned cluster gaps as Free.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => ExFatExtentMap.Enumerate(image);

  public string Id => "ExFat";
  public string DisplayName => "exFAT";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new ExFatBlockMover();
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    mover.Init(ms.ToArray());
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new ExFatBlockMover();
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    mover.Init(ms.ToArray());
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware exFAT defragmentor. Supports planner-driven in-place path
  /// and falls back to legacy rebuild path.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch (Exception planFailure) {
        // A silent fallback looks exactly like a successful in-place
        // defragmentation from outside, so the reason is reported.
        options.OnProgress?.Invoke(new DefragProgressEvent(
          "fallback", 0, -1, -1, archive.Length, null,
          $"In-place planning declined ({planFailure.GetType().Name}: " +
          $"{FirstLine(planFailure.Message)}); rebuilding instead"));
        archive.Position = 0;
      }
    }
    DefragmentWithRebuild(archive, options);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new ExFatBlockMover();
    mover.Init(archive); // reads only the 512-byte VBR

    // Stream the extent map directly off the archive — no whole-image load.
    var extents = ExFatExtentMap.Enumerate(archive).ToList();
    // Use the VBR-declared volume size (clusterHeapOffset + clusterCount * clusterSize)
    // rather than archive.Length so the planner doesn't target offsets past the end
    // of the cluster heap. When the exFAT image sits in a larger container
    // (partition window, sparse VHD), archive.Length includes trailing padding
    // bytes that are NOT part of the exFAT volume — placing a file there would
    // assign it a cluster number outside [2, clusterCount+1] and cause
    // UpdateAllocationAfterMove to write a FAT entry past fatLength, corrupting
    // the cluster heap contents.
    var volumeSize = Math.Min(mover.VolumeSize, archive.Length);
    options.OnProgress?.Invoke(new DefragProgressEvent("scanning", 0, 0, -1, volumeSize, extents, "Analysing layout"));

    // The allocation bitmap and up-case table are files with a directory entry
    // apiece, so a metadata placement can move them where it wants.
    var moves = DefragPlanner.Plan(extents, mover.FirstDataByte, volumeSize, mover.ClusterSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement, movableMetadata: mover.RelocatableMetadata);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, volumeSize, extents, "Already defragmented"));
      return;
    }

    // Refuse the whole plan before any of it is carried out. An exFAT directory
    // entry describes one contiguous run, so a file whose destination is not
    // contiguous cannot be expressed — and the mover says so, but it says it
    // while relinking, which is after the data has been moved. The caller then
    // falls back to rebuilding from a volume that is already half-migrated, and
    // rebuilds the damage faithfully: files came back at full length holding
    // other files' bytes.
    //
    // The executor has a check of its own for an owner that arrives in several
    // moves. This is the other half of the same rule: one move can still land on
    // clusters that are not consecutive.
    foreach (var owner in moves.Where(m => !string.IsNullOrEmpty(m.FileName))
                               .GroupBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)) {
      if (mover.RelocatableMetadata.Contains(owner.Key)) continue;   // repointed, not relinked
      var destinations = owner.OrderBy(m => m.SrcOffset)
                              .Select(m => (Start: m.DstOffset, End: m.DstOffset + m.Length))
                              .ToList();
      for (var i = 1; i < destinations.Count; ++i)
        if (destinations[i].Start != destinations[i - 1].End)
          throw new NotSupportedException(
            $"exFAT: '{owner.Key}' would end up in {destinations.Count} runs that are not "
            + "consecutive, which its directory entry cannot describe; rebuild the volume instead.");
    }

    // VBR doesn't change during defrag — no per-move re-init needed.
    DefragPlannerExecutor.Execute(archive, options, mover, moves, volumeSize,
      reinitAfterMove: null, metadataMover: mover);

    options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, volumeSize, null, "Defragmentation complete"));
  }

  /// <summary>
  /// Rebuild fallback for when the planner refuses. The volume is laid out
  /// straight into the stream: a byte[] tops out at two gigabytes, so building
  /// the image in memory threw on exactly the volumes that reach this path.
  /// </summary>
  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    ExFatWriter? writer = null;
    Stream? target = null;
    var spill = new List<string>();
    try {
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => {
          var r = new ExFatReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
        },
        beginWrite: s => { writer = new ExFatWriter(); target = s; },
        writeEntry: (name, data) => {
          var path = Path.GetTempFileName();
          spill.Add(path);
          File.WriteAllBytes(path, data);
          writer!.AddStreamingFile(name, data.LongLength, () => File.OpenRead(path));
        },
        // BuildToStreaming is the finaliser that pairs with AddStreamingFile —
        // BuildTo only writes entries added as byte arrays, so the volume came
        // back the right shape with every file's contents missing.
        finishWrite: () => writer!.BuildToStreaming(target!));
    } finally {
      foreach (var path in spill)
        try { File.Delete(path); } catch { /* scratch file already gone */ }
    }
  }
  public string DefaultExtension => ".img";
  public IReadOnlyList<string> Extensions => [".img", ".exfat"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("EXFAT   "u8.ToArray(), Offset: 3, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "exFAT filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ExFatReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  /// <summary>
  /// Opens a single filesystem entry as a bounded read-only stream. The
  /// reader produces the decoded file bytes by walking the entry's extent
  /// or block chain; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so cluster/extent slack past the entry's
  /// end is physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new ExFatReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

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

    var w = new ExFatWriter();
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      w.AddFile(input.ArchiveName, input.ReadContent());

    var specific = options.FormatSpecific;
    var sizeMB = ParseExFatImageSizeMB(specific?.GetValueOrDefault("ImageSize"));
    var clusterBytes = ParseExFatClusterSize(specific?.GetValueOrDefault("ClusterSize"));
    var volumeLabel = options.GetOption("VolumeLabel", "");

    // BuildTo keeps free space sparse, so an explicitly-sized volume costs only its
    // contents and is not bounded by what a byte[] can hold.
    if (sizeMB > 0 && output.CanSeek) {
      w.BuildTo(output, sizeMB, clusterBytes, volumeLabel);
      return;
    }

    // An auto-sized volume goes the same way. BuildAutoSized materialises the
    // whole thing as one byte[], so a payload past the array limit threw an
    // overflow computing its length instead of producing the volume.
    if (output.CanSeek) {
      w.BuildToStreaming(output, clusterBytes, volumeLabel);
      return;
    }

    var disk = sizeMB > 0
      ? w.Build(sizeMB, clusterBytes, volumeLabel)
      : w.BuildAutoSized(clusterBytes, volumeLabel);
    output.Write(disk);
  }

  /// <summary>
  /// Two-pass streaming creation: pre-known per-input sizes drive the
  /// cluster geometry in pass 1; pass 2 emits the boot region, FAT,
  /// allocation bitmap, up-case table and directory tree with empty file
  /// clusters, then streams each input's bytes from its
  /// <see cref="StreamingArchiveInput.OpenStream"/> factory into the
  /// pre-allocated cluster run via 64 KB chunks. Cluster tails past each
  /// entry's exact <c>Size</c> stay sparse-zero.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new ExFatWriter();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    var specific = options.FormatSpecific;
    var sizeMB = ParseExFatImageSizeMB(specific?.GetValueOrDefault("ImageSize"));
    var clusterBytes = ParseExFatClusterSize(specific?.GetValueOrDefault("ClusterSize"));
    var volumeLabel = options.GetOption("VolumeLabel", "");
    if (output.CanSeek && sizeMB <= 0) {
      // Auto-size streaming path: BuildToStreaming derives geometry from
      // the declared sizes and never buffers entry contents.
      w.BuildToStreaming(output, clusterBytes, volumeLabel);
      return;
    }
    // Fixed-image-size or non-seekable output: fall back to the buffered
    // path. The declared-size streaming inputs let the writer skip
    // per-entry byte[] materialisation; bytes still travel one entry at
    // a time inside ExFatWriter.BuildToStreaming or via the in-memory
    // disk byte[] for the fixed-size path.
    if (output.CanSeek) {
      w.BuildToStreaming(output, clusterBytes, volumeLabel);
      return;
    }
    // BuildTo keeps free space sparse, so an explicitly-sized volume costs only its
    // contents and is not bounded by what a byte[] can hold.
    if (sizeMB > 0 && output.CanSeek) {
      w.BuildTo(output, sizeMB, clusterBytes, volumeLabel);
      return;
    }

    var disk = sizeMB > 0
      ? w.Build(sizeMB, clusterBytes, volumeLabel)
      : w.BuildAutoSized(clusterBytes, volumeLabel);
    output.Write(disk);
  }

  private static int ParseExFatImageSizeMB(string? s) => s?.Trim() switch {
    "32 MB"  => 32,  "128 MB" => 128,  "256 MB" => 256,
    "512 MB" => 512, "1 GB"   => 1024, "2 GB"   => 2048,
    "4 GB"   => 4096,"16 GB"  => 16384,"32 GB"  => 32768,"128 GB" => 131072,
    _ => 0,
  };

  private static int ParseExFatClusterSize(string? s) => s?.Trim() switch {
    "4 KB"  => 4096,  "8 KB"  => 8192,  "16 KB" => 16384,
    "32 KB" => 32768, "64 KB" => 65536, "128 KB"=> 131072,
    _ => 0,
  };

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ExFatReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Adds (or replaces by name) files to an existing exFAT image. Uses
  /// <see cref="ExFatModifier"/> for true O(touched bytes) random-access I/O —
  /// only the FAT entries for new clusters, the allocation-bitmap byte(s) covering
  /// them, the root-directory cluster(s) holding the entry-set, the new file's
  /// data clusters, and the VBR PercentInUse byte are touched. The up-case table
  /// and all other files are never read.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FlatFiles(inputs)) {
      ExFatModifier.RemoveFile(archive, name, wipeData: true);
      ExFatModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes files from an existing exFAT image with full secure wipe (cluster
  /// bytes, FAT chain, allocation bitmap bits, directory entry set). Uses
  /// <see cref="ExFatModifier"/> for O(touched bytes) random-access I/O — no
  /// forensic recovery of the removed content is possible from the resulting bytes.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ExFatModifier.RemoveFile(archive, name, wipeData: true);
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


  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string FirstLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
