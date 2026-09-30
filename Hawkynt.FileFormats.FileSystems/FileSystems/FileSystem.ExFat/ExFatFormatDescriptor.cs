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
public sealed class ExFatFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // The optimization adapters are keyed on this descriptor's runtime type, so the
  // registration has to have run before any instance can be looked up. Doing it from
  // the type initializer gives exactly that guarantee without a module initializer.
  static ExFatFormatDescriptor() => ExFatSharedDataOptimization.Register();

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

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "ExFat";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "exFAT";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
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

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware exFAT defragmentor, planner-driven and in place. A layout the
  /// planner cannot reach in place is refused before anything moves.
  /// </summary>
  /// <remarks>
  /// The rebuild that used to follow every refusal wrote a volume at another size
  /// with no label, a new serial, fresh timestamps, plain attributes and no empty
  /// folders.
  /// </remarks>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    DefragSupport.Require(options, DefragFeature.Packing | DefragFeature.CarveHole | DefragFeature.MetadataZone, "exFAT");
    DefragmentWithPlanner(archive, options);
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
    var moves = DefragPlanner.PlanOrRefuse("exFAT", () => DefragPlanner.Plan(extents, mover.FirstDataByte, volumeSize, mover.ClusterSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement, movableMetadata: mover.RelocatableMetadata,
      allowMemoryStaging: mover.SupportsHeldRuns));
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, volumeSize, extents, "Already defragmented"));
      return;
    }

    // A file whose packed runs are not consecutive keeps a FAT chain: the mover
    // clears its NoFatChain flag and links the clusters in order.

    // VBR doesn't change during defrag — no per-move re-init needed.
    // The layout is what gives each file's clusters their chain order when they
    // are relinked; without it the order is the order the moves happened in, and a
    // fragmented file came back with its runs swapped.
    DefragPlannerExecutor.Execute(archive, options, mover, moves, volumeSize,
      reinitAfterMove: null, metadataMover: mover, layout: extents);

    options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, volumeSize, null, "Defragmentation complete"));
  }

  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".img";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".img", ".exfat"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("EXFAT   "u8.ToArray(), Offset: 3, Confidence: 0.90)];
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
  public string Description => "exFAT filesystem image";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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
  /// <remarks>
  /// Only the root directory is edited in place: a path into a folder is refused
  /// rather than flattened to its leaf name. All inputs are applied to a working
  /// copy and committed together, so a replace that cannot be finished leaves the
  /// old file in place.
  /// </remarks>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      if (input.ArchiveName.Replace('\\', '/').Trim('/').Contains('/'))
        throw new NotSupportedException(
          $"exFAT: '{input.ArchiveName}' is inside a folder; only the root directory is edited in place.");
    using var work = Stage(archive);
    foreach (var (name, data) in FlatFiles(inputs)) {
      ExFatModifier.RemoveFile(work, name, wipeData: true);
      ExFatModifier.AddFile(work, name, data);
    }
    Commit(archive, work);
  }

  /// <summary>A working copy of the volume the edit is applied to before it is committed.</summary>
  private static MemoryStream Stage(Stream archive) {
    if (archive.Length > Array.MaxLength)
      throw new NotSupportedException(
        $"exFAT: in-place editing stages the volume in memory; a {archive.Length:N0}-byte volume is refused.");
    var work = new MemoryStream((int)archive.Length);
    archive.Position = 0;
    archive.CopyTo(work);
    work.Position = 0;
    return work;
  }

  private static void Commit(Stream archive, MemoryStream work) {
    archive.Position = 0;
    work.Position = 0;
    work.CopyTo(archive);
    archive.SetLength(work.Length);
  }

  /// <summary>
  /// Removes files from an existing exFAT image with full secure wipe (cluster
  /// bytes, FAT chain, allocation bitmap bits, directory entry set). Uses
  /// <see cref="ExFatModifier"/> for O(touched bytes) random-access I/O — no
  /// forensic recovery of the removed content is possible from the resulting bytes.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    using var work = Stage(archive);
    foreach (var raw in entryNames ?? []) {
      var name = raw.Replace('\\', '/').Trim('/');
      if (name.Contains('/'))
        throw new NotSupportedException($"exFAT: '{raw}' is inside a folder; only the root directory is edited in place.");
      if (!ExFatModifier.RemoveFile(work, name, wipeData: true))
        throw new FileNotFoundException($"exFAT: '{raw}' is not in the root directory.", raw);
    }
    Commit(archive, work);
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
