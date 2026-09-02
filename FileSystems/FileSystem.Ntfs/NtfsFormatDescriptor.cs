#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ntfs;

/// <summary>
/// Descriptor for Microsoft NTFS volume images ("NTFS    " boot-sector OEM
/// magic; $MFT-based metadata) with create, in-place modify and defragment
/// support.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://flatcap.github.io/linux-ntfs/ntfs/</c> — Linux-NTFS project on-disk structure documentation — the de-facto public NTFS spec</description></item>
///   <item><description><c>https://github.com/tuxera/ntfs-3g</c> — maintained open-source implementation</description></item>
///   <item><description><c>https://learn.microsoft.com/en-us/windows-server/storage/file-server/ntfs-overview</c> — Microsoft's NTFS overview</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/NTFS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class NtfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// NTFS creation knobs surfaced by the Convert Archive dialog / CLI: image
  /// size (Auto + fixed presets), volume label (capped at 32 chars to match
  /// $VOLUME_NAME), cluster size, MFT record size and the 8.3 short-name
  /// toggle. Cluster + MFT record size cooperate via
  /// <see cref="NtfsWriter.BuildAutoSized"/> when both are on Auto. The MFT
  /// reserve % knob (stash) is not honoured by the upstream writer yet —
  /// see Build()'s constant MFT zone — so it's not published here.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.ImageSize(
      ["16 MB", "64 MB", "256 MB", "1 GB", "4 GB", "16 GB"]),
    FilesystemSchemaPresets.VolumeLabel(maxChars: 32),
    FilesystemSchemaPresets.ClusterSize(min: 4096,
      description: "NTFS allocation unit size. Auto picks the size that minimises slack + MFT-zone overhead."),
    FilesystemSchemaPresets.PowerOfTwoSize(
      "MftRecordSize", "MFT record size", 512, 4096, "Auto",
      "Size of each $MFT file record. Smaller records pack tighter for many tiny files; larger records keep more attributes resident. Auto co-optimises with cluster size."),
    new FormatOptionDescriptor(
      Key: "Generate8Dot3",
      DisplayName: "Generate 8.3 short names",
      Kind: FormatOptionKind.Boolean,
      Default: "true",
      Description: "Records each $FILE_NAME in the Win32&DOS namespace so the long name doubles as an 8.3 short name (Windows default). " +
        "Disable to suppress DOS short names (Win32-only names), the equivalent of 'fsutil behavior set disable8dot3'."),
    new FormatOptionDescriptor(
      Key: "Compression",
      DisplayName: "File compression",
      Kind: FormatOptionKind.Enum,
      Default: "Off",
      AllowedValues: ["Off", "LZNT1"],
      Description: "Stores each non-resident file's $DATA as an NTFS LZNT1 compressed attribute " +
        "(16-cluster compression units, the 0x0001 compressed flag, sparse runs for saved clusters). " +
        "Resident files (≤ ~700 bytes) are never compressed. Off stores files uncompressed (default)."),
    new FormatOptionDescriptor(
      Key: "NtfsVersion",
      DisplayName: "NTFS version",
      Kind: FormatOptionKind.Enum,
      Default: "3.1",
      AllowedValues: ["3.1", "3.0"],
      Description: "Volume version stamped into $VOLUME_INFORMATION. 3.1 (Windows XP and later) is the modern default; " +
        "3.0 marks the volume as a Windows 2000-era NTFS volume."),
  ];

  /// <summary>
  /// Walks the boot sector + $MFT + each MFT record's $DATA attribute and
  /// yields one extent per data run. Records 0-15 (the reserved system
  /// files: $MFT, $MFTMirr, $LogFile, $Volume, $AttrDef, root, $Bitmap,
  /// $Boot, $BadClus, $Secure, $UpCase, $Extend) surface as
  /// MetadataReserved; regular files surface as Used. Adjacent runs are
  /// coalesced.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => NtfsExtentMap.Enumerate(image);

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Ntfs";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "NTFS";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable filesystem. Add/Remove produce a valid modified image; the
  // implementation re-packs the volume, so existing data may move — acceptable for
  // a conceptually read-write container. See FormatCapabilities.cs (WORM vs R/W).
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Zeros all unused space in the NTFS image: unallocated clusters, the slack
  /// between a non-resident file's logical size and the end of its last
  /// allocated cluster (the cluster tip), and any region not claimed by a live
  /// extent. Resident files (≤ 700 bytes) live inside their MFT record and own
  /// no data cluster, so they have no cluster tip — those are left untouched.
  /// Cluster-tip wiping is applied only to files whose <c>$DATA</c> is a single
  /// contiguous run; a fragmented file's tip lives in its final run only, which
  /// the coalesced extent map cannot pinpoint per-extent, so such files are
  /// omitted from the tip pass to avoid clobbering live clusters.
  /// </summary>
  /// <summary>
  /// Above this a file cannot live inside its MFT record, so the block map has
  /// to name it. One kilobyte is the record size this writer uses.
  /// </summary>
  private const long ResidentCeilingBytes = 1024;

    /// <summary>
  /// Performs the wipe unused space operation.
  /// </summary>
