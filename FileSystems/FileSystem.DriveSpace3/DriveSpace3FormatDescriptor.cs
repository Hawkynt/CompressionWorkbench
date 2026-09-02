#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using Compression.Registry.Cvf;
using FileSystem.DoubleSpace;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.DriveSpace3;

/// <summary>
/// Descriptor for Microsoft DriveSpace 3 CVF (Windows 95 Plus! Pack, 1995).
/// Distinguished from DoubleSpace/DriveSpace 2 by the <c>MS_DSP3</c> MDBPB
/// signature at file offset 3 and the <c>DVR3</c> CvfSignature at offset 36.
/// The compression algorithm changed from DS LZ77 (DOS 6.x) to MS LZH
/// (LZ77 + canonical Huffman).
/// <para>
/// Read/write/modify/defrag are delegated to the shared DoubleSpace
/// infrastructure (<see cref="DoubleSpaceWriter"/> routed through
/// <see cref="CvfVariant.DriveSpace3"/>, <see cref="DoubleSpaceReader"/> with
/// MS LZH dispatch, <see cref="DoubleSpaceExtentMap"/>,
/// <see cref="DoubleSpaceBlockMover"/>) — the on-disk MDBPB+MDFAT+BitFAT
/// layout is byte-compatible across the whole CVF family; only the OEM bytes
/// and inner-cluster codec differ. This brings DriveSpace 3 to full parity
/// with DoubleSpace/DriveSpace 6.22 for defrag, wipe-empty, modify, extent
/// map and block mover.
/// </para>
/// <para>
/// Shares the <c>.cvf</c> extension with DoubleSpace; FormatDetector
/// disambiguates by magic.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/sandsmark/dmsdos</c> — dmsdos, the GPL Linux CVF driver whose source + <c>doc/dmsdos.doc</c> are the de-facto DriveSpace 3 on-disk specification (5-byte MDFAT, MS LZH codecs)</description></item>
///   <item><description>Microsoft Plus! for Windows 95 documentation (DriveSpace 3) — original vendor description</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/DriveSpace</c> — Wikipedia overview of the CVF family</description></item>
/// </list>
/// </summary>
public sealed class DriveSpace3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {
  /// <inheritdoc />
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "DriveSpace3";
  /// <inheritdoc />
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "DriveSpace 3 CVF";
  /// <inheritdoc />
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <inheritdoc />
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".cvf";
  /// <inheritdoc />
  // Extension-shared with DoubleSpace; detection routes by MS_DSP3 magic.
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [];
  /// <inheritdoc />
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MS_DSP3"u8.ToArray(), Offset: 3, Confidence: 0.95),
  ];
  /// <inheritdoc />
  // Four-tier effort set matches the DoubleSpace/DriveSpace ds-lz77 family:
  // base / + / ++ are routed through MsLzhCompressor.Compress(data, effort),
  // with the per-cluster shrink-or-store fallback inside DsCompression
  // applying at every tier.
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored",   "Stored (no compression)"),
    new("ms-lzh",   "MS LZH"),
    new("ms-lzh+",  "MS LZH (lazy matching, slower better ratio)"),
    new("ms-lzh++", "MS LZH (iterated parsing, best ratio)"),
  ];
  /// <inheritdoc />
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Microsoft DriveSpace 3 CVF (Win95 Plus! Pack 1995). Shares the
  /// DoubleSpace MDBPB+MDFAT+BitFAT infrastructure (extent map, block mover,
  /// planner-driven defrag, wipe-empty, add/remove via rebuild). Per-cluster
  /// payload is MS LZH (LZ77 + canonical Huffman) instead of DS LZ77 and
  /// publishes the full four-tier effort set — <c>ms-lzh</c> (greedy),
  /// <c>ms-lzh+</c> (lazy matching), <c>ms-lzh++</c> (iterated multi-pass).
  /// The shrink-or-store fallback inside the codec emits a stored CVF run
  /// when the compressed payload would not shrink the cluster, so
  /// incompressible regions are not penalised at any effort level.
  /// </summary>
  public string Description =>
    "Microsoft DriveSpace 3 CVF (Win95 Plus! Pack 1995) — defrag/wipe/modify/extent-map/block-mover parity with DoubleSpace via shared MDBPB infrastructure; per-cluster MS LZH with full effort tiers (greedy / lazy / iterated) and stored-run fallback at every tier.";

  // ── IFormatOptionsSchema ──────────────────────────────────────────────────
  /// <inheritdoc />
  /// <summary>
  /// Gets the options schema.
  /// </summary>
