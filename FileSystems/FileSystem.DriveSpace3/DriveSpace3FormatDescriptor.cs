#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
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
/// </summary>
public sealed class DriveSpace3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty {
  /// <inheritdoc />
  public string Id => "DriveSpace3";
  /// <inheritdoc />
  public string DisplayName => "DriveSpace 3 CVF";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <inheritdoc />
  public string DefaultExtension => ".cvf";
  /// <inheritdoc />
  // Extension-shared with DoubleSpace; detection routes by MS_DSP3 magic.
  public IReadOnlyList<string> Extensions => [];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MS_DSP3"u8.ToArray(), Offset: 3, Confidence: 0.95),
  ];
  /// <inheritdoc />
  // Four-tier effort set matches the DoubleSpace/DriveSpace ds-lz77 family:
  // base / + / ++ are routed through MsLzhCompressor.Compress(data, effort),
  // with the per-cluster shrink-or-store fallback inside DsCompression
  // applying at every tier.
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored",   "Stored (no compression)"),
    new("ms-lzh",   "MS LZH"),
    new("ms-lzh+",  "MS LZH (lazy matching, slower better ratio)"),
    new("ms-lzh++", "MS LZH (iterated parsing, best ratio)"),
  ];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
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

  // =========================================================================
  //                         Archive read / extract
  // =========================================================================

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new DoubleSpaceReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, -1, "ms-lzh", e.IsDirectory, false, null)).ToList();
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new DoubleSpaceReader(stream);
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
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var w = new DoubleSpaceWriter {
      Variant = CvfVariant.DriveSpace3,
      MethodName = options.MethodName,
    };
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // =========================================================================
  //                       Modify (add / remove via rebuild)
  // =========================================================================

  /// <inheritdoc />
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => {
        using var r = new DoubleSpaceReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
      },
      buildImage: files => {
        var w = new DoubleSpaceWriter { Variant = CvfVariant.DriveSpace3 };
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

  /// <inheritdoc />
  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        using var r = new DoubleSpaceReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
      },
      buildImage: files => {
        var w = new DoubleSpaceWriter { Variant = CvfVariant.DriveSpace3 };
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

  // =========================================================================
  //                         Filesystem extent map
  // =========================================================================

  /// <inheritdoc />
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
    image.Position = 0;
    var imageSize = image.Length;
    var extents = DoubleSpaceExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  // =========================================================================
  //                         IFilesystemBlockMover
  // =========================================================================

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new DoubleSpaceBlockMover();
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    mover.Init(ms.ToArray());
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
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
}
