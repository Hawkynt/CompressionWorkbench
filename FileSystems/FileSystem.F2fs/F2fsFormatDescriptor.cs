#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.F2fs;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.kernel.org/filesystems/f2fs.html</c> — Linux kernel F2FS documentation (on-disk layout: SB/CP/SIT/NAT/SSA/main area)</description></item>
///   <item><description><c>https://www.usenix.org/conference/fast15/technical-sessions/presentation/lee</c> — Lee et al., "F2FS: A New File System for Flash Storage" (USENIX FAST '15), the design paper</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/F2FS</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class F2fsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable , IFilesystemExtentMap, IWipeEmpty {

  // A F2FS segment is 2 MiB; image size in bytes = segment count × 2 MiB.
  private const long SegmentSizeBytes = 2L * 1024 * 1024;

  // ── IFormatOptionsSchema ────────────────────────────────────────────────
  // Image-size presets all map to a segment count (MB / 2 = segments). The smallest
  // offered preset (64 MB = 32 segments) is well above the writer's 16-segment floor.
  /// <summary>
  /// Gets the options schema.
  /// </summary>
public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.ImageSize(["64 MB", "128 MB", "256 MB", "512 MB", "1 GB", "2 GB"]),
    FilesystemSchemaPresets.VolumeLabel(16),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "F2fs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "F2FS";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".f2fs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".f2fs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x10, 0x20, 0xF5, 0xF2], Offset: 1024, Confidence: 0.95)];
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
  /// F2FS flash-friendly filesystem image — R/W via log-structured append.
  /// Add/Remove mutate in place: writes land in the open WARM_DATA/WARM_NODE
  /// current segments (no full image rebuild) and advance to fresh main-area
  /// segments of the right CURSEG_* type when the open one fills. On-disk NAT
  /// and SIT entries are always updated; the NAT/SIT journals in the compact
  /// summary block are mirrored when there is room and silently fall through
  /// to disk when full (the on-disk entry is authoritative — f2fs-tools
  /// treats the journal as overrides over disk). When the root inline-dentry
  /// region is full the directory is converted in place to a regular
  /// block-based dentry directory whose entries live in HOT_DATA blocks. The
  /// checkpoint version + CRC are advanced into the alternate pack so the
  /// prior pack stays as a roll-back. Genuinely out of scope: subdirectory
  /// creation, nested removal, growing the main-area segment count, and
  /// multi-level indirect inode trees.
  /// </summary>
  public string Description => "F2FS flash-friendly filesystem image (R/W via log-structured append; "
    + "full NAT/SIT block rewrite + regular dentry blocks on overflow)";

  // --- WORM write constraints ---
  // F2FS minimum image = ~30 MB in the real-world mkfs.f2fs tool; our writer emits 64 MB by
  // default. A file is capped by what the inode's node tree can address: 923 direct
  // addresses, two direct nodes, two indirect and one double-indirect.
  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the min total archive size.
  /// </summary>
