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

  // The optimization adapters are keyed on this descriptor's runtime type, so the
  // registration has to have run before any instance can be looked up. Doing it from
  // the type initializer gives exactly that guarantee without a module initializer.
  static NtfsFormatDescriptor() => NtfsOptimizationRegistration.Register();

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// NTFS creation knobs surfaced by the Convert Archive dialog / CLI: image
  /// size (Auto + fixed presets), volume label (capped at 32 chars to match
  /// $VOLUME_NAME), cluster size, MFT record size, the 8.3 short-name toggle
  /// and the NTFS version — which selects every version-sensitive structure in
  /// the image (record header layout, metadata file set, $AttrDef table,
  /// $STANDARD_INFORMATION shape), not only the $VOLUME_INFORMATION stamp.
  /// Cluster + MFT record size cooperate via
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
      AllowedValues: ["3.1", "3.0", "1.2"],
      Description: "Volume version, which selects every version-sensitive structure in the image, not just the " +
        "$VOLUME_INFORMATION stamp. 3.1 (Windows XP and later, the modern default) records carry the MFT record " +
        "number as a uint32 at offset 44 and start their update-sequence array at 48; 3.0 (Windows 2000) and 1.2 " +
        "(Windows NT 3.51/4.0) records have no record-number field and start the array at 42. 3.0 and 3.1 carry " +
        "$Secure (record 9), the $Extend directory (record 11), the 3.x $AttrDef table and the 72-byte " +
        "$STANDARD_INFORMATION; 1.2 has $Quota at record 9, no $Extend, the 1.2 $AttrDef table (with " +
        "$VOLUME_VERSION, $SYMBOLIC_LINK and no $LOGGED_UTILITY_STREAM) and the 48-byte $STANDARD_INFORMATION."),
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
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries) {
          if (entry.IsDirectory) continue;
          // The extent map keys regular files by their leaf $FILE_NAME; the
          // reader surfaces the full path. Re-key to the leaf to line them up.
          var leaf = entry.Name.Contains('/') ? entry.Name[(entry.Name.LastIndexOf('/') + 1)..] : entry.Name;
          // Two files sharing a leaf in different folders cannot be told apart by
          // that key: the size of a small resident one would be applied to the
          // run of a large one and the tip wipe would cut into live data. Neither
          // gets a tip wipe.
          if (!sizeMap.TryAdd(leaf, entry.Size)) ambiguous.Add(leaf);
        }
        foreach (var leaf in ambiguous) sizeMap.Remove(leaf);
        foreach (var leaf in sizeMap.Keys.ToList())
          if (usedExtentCount.GetValueOrDefault(leaf) != 1) sizeMap.Remove(leaf);
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new NtfsBlockMover();
    mover.Init(image); // reads only the boot sector + MFT record 0
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
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
      if (!input.CanSeek || input.Length > Array.MaxLength)
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

    // Nothing could be trimmed in place. The volume is handed back as it is rather
    // than rebuilt: a rebuilt volume would be smaller only by dropping the label,
    // serial number, security descriptors, streams and timestamps of its files.
    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    input.CopyTo(output);
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware NTFS defragmentor: planner-driven and in place (<see cref="DefragPlanner"/>
  /// + <see cref="NtfsBlockMover"/>) for the packing modes, a carved hole, ascending order,
  /// a metadata placement and layout templates. Block interleave is refused before a byte moves.
  /// </summary>
  /// <remarks>
  /// There is no rebuild fallback. Writing the volume afresh keeps its size but drops the
  /// label, serial number, security descriptors, alternate data streams, reparse points,
  /// timestamps and attributes — a defragmentation that loses metadata is not one.
  /// </remarks>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    // Interleaving deals a file's clusters out one by one, and a run list with a run
    // per cluster outgrows the file's MFT record long before the file is large.
    DefragSupport.Require(options, DefragFeature.Packing | DefragFeature.CarveHole | DefragFeature.AscendingOrder
      | DefragFeature.MetadataZone | DefragFeature.LayoutTemplate, "NTFS");
    DefragmentWithPlanner(archive, options);
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
      options.Profile, options.Mode, interleaveStride: options.InterleaveStride,
      holeSize: options.HoleSize, holeAt: options.HoleAt, metadataZone: options.MetadataZonePlacement,
      layoutTemplate: options.LayoutTemplate, movableMetadata: mover.RelocatableMetadata);
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
  /// $MFTMirr, $LogFile, $Volume (with a $VOLUME_INFORMATION carrying the
  /// version the NtfsVersion option selects — 3.1 by default — and a
  /// $VOLUME_NAME), $AttrDef, root ., $Bitmap, $Boot, $BadClus,
  /// $Secure (or $Quota on a 1.2 volume), $UpCase (128 KiB UTF-16 table),
  /// and, from NTFS 3.0 on, $Extend. Every record
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
  // NTFS version, which drives the $VOLUME_INFORMATION stamp, the FILE record
  // header layout, the metadata file set, the $AttrDef table and the
  // $STANDARD_INFORMATION shape.
  private static void ApplyWriterOptions(NtfsWriter w, IReadOnlyDictionary<string, string>? specific) {
    if (specific == null) return;
    if (string.Equals(specific.GetValueOrDefault("Compression"), "LZNT1", StringComparison.OrdinalIgnoreCase))
      w.SetCompression(true);
    if (NtfsVersions.TryParse(specific.GetValueOrDefault("NtfsVersion"), out var version))
      w.SetNtfsVersion(version);
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
  /// Adds (or replaces by path) files in an existing NTFS image, genuinely in place via
  /// <see cref="NtfsInPlaceAdder"/>: a free MFT record slot is claimed, a spec-shaped FILE
  /// record is written, data clusters are allocated from $Bitmap, missing folders are
  /// created and each entry is linked into its parent's $I30 index with every existing
  /// entry kept as it was. Existing files, their records and clusters stay byte-identical.
  /// </summary>
  /// <remarks>
  /// All inputs are applied to a working copy and committed together, so a case the
  /// in-place editor cannot express leaves the volume untouched and is reported as
  /// <see cref="NotSupportedException"/>. There is deliberately no rebuild fallback: a
  /// volume written afresh by <see cref="NtfsWriter"/> loses the label, serial number,
  /// security descriptors, alternate data streams, reparse points, timestamps and
  /// attributes of everything already on it.
  /// </remarks>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var work = LoadForInPlaceEdit(archive);
    try {
      foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
        NtfsInPlaceAdder.AddFile(work, name, data);
    } catch (Exception ex) when (ex is IOException or InvalidDataException) {
      throw new NotSupportedException($"NTFS: the files cannot be added in place ({ex.Message}); the volume was left unchanged.", ex);
    }
    Commit(archive, work);
  }

  /// <summary>
  /// Removes files — or folders with everything in them — from an existing NTFS image
  /// with a full secure wipe (cluster bytes, MFT record and index entry). All names are
  /// applied to a working copy and committed together.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    var work = LoadForInPlaceEdit(archive);
    foreach (var name in ExpandFolders(work, entryNames))
      NtfsRemover.Remove(work, name);
    Commit(archive, work);
  }

  /// <summary>
  /// The in-place editors walk the volume as one array. A volume that does not fit one
  /// is refused rather than rebuilt, for the reasons given on <see cref="Add(Stream, IReadOnlyList{ArchiveInputInfo})"/>.
  /// </summary>
  private static byte[] LoadForInPlaceEdit(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (archive.Length > Array.MaxLength)
      throw new NotSupportedException(
        $"NTFS: in-place editing holds the volume in memory; a {archive.Length:N0}-byte volume is larger than that allows, "
        + "and rebuilding it instead would drop its metadata.");
    archive.Position = 0;
    var work = new byte[archive.Length];
    archive.ReadExactly(work);
    return work;
  }

  private static void Commit(Stream archive, byte[] work) {
    archive.Position = 0;
    archive.Write(work, 0, work.Length);
    archive.SetLength(work.Length);
  }

  /// <summary>
  /// Replaces every name that is a folder by everything beneath it, deepest first,
  /// followed by the folder itself.
  /// </summary>
  private static IEnumerable<string> ExpandFolders(byte[] image, string[] entryNames) {
    List<(string Name, bool IsDirectory)>? listing = null;
    foreach (var raw in entryNames ?? []) {
      var name = raw.Replace('\\', '/').Trim('/');
      listing ??= new NtfsReader(new MemoryStream(image, false)).Entries
        .Select(e => (e.Name.Replace('\\', '/'), e.IsDirectory)).ToList();
      var isFolder = listing.Any(e => e.IsDirectory && string.Equals(e.Item1, name, StringComparison.OrdinalIgnoreCase));
      if (!isFolder) { yield return name; continue; }
      foreach (var child in listing
                 .Where(e => e.Item1.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase))
                 .OrderByDescending(e => e.Item1.Count(c => c == '/'))
                 .ThenBy(e => e.IsDirectory))
        yield return child.Item1;
      yield return name;
    }
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
