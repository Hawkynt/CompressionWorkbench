#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ntfs;

public sealed class NtfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {

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

  public string Id => "Ntfs";
  public string DisplayName => "NTFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

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
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    image.Position = 0;
    var extents = NtfsExtentMap.Enumerate(image).ToList();

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
      } catch {
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

    // Compute data origin: the first byte past all MetadataReserved extents.
    // User data must not be placed in the boot sector, MFT, or system file regions.
    long dataOrigin = mover.FirstDataByte;
    foreach (var e in extents) {
      if (e.Kind == DefragBlockKind.MetadataReserved) {
        var end = e.Offset + e.Length;
        if (end > dataOrigin) dataOrigin = end;
      }
    }
    // Align to cluster boundary.
    var cs = mover.ClusterSize;
    dataOrigin = (dataOrigin + cs - 1) / cs * cs;

    var moves = DefragPlanner.Plan(extents, dataOrigin, archive.Length, mover.ClusterSize, options.Profile, options.Mode);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    // After each move, re-init the mover by re-reading only the boot sector +
    // record 0 from the now-mutated stream — no whole-image load.
    DefragPlannerExecutor.Execute(archive, options, mover, moves, archive.Length, () => {
      archive.Position = 0;
      mover.Init(archive);
    });

    archive.Position = 0;
    var postExtents = NtfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    var totalSize = (int)archive.Length;
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new NtfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new NtfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build(totalSize);
      });
  }
  public string DefaultExtension => ".ntfs";
  public IReadOnlyList<string> Extensions => [".ntfs", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'N', (byte)'T', (byte)'F', (byte)'S', (byte)' ', (byte)' ', (byte)' ', (byte)' '], Offset: 3, Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
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

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new NtfsReader(stream);
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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
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
    var disk = totalSize > 0
      ? w.Build(totalSize,
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
      ? w.Build(totalSize,
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
  private static int ParseImageSizeBytes(string? s) => s?.Trim() switch {
    "16 MB"  => 16  * 1024 * 1024,
    "64 MB"  => 64  * 1024 * 1024,
    "256 MB" => 256 * 1024 * 1024,
    "1 GB"   => 1024 * 1024 * 1024,
    "4 GB"   => int.MaxValue,            // capped at int range; writer rounds to clusters
    "16 GB"  => int.MaxValue,            // capped at int range
    _        => 0,                       // "Auto (fit to files)" or unknown → auto-size
  };

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new NtfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Add files to an existing NTFS image. Current implementation re-builds the image
  /// with all existing files + the new ones — the inherent build-from-scratch design
  /// of <see cref="NtfsWriter"/> means "add" equals "re-pack" here. Use
  /// <see cref="Remove"/> first to clean up stale entries.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    archive.Position = 0;
    var reader = new NtfsReader(archive);
    var combined = new NtfsWriter();
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
      combined.AddFile(entry.Name, reader.Extract(entry));
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      combined.AddFile(name, data);
    var totalSize = (int)archive.Length;
    var rebuilt = combined.Build(totalSize);
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
}
