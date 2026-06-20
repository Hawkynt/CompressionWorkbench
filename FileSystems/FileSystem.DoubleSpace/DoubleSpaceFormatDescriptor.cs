#pragma warning disable CS1591
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.DoubleSpace;

public sealed class DoubleSpaceFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {
  public string Id => "DoubleSpace";
  public string DisplayName => "DoubleSpace CVF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".cvf";
  public IReadOnlyList<string> Extensions => [".cvf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Encoding.ASCII.GetBytes("MSDSP6.0"), Offset: 3, Confidence: 0.85),
    // Some CVF files only expose the plaintext "DBLSPACE" name at offset 0
    // instead of the MSDSP signature in the BPB. Catching that case here
    // avoids duplicating the whole descriptor in a separate project.
    new(Encoding.ASCII.GetBytes("DBLSPACE"), Offset: 0, Confidence: 0.80),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored",     "Stored (no compression)"),
    new("ds-lz77",    "DS LZ77"),
    new("ds-lz77+",   "DS LZ77 (lazy matching, slower better ratio)"),
    new("ds-lz77++",  "DS LZ77 (Zopfli-style iteration, best ratio)"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Microsoft DoubleSpace compressed volume file (MS-DOS 6.0).
  /// <para>
  /// Spec-compliant MDBPB + MDFAT + BitFAT + DATA layout. Inner FAT16 volume
  /// with VFAT long filenames. Writer emits stored (uncompressed) runs; the
  /// JM/DSS LZ payload variant is a future enhancement (see
  /// <see cref="DoubleSpaceWriter"/>).
  /// </para>
  /// </summary>
  public string Description => "Microsoft DoubleSpace compressed volume file MS-DOS 6.0 (MDBPB/MDFAT/BitFAT layout; stored runs, VFAT LFN)";

  // ── IFormatOptionsSchema ──────────────────────────────────────────────────
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Compatibility",
      DisplayName: "On-disk layout",
      Kind: FormatOptionKind.Enum,
      Default: "Extended",
      AllowedValues: ["Genuine", "Extended"],
      Description:
        "Genuine — the real MS-DOS 6.x DoubleSpace/DriveSpace v2 container (MSDBL6.0, "
        + "16 sectors/cluster, inner FAT12, 4-byte MDFAT). Mounted and read byte-exact by "
        + "the independent dmsdos driver, which reports it as 'drivespace CVF version 2' "
        + "(also the layout the real DRVSPACE.BIN driver mounts). Clusters are STORED "
        + "(uncompressed); single flat root directory; ~69 clusters (557 KB) fixed "
        + "geometry. Choose this for interoperability with real DOS DriveSpace tooling.\n"
        + "Extended — CompressionWorkbench's feature layout (MSDSP6.0): DS-LZ77 "
        + "compression, long filenames, in-place add/remove, defrag and block-mover "
        + "support, readable ONLY by CompressionWorkbench — NOT by the real DriveSpace "
        + "driver or dmsdos."),
    new FormatOptionDescriptor(
      Key: "VolumeLabel",
      DisplayName: "Volume label",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Optional 11-char inner-volume label (Genuine layout only).",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "Timestamp",
      DisplayName: "File timestamp",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Optional ISO-8601 date/time (e.g. 1993-05-01) stamped on every file's "
        + "FAT directory entry. Blank leaves the date/time unset (Genuine layout only).",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "Method", DisplayName: "Compression", Kind: FormatOptionKind.Enum,
      Default: "DS",
      AllowedValues: ["Stored", "DS", "JM"],
      Description: "Per-cluster compression for the Genuine layout. Stored = none. "
        + "DS = DoubleSpace 'DS-0-x' LZ (DOS 6.0/6.2). JM = DriveSpace 'JM-0-x' LZ (DOS 6.22). "
        + "Both are read by the real driver and dmsdos; each cluster keeps the smaller of "
        + "compressed/stored.",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "Level", DisplayName: "Compression level", Kind: FormatOptionKind.Integer,
      Default: "2",
      Description: "Codec search effort (1 = fast, higher = better ratio, slower).",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "ForceCompress", DisplayName: "Force compression", Kind: FormatOptionKind.Boolean,
      Default: "false",
      Description: "Keep the compressed form even when it does not shrink a cluster.",
      DependsOn: "Compatibility=Genuine"),
  ];