public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    image.Position = 0;
    var extents = NtfsExtentMap.Enumerate(image).ToList();

    // Do not wipe against a map that demonstrably does not cover the volume.
    // Everything the map does not claim is treated as free and zeroed, so a file
    // the map has not seen is a file the wipe erases — and it erases it without
    // complaint, leaving an entry of the right length over zeroed clusters.
    //
    // A file smaller than an MFT record lives inside that record and rightly has
    // no extent of its own; its bytes are inside metadata the map does claim. A
    // file of a cluster or more cannot be resident, so if the map does not name
    // it, the map is incomplete and nothing it says about free space can be
    // relied on. Two files of three and a half kilobytes were zeroed exactly
    // this way.
    try {
      var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var ex in extents)
        if (ex.Kind == DefragBlockKind.Used && ex.FileName != null)
          named.Add(Path.GetFileName(ex.FileName));

      image.Position = 0;
      var reader = new NtfsReader(image);
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory || entry.Size < ResidentCeilingBytes) continue;
        if (!named.Contains(Path.GetFileName(entry.Name)))
          return 0;
      }
    } catch {
      return 0;   // the volume could not be read back; wiping it is not safe either
    }

    image.Position = 0;
    extents = NtfsExtentMap.Enumerate(image).ToList();

    // Build a cluster-tip lookup keyed by the extent-map file name (the MFT
    // record's $FILE_NAME leaf). Only single-run files are eligible: the
    // generic wiper trims each extent's tail using the file's logical size, so
    // a multi-run file would have its tip mis-attributed to the wrong run.
    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        var usedExtentCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ex in extents)
          if (ex.Kind == DefragBlockKind.Used && ex.FileName != null)
            usedExtentCount[ex.FileName] = usedExtentCount.GetValueOrDefault(ex.FileName) + 1;

        image.Position = 0;
        using var reader = new NtfsReader(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries) {
          if (entry.IsDirectory) continue;
          // The extent map keys regular files by their leaf $FILE_NAME; the
          // reader surfaces the full path. Re-key to the leaf to line them up.
          var leaf = entry.Name.Contains('/') ? entry.Name[(entry.Name.LastIndexOf('/') + 1)..] : entry.Name;
          if (usedExtentCount.GetValueOrDefault(leaf) == 1)
            sizeMap[leaf] = entry.Size;
        }
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
    /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new NtfsBlockMover();
    mover.Init(image); // reads only the boot sector + MFT record 0
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new NtfsBlockMover();
    mover.Init(image); // reads only the boot sector + MFT record 0
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  // ── IArchiveShrinkable: genuine in-place shrink ─────────────────────────

  /// <summary>
  /// Genuine in-place NTFS shrink: relocates only the clusters above the auto-fit
  /// boundary into free space below it via <see cref="NtfsInPlaceShrinker"/>, trims
  /// $Bitmap/$Boot, and emits the smaller image. Falls back to the
  /// <see cref="IArchiveShrinkable"/> default (verified rebuild / copy-through) when
  /// the in-place path cannot handle the image (e.g. a compressed stream would need
  /// relocation).
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    // The in-place shrinker works on a byte[] of the whole volume, so it is only
    // reachable for volumes that fit in one. Larger ones go straight to the rebuild
    // path below, which streams both halves.
    try {
      if (!input.CanSeek || input.Length > MaxBufferedImageBytes)
        throw new NotSupportedException("volume too large for the in-place shrinker");

      input.Position = 0;
      using var ms = new MemoryStream();
      input.CopyTo(ms);
      var image = ms.ToArray();

      var result = NtfsInPlaceShrinker.ShrinkToFit(image);
      if (result.WasReduced) {
        output.Position = 0;
        output.SetLength(0);
        output.Write(image, 0, (int)result.NewSize);
        return;
      }
    } catch (NotSupportedException) {
      // fall through to the rebuild/copy-through default
    } catch (InvalidDataException) {
      // not an NTFS image we can parse in place; fall through
    } catch (IOException) {
      // buffering the volume failed (too long for a MemoryStream); fall through
    }

    // Default behaviour: verified rebuild that never grows/corrupts, else copy-through.
    ((IArchiveShrinkable)this).ShrinkDefault(input, output);
  }

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware NTFS defragmentor. Supports planner-driven in-place path
  /// (using <see cref="DefragPlanner"/> + <see cref="NtfsBlockMover"/>) and the
  /// legacy rebuild path (using <see cref="DefragRebuilder"/>). Falls back to
  /// rebuild when the planner path throws (e.g. data-run re-encoding changes
  /// byte length with no slack space).
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy) {
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
    var mover = new NtfsBlockMover();
    mover.Init(archive); // reads only the boot sector + MFT record 0

    // Stream the extent map directly off the archive — no whole-image load.
    var extents = NtfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent("scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    // Data starts after the metadata that sits at the front of the volume — and
    // only that. NTFS scatters system files ($MFTMirr near the middle, $AttrDef
    // at the tail), so taking the end of the last one put the origin a few
    // megabytes short of the image end; every file was then planned into the
    // same impossible destination past the end of the volume, and executing
    // that left them the right length with each other's bytes. The planner is
    // told about the scattered records separately — they arrive as forbidden
    // regions in the extent list, which is what keeps data off them.
    long dataOrigin = mover.FirstDataByte;
    foreach (var e in extents.Where(e => e.Kind == DefragBlockKind.MetadataReserved)
                             .OrderBy(e => e.Offset)) {
      if (e.Offset > dataOrigin) break;   // a gap: the leading metadata ended here
      var end = e.Offset + e.Length;
      if (end > dataOrigin) dataOrigin = end;
    }
    // Align to cluster boundary.
    var cs = mover.ClusterSize;
    dataOrigin = (dataOrigin + cs - 1) / cs * cs;

    // The volume's own structures are offered to the planner as owners: their
    // position is recorded in the boot sector or in their MFT record, both of
    // which the mover rewrites. A layout that asks for metadata at the front
    // can then actually move the MFT there instead of planning around it.
    // The volume ends before the image does — the last sector is the boot sector's
    // backup — so that, and not the file's length, is what bounds a layout.
    var volumeEnd = mover.VolumeEndByte > 0 ? Math.Min(mover.VolumeEndByte, archive.Length) : archive.Length;

    var moves = DefragPlanner.Plan(extents, dataOrigin, volumeEnd, mover.ClusterSize,
      options.Profile, options.Mode, metadataZone: options.MetadataZonePlacement,
      movableMetadata: mover.RelocatableMetadata);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    // After each move, re-init the mover by re-reading only the boot sector +
    // record 0 from the now-mutated stream — no whole-image load.
    DefragPlannerExecutor.Execute(archive, options, mover, moves, volumeEnd, () => {
      archive.Position = 0;
      mover.Init(archive);
    }, metadataMover: mover);

    archive.Position = 0;
    var postExtents = NtfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// Rebuild fallback for when the planner refuses. The volume is laid out
  /// straight into the stream: a byte[] tops out at two gigabytes, so building
  /// the image in memory threw on exactly the volumes that reach this path.
  /// </summary>
  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    var totalSize = archive.Length;
    NtfsWriter? writer = null;
    Stream? target = null;
    var spill = new List<string>();
    try {
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => {
          var r = new NtfsReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
        },
        beginWrite: s => { writer = new NtfsWriter(); target = s; },
        writeEntry: (name, data) => {
          var path = Path.GetTempFileName();
          spill.Add(path);
          File.WriteAllBytes(path, data);
          writer!.AddStreamingFile(name, data.LongLength, () => File.OpenRead(path));
        },
        finishWrite: () => writer!.BuildToStreaming(target!, totalSize));
    } finally {
      foreach (var path in spill)
        try { File.Delete(path); } catch { /* scratch file already gone */ }
    }
  }
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".ntfs";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".ntfs", ".img"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'N', (byte)'T', (byte)'F', (byte)'S', (byte)' ', (byte)' ', (byte)' ', (byte)' '], Offset: 3, Confidence: 0.90)
  ];
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
  /// NTFS filesystem image with LZNT1 compression support. The writer emits
  /// every reserved system MFT record (0-15) with real content: $MFT,
  /// $MFTMirr, $LogFile, $Volume (with a version-3.1 $VOLUME_INFORMATION
  /// and a $VOLUME_NAME), $AttrDef, root ., $Bitmap, $Boot, $BadClus,
  /// $Secure, $UpCase (128 KiB UTF-16 table), and $Extend. Every record
  /// carries $STANDARD_INFORMATION and $FILE_NAME, the Update Sequence
  /// Array (USA) fixup is applied at sector boundaries, and the on-disk
  /// cluster bitmap reflects actual allocations.
  /// </summary>
  public string Description => "NTFS filesystem image with LZNT1 compression and full $MFT system files";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new NtfsReader(stream);
    var entries = r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified,
      Kind: null, IsSymlink: e.IsSymlink, LinkTarget: e.LinkTarget
    )).ToList();
    return SymlinkResolver.Resolve(entries);
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
    var r = new NtfsReader(archive);
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

    var specific = options.FormatSpecific;
    var label = specific?.GetValueOrDefault("VolumeLabel");
    // 8.3 short-name generation defaults on (matches a freshly formatted Windows
    // volume); only an explicit "false" suppresses the DOS short name.
    var generateShortNames = specific?.GetValueOrDefault("Generate8Dot3") != "false";
    var w = string.IsNullOrEmpty(label)
      ? new NtfsWriter(generateShortNames: generateShortNames)
      : new NtfsWriter(label, generateShortNames);
    ApplyWriterOptions(w, specific);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);

    var totalSize     = ParseImageSizeBytes(specific?.GetValueOrDefault("ImageSize"));
    var clusterSize   = FilesystemSchemaPresets.ParseSize(specific?.GetValueOrDefault("ClusterSize"));
    var mftRecordSize = FilesystemSchemaPresets.ParseSize(specific?.GetValueOrDefault("MftRecordSize"));

    // Explicit image size → fixed Build with whatever cluster/MFT sizes were
    // requested (falling back to the writer defaults when 0). Auto image size →
    // BuildAutoSized, which co-optimises cluster + MFT record size for the
    // payload (honouring any explicit cluster/MFT request).
    // BuildTo keeps free space sparse, so an explicitly-sized volume costs only its
    // contents and is not bounded by what a byte[] can hold.
    if (totalSize > 0 && output.CanSeek) {
      w.BuildTo(output, totalSize,
                clusterSize   > 0 ? clusterSize   : 4096,
                mftRecordSize > 0 ? mftRecordSize : 1024);
      return;
    }

    // An auto-sized volume goes the same way. BuildAutoSized materialises the
    // whole thing as one byte[], so a payload past the array limit threw an
    // overflow computing its length instead of producing the volume.
    if (output.CanSeek) {
      w.BuildToStreamingAutoSized(output);
      return;
    }

    var disk = totalSize > 0
      ? w.Build((int)totalSize,
                clusterSize   > 0 ? clusterSize   : 4096,
                mftRecordSize > 0 ? mftRecordSize : 1024)
      : w.BuildAutoSized(clusterSize, mftRecordSize);
    output.Write(disk);
  }

  /// <summary>
  /// Two-pass streaming creation: pre-known per-input sizes drive MFT-record
  /// + cluster geometry in pass 1; pass 2 emits all reserved system MFT
  /// records + per-user MFT records (with single-run non-resident $DATA for
  /// large files), then streams each non-resident entry's bytes from its
  /// <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// factory into its allocated cluster run via 64 KB chunks. Cluster tail
  /// past each entry's exact <c>Size</c> stays sparse-zero. Resident files
  /// (≤ 700 bytes) buffer their bounded source bytes inline in the MFT
  /// record — the bound itself caps anything past <c>Size</c>.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var specific = options.FormatSpecific;
    var label = specific?.GetValueOrDefault("VolumeLabel");
    var generateShortNames = specific?.GetValueOrDefault("Generate8Dot3") != "false";
    var w = string.IsNullOrEmpty(label)
      ? new NtfsWriter(generateShortNames: generateShortNames)
      : new NtfsWriter(label, generateShortNames);
    // Streaming entries keep the single-run uncompressed layout; only the NTFS
    // version knob applies on this path (LZNT1 compression is not wired into the
    // streaming writer).
    ApplyWriterOptions(w, specific);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    var totalSize     = ParseImageSizeBytes(specific?.GetValueOrDefault("ImageSize"));
    var clusterSize   = FilesystemSchemaPresets.ParseSize(specific?.GetValueOrDefault("ClusterSize"));
    var mftRecordSize = FilesystemSchemaPresets.ParseSize(specific?.GetValueOrDefault("MftRecordSize"));
    if (output.CanSeek) {
      if (totalSize > 0)
        w.BuildToStreaming(output, totalSize);
      else
        w.BuildToStreamingAutoSized(output);
      return;
    }
    var disk = totalSize > 0
      ? w.Build((int)totalSize,
                clusterSize   > 0 ? clusterSize   : 4096,
                mftRecordSize > 0 ? mftRecordSize : 1024)
      : w.BuildAutoSized(clusterSize, mftRecordSize);
    output.Write(disk);
  }

  // Applies the create-glue knobs that the writer can honour for both the
  // in-memory and streaming build paths: LZNT1 compression (in-memory only —
  // a no-op on streaming entries, which the writer leaves uncompressed) and the
  // NTFS minor version stamped into $VOLUME_INFORMATION.
  private static void ApplyWriterOptions(NtfsWriter w, IReadOnlyDictionary<string, string>? specific) {
    if (specific == null) return;
    if (string.Equals(specific.GetValueOrDefault("Compression"), "LZNT1", StringComparison.OrdinalIgnoreCase))
      w.SetCompression(true);
    if (specific.GetValueOrDefault("NtfsVersion")?.Trim() == "3.0")
      w.SetNtfsMinorVersion(0);
  }

  // Parses the NTFS image-size labels ("16 MB".."16 GB"); "Auto …" → 0.
  private static long ParseImageSizeBytes(string? s) => s?.Trim() switch {
    "16 MB"  => 16L  * 1024 * 1024,
    "64 MB"  => 64L  * 1024 * 1024,
    "256 MB" => 256L * 1024 * 1024,
    "1 GB"   => 1024L * 1024 * 1024,
    // Reported at their true size: the writer takes a 64-bit length and streams,
    // so these no longer have to be clamped to what an int (or an array) can hold.
    "4 GB"   => 4L  * 1024 * 1024 * 1024,
    "16 GB"  => 16L * 1024 * 1024 * 1024,
    _        => 0,                       // "Auto (fit to files)" or unknown → auto-size
  };

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new NtfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Adds (or replaces by name) files into the root directory of an existing NTFS image.
  /// The common case is a genuine in-place edit via <see cref="NtfsInPlaceAdder"/>: a free
  /// MFT record slot is claimed, a spec-shaped FILE record (resident or non-resident $DATA)
  /// is written, data clusters are allocated from $Bitmap, the $MFT:$BITMAP bit is set, and
  /// a collation-sorted entry is inserted into the root $INDEX_ROOT — existing files, their
  /// records and clusters stay byte-identical (no re-pack). ntfs-3g (ntfsls/ntfscat/ntfsfix)
  /// accepts the result. Cases not yet handled in place — the MFT being full (needs a
  /// reserved contiguous MFT zone + non-contiguous-MFT reader support), the root index
  /// spilling out of the resident $INDEX_ROOT, or nested sub-directory targets — fall back
  /// to the verified <see cref="NtfsWriter"/> rebuild.
  /// </summary>
  /// <summary>
  /// Largest volume the in-place editors can work on. NtfsModifier and NtfsRemover
  /// mutate a byte[] copy of the whole volume; past this the edit is applied by a
  /// streaming rebuild instead -- correct, just not in-place.
  /// </summary>
  private const long MaxBufferedImageBytes = 1L << 31;

  /// <summary>
  /// Applies an edit by reading every surviving entry out of <paramref name="archive" />
  /// and writing a fresh volume of the same declared size back over it. Memory scales
  /// with the content, not with the volume.
  /// </summary>
  private static void RebuildInPlaceStreaming(
      Stream archive,
      IReadOnlyList<(string Name, byte[] Data)> additions,
      ISet<string>? drop) {
    var declaredBytes = archive.Length;
    var combined = new NtfsWriter();

    archive.Position = 0;
    var reader = new NtfsReader(archive, leaveOpen: true);
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory)) {
      if (drop != null && (drop.Contains(entry.Name) || drop.Contains(Path.GetFileName(entry.Name))))
        continue;
      combined.AddFile(entry.Name, reader.Extract(entry));
    }
    foreach (var (name, data) in additions)
      combined.AddFile(name, data);

    // Every entry is materialised above, so the source is no longer needed.
    archive.Position = 0;
    archive.SetLength(0);
    combined.BuildTo(archive, declaredBytes);
  }

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

    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      RebuildInPlaceStreaming(archive, FormatHelpers.FilesOnly(inputs).ToList(), drop: null);
      return;
    }

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var original = ms.ToArray();
    var items = FormatHelpers.FilesOnly(inputs).ToList();

    // Genuine in-place on a working copy; commit only if every input succeeds so a
    // structural limit leaves the source untouched for the rebuild fallback.
    var work = (byte[])original.Clone();
    var inPlace = true;
    try {
      foreach (var (name, data) in items)
        NtfsInPlaceAdder.AddFile(work, name, data);
    } catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidDataException) {
      inPlace = false;
    }
    if (inPlace) {
      archive.Position = 0;
      archive.Write(work, 0, work.Length);
      archive.SetLength(work.Length);
      return;
    }

    // Fallback: verified rebuild from the untouched original.
    var reader = new NtfsReader(new MemoryStream(original, false));
    var combined = new NtfsWriter();
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
      combined.AddFile(entry.Name, reader.Extract(entry));
    foreach (var (name, data) in items)
      combined.AddFile(name, data);
    var rebuilt = combined.Build(original.Length);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Removes files from an existing NTFS image with full secure wipe (cluster bytes
  /// for non-resident data, MFT record, and root-dir index entry). No forensic
  /// recovery of the removed content is possible from the resulting bytes.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      RebuildInPlaceStreaming(archive, [], new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase));
      return;
    }

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var name in entryNames)
      NtfsRemover.Remove(image, name);
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
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
