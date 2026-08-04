#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Zfs;

/// <summary>
/// Descriptor for ZFS pool images — four 256 KB vdev labels (NVList +
/// uberblock ring) around the pool data area; WORM pool writer + reader
/// round-trip.
///
/// References:
/// <list type="bullet">
///   <item><description>Sun Microsystems, "ZFS On-Disk Specification" (2006 draft) — vdev labels, uberblocks, DMU structures</description></item>
///   <item><description><c>https://github.com/openzfs/zfs</c> — OpenZFS — the maintained implementation</description></item>
///   <item><description><c>https://openzfs.github.io/openzfs-docs/</c> — OpenZFS documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/ZFS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
/// <summary>
/// How this pool is laid out again by moving.
/// </summary>
/// <remarks>
/// <para>A block is named by a device address inside a block pointer, and the
/// block pointer carries a Fletcher-4 over what it points at. Moving bytes
/// leaves that check good — the bytes do not change — and breaks every one
/// above it: the pointer sits in an indirect block whose own check sits in the
/// pointer above, up to the uberblock. So the addresses are written as the pass
/// goes and the checks are taken again from the bottom up once it is over.</para>
///
/// <para>The space maps are not an obstacle here. This writer sets
/// <c>metaslab_array</c> to zero, so a pool it produces has none, and nothing
/// but the pointers records where a block is.</para>
///
/// <para>What made this the longest of the walks is the path itself: the
/// uberblock, the meta object set, a dnode array, the dataset's own object set,
/// another dnode array, and then the file's indirect blocks. Every pointer
/// along it is written down once, which is what <see cref="ZfsLayout" /> is
/// for.</para>
/// </remarks>
public sealed class ZfsFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Knobs the WORM pool writer honours. <c>VolumeLabel</c> maps to the pool
  /// name written into the vdev-label NVList <c>name</c> field (and the vdev
  /// <c>path</c>), read back as <c>ZfsReader.PoolName</c>; <c>ImageSize</c> maps
  /// to the total pool image size and must be at least
  /// <see cref="MinTotalArchiveSize"/> (the four 256&#160;KB vdev labels plus a
  /// usable data area). The 512-byte sector size and Fletcher-4 checksum are
  /// fixed, so they are not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Pool name", Kind: FormatOptionKind.String, Default: "compworkbench",
      Description: "ZFS pool name stored in the vdev-label NVList."),
    FilesystemSchemaPresets.ImageSize(["64 MB", "128 MB", "256 MB"],
      description: "Total pool image size (at least 64 MB)."),
  ];

  public string Id => "Zfs";
  public string DisplayName => "ZFS";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a genuine in-place writer. Add tries copy-on-write in place (new blocks for
  // the changed path only, advance the uberblock) and falls back to a rebuild for the
  // shapes the in-place adder does not handle; Remove is rebuild-based. CanModify is
  // advertised because the in-place add genuinely mutates the image without re-laying
  // untouched data (verified by ZfsReader round-trip + the CoW-offset proof).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".zfs";
  public IReadOnlyList<string> Extensions => [".zfs", ".zpool"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // A ZFS vdev label carries no signature at offset 0 — the first 8 KB are a
  // VTOC pad. The uberblock array starts 128 KB in, and slot 0 begins with
  // UBERBLOCK_MAGIC (0x00bab10c, little-endian). Without this the detector has
  // nothing to go on and a pool image named ".img" falls through to FAT.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x0C, 0xB1, 0xBA, 0x00, 0x00, 0x00, 0x00, 0x00],
      Offset: ZfsConstants.UberblockArrayOffset, Confidence: 0.9),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "ZFS pool image — single-vdev, single-dataset, flat root directory (WORM writer). " +
    "Fletcher-4 checksums, NV_BIG_ENDIAN XDR label, pool version 28.";

  // Write constraints.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 64L * 1024 * 1024; // 64 MB minimum image size.
  public string AcceptedInputsDescription =>
    "ZFS pool image (WORM); flat root directory, no subdirectories, up to 14 files.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "Flat root only; no subdirectories."; return false; }
    // microzap fits ~14 entries in 1 KB — we don't have a count at CanAccept time, so
    // limit only per-entry here and let the writer throw if over 14.
    if (input.ArchiveName.Length >= 50) {
      reason = "File name exceeds microzap 49-char limit.";
      return false;
    }
    if (input.ArchiveName.Contains('/') || input.ArchiveName.Contains('\\')) {
      reason = "Flat root only; no path separators in names.";
      return false;
    }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new ZfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new ZfsWriter();
    var poolName = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(poolName))
      w.SetPoolName(poolName);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      if (i.InMemoryContent is { } bytes) {
        w.AddFile(i.ArchiveName, bytes);
        continue;
      }
      // Sized from disk and opened only while its records are being written, so
      // the pool is bounded by the target rather than by what a byte[] can hold.
      var path = i.FullPath;
      w.AddStreamingFile(i.ArchiveName, new FileInfo(path).Length, () => File.OpenRead(path));
    }

    long sizeBytes = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
    var needed = w.ComputeAutoSize();
    w.WriteTo(output, Math.Max(sizeBytes, needed));
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware ZFS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a fresh
  /// contiguous-from-start pool image with single-vdev, single-dataset,
  /// flat root, Fletcher-4 checksums, and NV_BIG_ENDIAN XDR labels.
  /// Image size is preserved from the original archive length so labels
  /// land at the expected start/end positions.
  /// </summary>
  /// <summary>
  /// Largest pool the in-place pass is offered for. Its guard holds a copy of
  /// the image to compare payloads across the pass.
  /// </summary>
  private const long PlannerImageCap = 512L * 1024 * 1024;

  /// <summary>
  /// Where the pool keeps its bytes: the four labels, the object sets, dnode
  /// arrays and indirect blocks as structure, and each file's data under its
  /// name.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    ZfsLayout.Layout? layout;
    try {
      layout = ZfsLayout.Read(image);
    } catch {
      // A pool we cannot walk claims nothing; wiping it would zero live data.
      yield break;
    }

    if (layout == null) yield break;

    foreach (var (offset, length) in layout.Structure.Distinct().OrderBy(s => s.Offset))
      yield return new DefragBlockInfo(offset, length, DefragBlockKind.MetadataReserved,
        "ZFS pool structure");

    foreach (var block in layout.DataBlocks.OrderBy(b => b.Offset))
      yield return new DefragBlockInfo(block.Offset, block.Length, DefragBlockKind.Used, block.Owner);
  }

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    using var reader = new ZfsReader(stream, leaveOpen: true);
    return reader.Entries.Select(reader.Extract).ToList();
  }

  /// <summary>Plans the new layout and moves the blocks into it, settling the checks after.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new ZfsBlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, archive.Length, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      archive.Length, reinitAfterMove: null);

    // A pointer's check covers the block it names, and that block holds the
    // pointers below it, so nothing can be settled until everything has moved.
    mover.SettleChecksums(archive);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  public void Defragment(Stream archive, DefragOptions options) {
    // Moving what is out of place beats writing the pool out again: a block
    // pointer holds the address, and the checks above it are taken again once
    // every block has landed.
    if (archive.CanSeek && archive.Length <= PlannerImageCap) {
      var planned = false;
      // The in-place pass is kept only if every payload still reads back: it
      // can refuse partway, and a rebuild is the honest answer when it does.
      DefragContentGuard.RunOrRebuild(archive,
        readContents: ReadPayloadsForGuard,
        inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }

    // ZFS labels live at fixed start + end positions, so keep the original
    // footprint. Capture it before the rebuild rewrites the archive.
    var originalSize = archive.Length;

    // Buffering the rebuilt pool would cap it at what a byte[] can hold, so the
    // packing modes stream: each entry is spilled to scratch and the writer pulls
    // it back while writing records.
    // Every mode streams: end-pack and carve-hole order their entries from
    // scratch inside the rebuilder, so none of them has to fall back to the
    // buffered path that a volume past two gigabytes cannot use.
    {
      ZfsWriter? writer = null;
      Stream? target = null;
      var spill = new List<string>();
      try {
        DefragRebuilder.RebuildStreaming(archive, options,
          readEntries: ReadEntries,
          beginWrite: s => { writer = new ZfsWriter(); target = s; },
          writeEntry: (name, data) => {
            var path = Path.GetTempFileName();
            spill.Add(path);
            File.WriteAllBytes(path, data);
            writer!.AddStreamingFile(name, data.LongLength, () => File.OpenRead(path));
          },
          finishWrite: () => writer!.WriteTo(target!, Math.Max(originalSize, writer.ComputeAutoSize())));
      } finally {
        foreach (var path in spill)
          try { File.Delete(path); } catch { /* scratch file already gone */ }
      }
    }
  }

  // ── IArchiveModifiable (genuine copy-on-write add, rebuild fallback) ────
  // Add tries a real in-place CoW add via ZfsModifier (new blocks only for the
  // changed path, then a new uberblock in the next label slot); for shapes the
  // in-place adder cannot do it falls back to the read-all/rebuild path the
  // defragmentor uses. Remove is rebuild-based.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

    var toAdd = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (i.ArchiveName, i.ReadContent()))
      .ToList();
    if (toAdd.Count == 0)
      return;
    ZfsModifier.AddOrReplace(archive, toAdd);
  }

  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    ZfsModifier.Remove(archive, entryNames);
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new ZfsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files, long imageSize) {
    var w = new ZfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms, imageSize);
    return ms.ToArray();
  }
}