  private static Compression.Registry.Cvf.CvfLzMethod ParseMethod(string s) => s.ToLowerInvariant() switch {
    "ds" => Compression.Registry.Cvf.CvfLzMethod.Ds,
    "jm" => Compression.Registry.Cvf.CvfLzMethod.Jm,
    _ => Compression.Registry.Cvf.CvfLzMethod.Stored,
  };

  private static bool IsGenuineV2(byte[] data) =>
    data.Length > 0x0E
    && Encoding.ASCII.GetString(data, 3, 8) == "MSDBL6.0"
    && data[0x0D] == 16;

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    if (IsGenuineV2(data)) {
      using var g = new GenuineCvfReader(new MemoryStream(data));
      return g.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, null)).ToList();
    }
    var r = new DoubleSpaceReader(new MemoryStream(data));
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, -1, "DS-LZ77", e.IsDirectory, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    if (IsGenuineV2(data)) {
      using var g = new GenuineCvfReader(new MemoryStream(data));
      foreach (var e in g.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && !MatchesFilter(e.Name, files)) continue;
        WriteFile(outputDir, e.Name, g.Extract(e));
      }
      return;
    }
    var r = new DoubleSpaceReader(new MemoryStream(data));
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.GetOption("Compatibility", "Extended").Equals("Genuine", StringComparison.OrdinalIgnoreCase)) {
      var gw = new GenuineCvfWriter {
        VolumeLabel = options.GetOption("VolumeLabel", ""),
        Timestamp = FatDirStamp.Parse(options.GetOption("Timestamp", "")),
        CompressionMethod = ParseMethod(options.GetOption("Method", "DS")),
        CompressionLevel = options.GetOptionInt("Level", 2),
        ForceCompress = options.GetOptionBool("ForceCompress", false),
      };
      foreach (var (name, data) in FlatFiles(inputs))
        gw.AddFile(name, data);
      output.Write(gw.Build());
      return;
    }

    var w = new DoubleSpaceWriter {
      Variant = CvfVariant.DoubleSpace60,
      MethodName = options.MethodName,
    };
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  /// <summary>
  /// True in-place add: BitFAT bits flip, MDFAT cluster-allocation entries
  /// are written in place, inner FAT chains extended, and VFAT dirents are
  /// inserted into the root directory without rewriting any unrelated bytes.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => DoubleSpaceInPlaceModifier.Add(archive, inputs);

  /// <summary>
  /// True in-place remove: walks the inner FAT chain, zeros each physical
  /// run, clears BitFAT bits, zeros MDFAT entries, zeros inner FAT chain,
  /// and scratches the dirent (+ LFN chain) with 0xE5.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => DoubleSpaceInPlaceModifier.Remove(archive, entryNames);

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => DoubleSpaceExtentMap.Enumerate(image);

  /// <summary>
  /// Zeros all unused space in the CVF image: every physical sector in the DATA
  /// region not claimed by a live file/directory run, plus any gaps outside the
  /// metadata regions, is overwritten with zeros.
  /// <para>
  /// Cluster-tip wiping is not applicable to a CVF: the DATA region holds
  /// <em>compressed/stored</em> sector runs whose physical byte length is
  /// unrelated to the logical (uncompressed) file size recorded in the inner
  /// FAT directory. Zeroing a tail by logical-size offset would corrupt the
  /// encoded run, so only whole free sectors are cleared.
  /// </para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = DoubleSpaceExtentMap.Enumerate(image);
    // Compressed/stored physical runs — logical file size does not map to a
    // physical cluster-aligned tail, so cluster-tip wiping is disabled.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

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

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware CVF defragmentor. Supports planner-driven in-place defrag
  /// (using <see cref="DefragPlanner"/> + <see cref="DoubleSpaceBlockMover"/>)
  /// with rebuild fallback on error.
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

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new DoubleSpaceReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new DoubleSpaceWriter { Variant = CvfVariant.DoubleSpace60 };
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }
}