public long? MinTotalArchiveSize => 64L * 1024 * 1024;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "F2FS filesystem image (nested directories, inline or hash-bucket dentries).";
  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = null; return true; }
    try {
      var length = input.InMemoryContent?.LongLength ?? new FileInfo(input.FullPath).Length;
      var maxBytes = F2fsWriter.MaxFileBlocks * 4096L;
      if (length > maxBytes) {
        reason = $"F2FS addresses at most {maxBytes} bytes per file through the inode's node tree.";
        return false;
      }
    } catch {
      // If we can't stat it, let Create fail with the real reason.
    }
    reason = null;
    return true;
  }

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new F2fsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new F2fsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
    }
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
    var r = new F2fsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      // The blocks are scattered across the node tree, so the entry is spooled to
      // scratch rather than windowed; the spill is deleted when the stream closes.
      var scratch = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
        FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
      var size = r.ExtractTo(e, scratch);
      scratch.Position = 0;
      return new Compression.Registry.Streaming.BoundedEntryStream(scratch, size, leaveOpen: false);
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
    var specific = options.FormatSpecific;
    var segments = ParseImageSizeSegments(specific?.GetValueOrDefault("ImageSize"));
    var label = specific?.GetValueOrDefault("VolumeLabel");

    var w = new F2fsWriter();
    w.SetVolumeLabel(label);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = input.ArchiveName;
      if (input.InMemoryContent is { } bytes) {
        w.AddFile(name, bytes);
        continue;
      }
      // Sized from disk and opened only while the block is being filled, so the
      // volume is bounded by the target rather than by what a byte[] can hold.
      var path = input.FullPath;
      w.AddStreamingFile(name, new FileInfo(path).Length, () => File.OpenRead(path));
    }

    if (output.CanSeek) {
      w.BuildToStreaming(output, segments);
      return;
    }

    var image = segments > 0 ? w.Build(segments) : w.BuildAutoSized();
    output.Write(image, 0, image.Length);
  }

  /// <summary>
  /// Two-pass streaming creation: pre-known per-input sizes drive the F2FS
  /// segment geometry in pass 1; pass 2 emits the metadata image with each
  /// file's WARM_DATA blocks left zero, then streams each input's bytes from
  /// its <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// factory into its first allocated data block via 64 KB chunks. The output is
  /// byte-identical to <see cref="Create"/> for the same inputs (F2FS has no
  /// per-block content checksum). Falls back to the buffered default when the
  /// target stream is not seekable.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var inputList = inputs.ToList();
    if (!output.CanSeek) {
      // Non-seekable target: fall back to the buffered default (two-pass needs seek).
      ((IArchiveCreatable)this).CreateFromStreams(output, inputList, options);
      return;
    }

    var specific = options.FormatSpecific;
    var segments = ParseImageSizeSegments(specific?.GetValueOrDefault("ImageSize"));
    var label = specific?.GetValueOrDefault("VolumeLabel");

    var w = new F2fsWriter();
    w.SetVolumeLabel(label);
    foreach (var input in inputList) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    w.BuildToStreaming(output, segments);
  }

  // Maps an image-size preset label to a F2FS segment count (2 MiB per segment).
  // "Auto (fit to files)" / unknown → 0, signalling BuildAutoSized().
  private static int ParseImageSizeSegments(string? s) => s?.Trim() switch {
    "64 MB"  => (int)(64L * 1024 * 1024 / SegmentSizeBytes),    // 32
    "128 MB" => (int)(128L * 1024 * 1024 / SegmentSizeBytes),   // 64
    "256 MB" => (int)(256L * 1024 * 1024 / SegmentSizeBytes),   // 128
    "512 MB" => (int)(512L * 1024 * 1024 / SegmentSizeBytes),   // 256
    "1 GB"   => (int)(1024L * 1024 * 1024 / SegmentSizeBytes),  // 512
    "2 GB"   => (int)(2L * 1024 * 1024 * 1024 / SegmentSizeBytes), // 1024
    _        => 0, // Auto (fit to files)
  };

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware F2FS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a fresh
  /// contiguous-from-start multi-segment image (SIT/NAT journals, checkpoint
  /// pack, inline-dentry root).
  /// </summary>
  /// <summary>
  /// Largest volume the in-place pass is offered for. Its guard holds a copy of
  /// the image to compare payloads across the pass.
  /// </summary>
  private const long PlannerImageCap = 256L * 1024 * 1024;

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    using var reader = new F2fsReader(stream, leaveOpen: true);
    return reader.Entries.Where(e => !e.IsDirectory).Select(reader.Extract).ToList();
  }

  /// <summary>Plans a layout inside the data region and moves the blocks into it.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new F2fsBlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var extents = this.EnumerateExtents(archive).ToList();
    var data = extents.Where(e => e.Kind == DefragBlockKind.Used).ToList();
    if (data.Count == 0) return;

    mover.FindDataRegion(archive, data.Select(e => e.Offset));
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    // The plan is bounded by the region rather than the volume: a segment
    // carries one type for everything in it.
    var within = extents
      .Where(e => e.Offset >= mover.FirstDataByte && e.Offset + e.Length <= mover.DataRegionEnd)
      .ToList();

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      within, mover.FirstDataByte, mover.DataRegionEnd, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      mover.DataRegionEnd, reinitAfterMove: null);

    // The segment table and the summary area are keyed by where a block sits,
    // so both move with it.
    mover.SettleSegmentTables(archive);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the volume out again. A block's
    // address is one field, and the two structures that record the same fact —
    // the segment information table's bitmaps and counts, and the summary area
    // that maps a block back to its owner — are brought along after the pass.
    //
    // A segment carries one type for everything in it, so a pass stays inside
    // the region already given over to file data; moving a data block into a
    // segment meant for nodes is what fsck would refuse.
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

    //
    // Buffering the rebuilt image would cap the volume at what a byte[] can
    // hold, so the packing modes stream: each entry is spilled to scratch and
    // the writer pulls it back while laying out the segments.
    // Every mode streams: end-pack and carve-hole order their entries from
    // scratch inside the rebuilder, so none of them has to fall back to the
    // buffered path that a volume past two gigabytes cannot use.
    {
      F2fsWriter? writer = null;
      Stream? target = null;
      var spill = new List<string>();
      try {
        DefragRebuilder.RebuildStreaming(archive, options,
          readEntries: ReadEntries,
          beginWrite: s => { writer = new F2fsWriter(); target = s; },
          writeEntry: (name, data) => {
            var path = Path.GetTempFileName();
            spill.Add(path);
            File.WriteAllBytes(path, data);
            writer!.AddStreamingFile(name, data.LongLength, () => File.OpenRead(path));
          },
          finishWrite: () => writer!.BuildToStreaming(target!));
      } finally {
        foreach (var path in spill)
          try { File.Delete(path); } catch { /* scratch file already gone */ }
      }
    }
  }

  // ── IArchiveModifiable (in-place log-structured mutation) ──────────────
  // F2FS is a log-structured FS: Add appends new data + node blocks to the
  // open WARM_DATA/WARM_NODE current segments, promotes a fresh free segment
  // of the right CURSEG_* type when the open one fills, updates on-disk
  // NAT/SIT (the source of truth), best-effort mirrors NAT/SIT updates in
  // the compact summary block's journals (skipping silently when full —
  // f2fs-tools treats the journal as overrides over on-disk), and stamps a
  // fresh checkpoint (version + CRC) into the older of the two CP packs so
  // the previous pack stays as a roll-back snapshot. When the root
  // inline-dentry region fills the directory is converted in place to a
  // regular block-based dentry directory and Adds continue against
  // HOT_DATA blocks. Remove clears the dentry (inline or regular block),
  // invalidates the NAT entry, clears SIT valid_map bits, and wipes the
  // inode + data block bytes.
  //
  // Scope: root-level files only. Subdirectory creation, nested removal,
  // multi-level indirect inode trees, and main-area-segment growth are
  // genuinely out of scope.

  /// <summary>
  /// Adds the supplied entry to the target container.
  /// </summary>
