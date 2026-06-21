#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ExFat;

public sealed class ExFatFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {

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
      } catch {
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

    var moves = DefragPlanner.Plan(extents, mover.FirstDataByte, volumeSize, mover.ClusterSize, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, volumeSize, extents, "Already defragmented"));
      return;
    }

    // VBR doesn't change during defrag — no per-move re-init needed.
    DefragPlannerExecutor.Execute(archive, options, mover, moves, volumeSize, reinitAfterMove: null);

    options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, volumeSize, null, "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    var sizeMB = (int)System.Math.Max(8, (archive.Length + 1024 * 1024 - 1) / (1024 * 1024));
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ExFatReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new ExFatWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build(sizeMB);
      });
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
    var w = new ExFatWriter();
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      w.AddFile(input.ArchiveName, input.ReadContent());

    var specific = options.FormatSpecific;
    var sizeMB = ParseExFatImageSizeMB(specific?.GetValueOrDefault("ImageSize"));
    var clusterBytes = ParseExFatClusterSize(specific?.GetValueOrDefault("ClusterSize"));
    var volumeLabel = options.GetOption("VolumeLabel", "");

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
}