public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Compatibility",
      DisplayName: "On-disk layout",
      Kind: FormatOptionKind.Enum,
      Default: "Extended",
      AllowedValues: ["Genuine", "Extended"],
      Description:
        "Genuine — the real Windows 95 DriveSpace 3 layout (MSDBL6.0 container, 32 KB "
        + "clusters, version flag 3, 5-byte MDFAT, inner FAT16). Mounted and read "
        + "byte-exact by the independent dmsdos driver (and the real Win95 Plus!/OSR2 "
        + "DriveSpace 3 driver). Clusters are STORED (uncompressed); single flat root "
        + "directory; up to ~511 clusters. Choose this for interoperability with real "
        + "DriveSpace tooling — the MS LZH compression methods do not apply here.\n"
        + "Extended — CompressionWorkbench's feature layout (MS_DSP3/DVR3 header). Adds "
        + "per-cluster MS LZH compression, long filenames, in-place add/remove, defrag "
        + "and block-mover support, but is readable ONLY by CompressionWorkbench — NOT "
        + "by the genuine DriveSpace driver or dmsdos."),
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Volume label", Kind: FormatOptionKind.String,
      Default: "",
      Description: "Optional 11-char inner-volume label written to the root directory (Genuine layout only).",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "Timestamp", DisplayName: "File timestamp", Kind: FormatOptionKind.String,
      Default: "",
      Description: "Optional ISO-8601 date/time (e.g. 1996-08-24) stamped on every file's "
        + "FAT directory entry. Blank leaves the date/time unset (Genuine layout only).",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "Method", DisplayName: "Compression", Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Stored", "JM", "SQ", "Auto"],
      Description: "Per-cluster compression for the Genuine layout. Stored = none. "
        + "JM = DriveSpace 3 'JM-0-x' LZ (Normal/High). SQ = DriveSpace 3 'SQ-0-0' (Ultra; "
        + "DEFLATE). Both are read by the real driver and dmsdos. Auto = per cluster keep the "
        + "smallest of all codecs, falling back to stored.",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "Level", DisplayName: "Compression level", Kind: FormatOptionKind.Integer,
      Default: "2",
      Description: "Codec search effort (1 = fast, higher = better ratio, slower).",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "ForceCompress", DisplayName: "Force compression", Kind: FormatOptionKind.Boolean,
      Default: "false",
      Description: "Keep the compressed form even when it does not shrink a cluster "
        + "(overrides the per-cluster auto-best stored fallback).",
      DependsOn: "Compatibility=Genuine"),
  ];

  private static CvfLzMethod ParseMethod(string s) => s.ToLowerInvariant() switch {
    "ds" => CvfLzMethod.Ds,
    "jm" => CvfLzMethod.Jm,
    "sq" => CvfLzMethod.Sq,
    "auto" => CvfLzMethod.Auto,
    _ => CvfLzMethod.Stored,
  };

  // =========================================================================
  //                         Archive read / extract
  // =========================================================================

  /// <inheritdoc />
  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    if (IsGenuineDvr3(data)) {
      using var g = new GenuineDvr3Reader(new MemoryStream(data));
      return g.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, null)).ToList();
    }
    using var r = new DoubleSpaceReader(new MemoryStream(data));
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, -1, "ms-lzh", e.IsDirectory, false, null)).ToList();
  }

  /// <inheritdoc />
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    if (IsGenuineDvr3(data)) {
      using var g = new GenuineDvr3Reader(new MemoryStream(data));
      foreach (var e in g.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
        WriteFile(outputDir, e.Name, g.Extract(e));
      }
      return;
    }
    using var r = new DoubleSpaceReader(new MemoryStream(data));
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  // =========================================================================
  //                            Archive create
  // =========================================================================

  /// <inheritdoc />
  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    if (options.GetOption("Compatibility", "Extended").Equals("Genuine", StringComparison.OrdinalIgnoreCase)) {
      var gw = new GenuineDvr3Writer {
        VolumeLabel = options.GetOption("VolumeLabel", ""),
        Timestamp = FatDirStamp.Parse(options.GetOption("Timestamp", "")),
        CompressionMethod = ParseMethod(options.GetOption("Method", "JM")),
        CompressionLevel = options.GetOptionInt("Level", 2),
        ForceCompress = options.GetOptionBool("ForceCompress", false),
      };
      foreach (var (name, data) in FlatFiles(inputs))
        gw.AddFile(name, data);
      output.Write(gw.Build());
      return;
    }

    var w = new DoubleSpaceWriter {
      Variant = CvfVariant.DriveSpace3,
      MethodName = options.MethodName,
    };
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // The genuine Win95 DriveSpace 3 container: MSDBL6.0 signature, 64 sectors per
  // cluster (boot byte 13) and version flag 3 (boot byte 51) — distinct from the
  // Extended MS_DSP3 layout and from DoubleSpace/DriveSpace v2 (16 sec/cluster).
  private static bool IsGenuineDvr3(byte[] data) =>
    data.Length > 0x34
    && System.Text.Encoding.ASCII.GetString(data, 3, 8) == "MSDBL6.0"
    && data[0x0D] == 64
    && data[0x33] == 3;

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  // =========================================================================
  //                       Modify (add / remove via rebuild)
  // =========================================================================

  /// <inheritdoc />
  /// <summary>
  /// True in-place add via the shared MDBPB-aware
  /// <see cref="DoubleSpaceInPlaceModifier"/>. BitFAT bits flip, MDFAT
  /// cluster-allocation entries are written in place, inner FAT chains
  /// extended, and VFAT dirents are inserted into the root directory
  /// without rewriting any unrelated bytes. MS LZH codec is auto-selected
  /// from the OEM signature (<c>MS_DSP3</c>).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var data = ReadAll(archive);
    if (IsGenuineDvr3(data)) { WriteBack(archive, RebuildGenuine(data, inputs, null)); return; }
    DoubleSpaceInPlaceModifier.Add(archive, inputs);
  }

  /// <inheritdoc />
  /// <summary>
  /// True in-place remove via the shared MDBPB-aware
  /// <see cref="DoubleSpaceInPlaceModifier"/>. Walks the inner FAT chain,
  /// zeros each physical run, clears BitFAT bits, zeros MDFAT entries,
  /// zeros inner FAT chain, and scratches the dirent (+ LFN chain) with 0xE5.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    var data = ReadAll(archive);
    if (IsGenuineDvr3(data)) { WriteBack(archive, RebuildGenuine(data, null, entryNames)); return; }
    DoubleSpaceInPlaceModifier.Remove(archive, entryNames);
  }

  // Rebuild a genuine DVR3 image from its current contents — the basis for
  // add/remove/defrag/purge on the WORM-style genuine layout. Reading every
  // file and re-emitting packs clusters contiguously (defrag), re-runs the
  // auto-best compressor (optimize/shrink) and drops any stale/unused sectors
  // (purge). The inner volume label is preserved.
  private static byte[] RebuildGenuine(byte[] data, IReadOnlyList<ArchiveInputInfo>? add, string[]? remove) {
    using var r = new GenuineDvr3Reader(new MemoryStream(data));
    var keep = new List<(string Name, byte[] Data)>();
    var removeSet = remove is null ? null : new HashSet<string>(remove.Select(LeafLower));
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (removeSet is not null && removeSet.Contains(LeafLower(e.Name))) continue;
      keep.Add((e.Name, r.Extract(e)));
    }
    if (add is not null)
      foreach (var (n, d) in FlatFiles(add)) {
        keep.RemoveAll(k => LeafLower(k.Name) == LeafLower(n));
        keep.Add((n, d));
      }
    var w = new GenuineDvr3Writer {
      VolumeLabel = r.VolumeLabel,
      CompressionMethod = CvfLzMethod.Auto,
      CompressionLevel = 2,
    };
    foreach (var (n, d) in keep) w.AddFile(n, d);
    return w.Build();
  }

  private static string LeafLower(string name) {
    var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
    return (slash >= 0 ? name[(slash + 1)..] : name).ToLowerInvariant();
  }

  private static void WriteBack(Stream s, byte[] img) {
    s.Position = 0; s.SetLength(img.Length); s.Write(img); s.Position = 0;
  }

  // =========================================================================
  //                         Filesystem extent map
  // =========================================================================

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the extents.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => DoubleSpaceExtentMap.Enumerate(image);

  // =========================================================================
  //                              Wipe empty
  // =========================================================================

  /// <summary>
  /// Zeros all unused space in the CVF image: every physical sector in the DATA
  /// region not claimed by a live file/directory run, plus any gaps outside the
  /// metadata regions, is overwritten with zeros. Cluster-tip wiping is not
  /// applicable to a CVF — the DATA region holds compressed/stored runs whose
  /// physical byte length is unrelated to the logical (uncompressed) file size.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var data = ReadAll(image);
    if (IsGenuineDvr3(data)) {
      // Purge: a fresh rebuild contains only live clusters packed contiguously,
      // so no stale/unused payload sectors survive.
      var before = data.Length;
      var rebuilt = RebuildGenuine(data, null, null);
      WriteBack(image, rebuilt);
      return Math.Max(0, before - rebuilt.Length);
    }
    image.Position = 0;
    var imageSize = image.Length;
    var extents = DoubleSpaceExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  // =========================================================================
  //                         IFilesystemBlockMover
  // =========================================================================

  /// <inheritdoc />
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new DoubleSpaceBlockMover();
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    mover.Init(ms.ToArray());
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new DoubleSpaceBlockMover();
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    mover.Init(ms.ToArray());
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  // =========================================================================
  //                            Defragmentation
  // =========================================================================

  /// <inheritdoc />
  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware CVF defragmentor. Supports planner-driven in-place defrag
  /// (using <see cref="DefragPlanner"/> + <see cref="DoubleSpaceBlockMover"/>)
  /// with rebuild fallback on error — same shape as
  /// <see cref="DoubleSpaceFormatDescriptor.Defragment(Stream, DefragOptions)"/>.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    var data = ReadAll(archive);
    if (IsGenuineDvr3(data)) { WriteBack(archive, RebuildGenuine(data, null, null)); return; }

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

  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;

    using var bpbMs = new MemoryStream();
    archive.CopyTo(bpbMs);
    var imageData = bpbMs.ToArray();

    var mover = new DoubleSpaceBlockMover();
    mover.Init(imageData);

    var extents = DoubleSpaceExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "scanning", Fraction: 0, CurrentReadOffset: 0, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: extents, Status: "Analysing layout"));

    var moves = DefragPlanner.Plan(
      extents, mover.DataRegionByteStart, imageSize, mover.BytesPerSector,
      options.Profile, options.Mode, Math.Max(1, options.InterleaveStride),
      options.HoleSize, options.HoleAt);

    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
        ImageSize: imageSize, BlockMap: extents, Status: "Already defragmented"));
      return;
    }

    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize, () => {
      archive.Position = 0;
      using var reread = new MemoryStream();
      archive.CopyTo(reread);
      imageData = reread.ToArray();
      mover.Init(imageData);
    });

    archive.Position = 0;
    var postExtents = DoubleSpaceExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: postExtents, Status: "Defragmentation complete"));
  }

  private static void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new DoubleSpaceReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
      },
      buildImage: files => {
        var w = new DoubleSpaceWriter { Variant = CvfVariant.DriveSpace3 };
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string FirstLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