public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    if (!archive.CanSeek)
      throw new NotSupportedException("F2fs: Add requires a seekable stream.");

    var image = ReadAll(archive);
    var files = inputs.Where(i => !i.IsDirectory).Select(i => (i.ArchiveName, i.ReadContent())).ToList();
    // Replace-by-name: drop any existing entry of the same name first so an update
    // overwrites rather than leaving a duplicate directory entry.
    image = F2fsModifier.RemoveFiles(image, files.Select(f => f.ArchiveName).ToList());
    var updated = F2fsModifier.AddFiles(image, files);
    WriteAll(archive, updated);
  }

  /// <summary>
  /// Removes the specified entry from the target container.
  /// </summary>
public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);

    if (!archive.CanSeek)
      throw new NotSupportedException("F2fs: Remove requires a seekable stream.");

    var image = ReadAll(archive);
    var updated = F2fsModifier.RemoveFiles(image, entryNames);
    WriteAll(archive, updated);
  }

  private static byte[] ReadAll(Stream s) {
    s.Position = 0;
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static void WriteAll(Stream s, byte[] data) {
    s.Position = 0;
    s.Write(data, 0, data.Length);
    s.SetLength(data.Length);
    s.Position = 0;
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new F2fsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new F2fsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    // Auto-size: the default 32-segment image only fits a few megabytes, so a
    // rebuild of anything larger would run out of main-area segments.
    return w.BuildAutoSized();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// Everything below the main area — superblocks, checkpoints, SIT, NAT and
  /// SSA — is structure, and inside the main area each live file claims both its
  /// data blocks and the node blocks that address them. Blocks nothing claims
  /// still hold whatever was last written to them.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new F2fsReader(image);
      var blockSize = (long)reader.BlockSize;
      if (blockSize <= 0) return [];

      result.Add(new DefragBlockInfo(0, Math.Min(reader.MainAreaStart, image.Length),
        DefragBlockKind.MetadataReserved));

      // The root directory is in the main area but in no listing, so it is
      // claimed here — wiping it would take every name with it.
      foreach (var block in reader.RootBlocks()) {
        var offset = block * blockSize;
        if (offset < 0 || offset >= image.Length) continue;
        result.Add(new DefragBlockInfo(offset, Math.Min(blockSize, image.Length - offset),
          DefragBlockKind.MetadataReserved));
      }

      foreach (var entry in reader.Entries)
        foreach (var (block, isData) in reader.EnumerateBlocks(entry)) {
          var offset = block * blockSize;
          if (offset < 0 || offset >= image.Length) continue;
          var length = Math.Min(blockSize, image.Length - offset);
          result.Add(new DefragBlockInfo(offset, length,
            isData ? DefragBlockKind.Used : DefragBlockKind.MetadataReserved,
            isData ? entry.Name : null));
        }

      if (result.Count <= 1 && reader.Entries.Count > 0) return [];
    } catch {
      // A volume we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
  /// <summary>
  /// Performs the wipe unused space operation.
  /// </summary>
public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new F2fsReader(image);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var e in reader.Entries)
          if (!e.IsDirectory)
            sizes[e.Name] = reader.SizeOf(e);
        // Only a file that fits in one block maps to a single extent, so the
        // lookup is what tells the wiper where such a file really ends.
        fileSizeLookup = n => sizes.TryGetValue(n, out var v) && v < 4096 ? v : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length, wipeClusterTips, fileSizeLookup);
  }

}
