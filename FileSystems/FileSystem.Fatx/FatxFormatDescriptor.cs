#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Fatx;

/// <summary>
/// R/W descriptor for Microsoft Xbox / Xbox 360 FATX volumes.
/// Magic "FATX" at offset 0; 4 KiB superblock followed by FAT16/FAT32 table.
/// Read via <see cref="FatxReader"/>, create via <see cref="FatxWriter"/>,
/// mutate via <see cref="FatxModifier"/> (in-place Add/Remove on the root
/// directory; sub-directory mutation stays out of scope).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://xboxdevwiki.net/FATX</c> — Xbox Dev Wiki's FATX page, the de-facto community specification</description></item>
///   <item><description><c>https://github.com/mborgerson/fatx</c> — maintained open-source FATX implementation (fatxfs)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Design_of_the_FAT_file_system</c> — Wikipedia's FAT reference, which covers the FATX variant</description></item>
/// </list>
/// </summary>
public sealed class FatxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  /// <summary>
  /// Creation knobs surfaced by the Convert dialog / CLI. <c>SectorsPerCluster</c>
  /// is the FATX allocation unit (512-byte sectors): leave it at "auto" (0) to let
  /// the layout optimiser minimise file-tail slack for the actual file-set, or pin
  /// a power-of-two value. Real Xbox HDDs use 32 (16 KiB clusters).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("SectorsPerCluster", "Sectors per cluster", FormatOptionKind.Enum, "0",
      AllowedValues: ["0", "4", "8", "16", "32", "64", "128"],
      Description: "FATX cluster size in 512-byte sectors (0 = auto-optimise for least slack; 32 = 16 KiB Xbox default)."),
    new("VolumeId", "Volume ID", FormatOptionKind.String, "",
      Description: "32-bit volume identifier (hex or decimal). Blank = 0x12345678."),
  ];
  public string Id => "Fatx";
  public string DisplayName => "FATX (Xbox)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".fatx";
  public IReadOnlyList<string> Extensions => [".fatx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'F', (byte)'A', (byte)'T', (byte)'X'], Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Xbox/Xbox 360 FATX filesystem image (R/W: list/extract/create/add/remove at root; FAT16+FAT32 width-aware).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FatxReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FatxReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Streamed, not buffered: an entry may be larger than a byte[] can hold.
      using var target = CreateEntryFile(outputDir, e.Name);
      r.ExtractTo(e, target);
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new FatxReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Emits a fresh FATX volume containing <paramref name="inputs"/> via
  /// <see cref="FatxWriter"/>. Path components in <c>ArchiveName</c> become
  /// nested FATX subdirectories (one cluster chain per directory); files
  /// are stored contiguously starting at the next free cluster.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new FatxWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to plan the cluster chains; reading a large
      // input into a byte[] would cap the image at what an array can hold.
      if (info.InMemoryContent is { } bytes)
        w.AddFile(info.ArchiveName, bytes);
      else
        w.AddStreamingFile(info.ArchiveName, new FileInfo(info.FullPath).Length,
                           () => File.OpenRead(info.FullPath));
    }

    // Sectors-per-cluster: 0 (or unset) hands the choice to the writer's layout
    // optimiser; an explicit power-of-two value is honoured verbatim so pinned
    // sizes stay byte-identical.
    var spc = options.GetOptionInt("SectorsPerCluster", 0);
    var volIdStr = options.GetOption("VolumeId", "");
    var volumeId = 0x12345678u;
    if (!string.IsNullOrEmpty(volIdStr)) {
      var span = volIdStr.AsSpan();
      var hex = span.StartsWith("0x") || span.StartsWith("0X");
      if (hex
            ? uint.TryParse(span[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            : uint.TryParse(span, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed))
        volumeId = parsed;
    }

    if (output.CanSeek) {
      w.WriteTo(output, sectorsPerCluster: spc, volumeId: volumeId);
      return;
    }

    var image = w.Build(sectorsPerCluster: spc, volumeId: volumeId);
    output.Write(image);
  }

  /// <summary>
  /// In-place add: each input becomes a new dirent in the root cluster of the
  /// existing FATX image, with its bytes written into the first contiguous
  /// free cluster run found in the FAT. Sub-directory adds are not supported
  /// by v1 — only leaf filenames go to root. The FAT16/FAT32 width is
  /// auto-detected from the on-disk geometry.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier reads the volume into an array to walk its
    // structures, which a volume past two gigabytes does not fit in. Above that
    // the edit is applied by unpacking and relaying the volume out instead.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      FatxModifier.AddFile(image, input.ArchiveName, input.ReadContent());
    }
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  /// <summary>
  /// In-place remove: tombstones each named dirent (name_length = 0xE5) and
  /// frees + wipes every data cluster in the file's FAT chain. Unknown names
  /// are silently skipped (consistent with how WORM Extract treats them).
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var name in entryNames)
      FatxModifier.RemoveFile(image, name);
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  // ── ILayoutOptimizable ────────────────────────────────────────────────
  //
  // FATX is the canonical fit for this contract: the allocation unit
  // (sectors-per-cluster) is reader-agnostic, so any legal cluster size
  // round-trips, and a cluster-size change is purely a structural rebuild.
  // The per-file cluster-tail slack is exactly what the shared optimiser
  // minimises. PatchInPlace handles the metadata-only volume-id field; a
  // cluster-size change is routed to RebuildStreaming.

  /// <inheritdoc />
  public LayoutAnalysis AnalyzeLayout(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.CanSeek) image.Position = 0;
    var reader = new FatxReader(image);
    var fileSizes = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Size).ToList();
    var current = reader.ClusterSize;

    int[] candidates = [2048, 4096, 8192, 16384, 32768, 65536];
    var optimal = Compression.Core.Layout.LayoutOptimizerAdapter.SelectAllocationUnit(
      candidates,
      fileSizes,
      fixedOverhead: clusterBytes => {
        var dataClusters = Compression.Core.Layout.FilesystemLayoutOptimizer.DataClusters(fileSizes, clusterBytes);
        var entryBytes = dataClusters < 0xFFF4 ? 2L : 4L;
        return (((dataClusters + 2) * entryBytes) + 0xFFFL) & ~0xFFFL;
      });

    var currentSlack = Compression.Core.Layout.LayoutOptimizerAdapter.SlackAt(fileSizes, current);
    var optimalSlack = Compression.Core.Layout.LayoutOptimizerAdapter.SlackAt(fileSizes, optimal);
    return new LayoutAnalysis {
      ImageSize = image.CanSeek ? image.Length : 0,
      CurrentUnitSize = current,
      CurrentSlackBytes = currentSlack,
      OptimalUnitSize = optimal,
      OptimalSlackBytes = optimalSlack,
      InPlaceChanges = ["volume id"],
      RequiresRebuild = optimal != current ? ["cluster size"] : [],
      Notes = optimal == current
        ? ["Cluster size is already optimal for this file-set."]
        : [$"Rebuild at {optimal}-byte clusters saves {currentSlack - optimalSlack} slack bytes."],
    };
  }

  /// <inheritdoc />
  public void PatchInPlace(Stream image, LayoutPatch patch) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(patch);
    if (patch.SerialNumber is { } serial) {
      // FATX volume_id lives at superblock offset 0x04 (little-endian u32).
      Span<byte> buf = stackalloc byte[4];
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, serial);
      image.Position = 0x04;
      image.Write(buf);
    }
    // FATX carries no on-disk volume label, so VolumeLabel is a no-op here.
  }

  /// <inheritdoc />
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);
    if (source.CanSeek) source.Position = 0;
    var reader = new FatxReader(source);
    var w = new FatxWriter();
    // Each entry is spilled to scratch and pulled back while the volume is laid
    // out, so neither the file nor the image has to be held in memory — the
    // buffered form of this refused any volume past the array limit.
    var spill = new List<string>();
    try {
      foreach (var e in reader.Entries) {
        if (e.IsDirectory) continue;
        var path = Path.GetTempFileName();
        spill.Add(path);
        File.WriteAllBytes(path, reader.Extract(e));
        var captured = path;
        w.AddStreamingFile(e.Name, new FileInfo(captured).Length, () => File.OpenRead(captured));
      }

      // UnitSize 0 = auto-optimise; an explicit byte size maps to sectors-per-cluster.
      var spc = options.UnitSize > 0 ? options.UnitSize / FatxReader.SectorSize : 0;
      if (target.CanSeek) {
        w.WriteTo(target, sectorsPerCluster: spc);
      } else {
        var image = w.Build(sectorsPerCluster: spc);
        target.Write(image);
      }
      options.OnProgress?.Invoke(target.Length, target.Length);
    } finally {
      foreach (var path in spill)
        try { File.Delete(path); } catch { /* scratch file already gone */ }
    }
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// Walks the FATX chain of every live entry: the superblock and the FAT are
  /// structure, each cluster run is the file that owns it, and whatever no
  /// chain reaches is free.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new FatxReader(image);
      result.Add(new DefragBlockInfo(0, reader.DataRegionStart, DefragBlockKind.MetadataReserved));

      // The root directory has no entry of its own, so its chain is walked
      // first — wiping it would take every file's name with it.
      var rootChain = reader.RootDirCluster;
      var rootSeen = new HashSet<uint>();
      while (rootChain >= 1 && !reader.IsEoc(rootChain) && rootSeen.Add(rootChain)) {
        var rootOffset = reader.ClusterOffset(rootChain);
        if (rootOffset < 0 || rootOffset >= image.Length) break;
        result.Add(new DefragBlockInfo(rootOffset,
          Math.Min(reader.ClusterSize, image.Length - rootOffset), DefragBlockKind.MetadataReserved));
        rootChain = reader.GetNextCluster(rootChain);
      }

      foreach (var entry in reader.Entries) {
        var cluster = entry.FirstCluster;
        var remaining = entry.IsDirectory ? long.MaxValue : entry.Size;
        var seen = new HashSet<uint>();
        while (cluster >= 1 && !reader.IsEoc(cluster) && seen.Add(cluster) && remaining > 0) {
          var offset = reader.ClusterOffset(cluster);
          if (offset < 0 || offset >= image.Length) break;
          var length = Math.Min(reader.ClusterSize, image.Length - offset);
          if (length <= 0) break;
          result.Add(new DefragBlockInfo(offset, length,
            entry.IsDirectory ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used,
            entry.IsDirectory ? null : entry.Name));
          if (!entry.IsDirectory) remaining -= length;
          cluster = reader.GetNextCluster(cluster);
        }
      }
    } catch {
      // An image we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;

    // A file's last cluster is only partly its own; the tip is slack.
    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new FatxReader(image);
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in reader.Entries)
          if (!e.IsDirectory)
            sizes[e.Name] = e.Size;
        fileSizeLookup = n => sizes.TryGetValue(n, out var v) ? v : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    // Cluster tips span the whole chain, not one extent, so they are left to
    // the per-extent lookup only when a file fits inside a single cluster.
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: fileSizeLookup);
  }


  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  /// <inheritdoc />
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Moves only the files that are out of place, relinking each chain and
  /// repointing its directory record as the clusters arrive. A rebuild would
  /// read and rewrite every file to fix a handful of runs; this rewrites the
  /// allocation table entries and one field per file instead.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // The in-place pass is kept only if every payload still reads back. It can
    // refuse partway — a layout it cannot order, a record it cannot find — and
    // a rebuild is the honest answer when it does, rather than the exception
    // this used to hand the caller.
    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => ReadFileEntries(stream).Select(e => e.Data).ToList(),
      inPlace: () => this.DefragmentWithPlanner(archive, options),
      rebuild: () => DefragRebuilder.Rebuild(archive, options,
        readEntries: stream => ReadFileEntries(stream),
        buildImage: files => {
          var writer = new FatxWriter();
          foreach (var (name, data) in files) writer.AddFile(name, data);
          var built = writer.Build();
          if (built.Length >= archive.Length) return built;
          var padded = new byte[archive.Length];
          Array.Copy(built, padded, built.Length);
          return padded;
        }));
  }

  /// <summary>Every file's name and bytes, for the rebuild and the guard.</summary>
  private static List<(string Name, byte[] Data)> ReadFileEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    var reader = new FatxReader(stream);
    return reader.Entries.Where(e => !e.IsDirectory)
                         .Select(e => (e.Name, reader.Extract(e))).ToList();
  }

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new FatxBlockMover();
    mover.Init(archive);

    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    // A file in more than one piece has its whole chain restated in one call
    // once every cluster has landed, which the mover now offers; this used to
    // refuse such volumes outright, so a fragmented FATX volume could not be
    // defragmented at all.

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, archive.Length, mover.ClusterSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      archive.Length, reinitAfterMove: null);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

}
