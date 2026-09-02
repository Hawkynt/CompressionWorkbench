#pragma warning disable CS1591
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.DoubleSpace;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/sandsmark/dmsdos</c> — dmsdos, the GPL Linux CVF driver whose source + <c>doc/dmsdos.doc</c> are the de-facto on-disk specification (incl. the JM-0-0 cluster codec)</description></item>
///   <item><description>Microsoft MS-DOS 6.22 documentation (DriveSpace chapter) — original vendor description</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/DriveSpace</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class DriveSpaceFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// Sole tunable the DriveSpace writer honours: the per-cluster compression
  /// method. "stored" forces uncompressed runs; the ds-lz77 family selects the
  /// genuine DS LZ77 codec at increasing effort tiers (lazy matching, then
  /// Zopfli-style iteration). The geometry (MDBPB/MDFAT/BitFAT) is fixed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Method", DisplayName: "Compression method", Kind: FormatOptionKind.Enum, Default: "ds-lz77",
      AllowedValues: ["stored", "ds-lz77", "ds-lz77+", "ds-lz77++"],
      Description: "Per-cluster codec: stored (no compression) or DS LZ77 at rising effort (+ lazy, ++ iterated)."),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "DriveSpace";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "DriveSpace CVF";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".cvf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".cvf"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Encoding.ASCII.GetBytes("MSDSP6.2"), Offset: 3, Confidence: 0.85),
    // Some CVF files only expose the plaintext "DRVSPACE" name at offset 0
    // instead of the MSDSP signature in the BPB. Catching that case here
    // avoids duplicating the whole descriptor in a separate project.
    new(Encoding.ASCII.GetBytes("DRVSPACE"), Offset: 0, Confidence: 0.80),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored",     "Stored (no compression)"),
    new("ds-lz77",    "DS LZ77"),
    new("ds-lz77+",   "DS LZ77 (lazy matching, slower better ratio)"),
    new("ds-lz77++",  "DS LZ77 (Zopfli-style iteration, best ratio)"),
  ];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Microsoft DriveSpace compressed volume file (MS-DOS 6.22+/Windows 95).
  /// <para>
  /// Spec-compliant MDBPB + MDFAT + BitFAT + DATA layout. Inner FAT16 volume
  /// with VFAT long filenames. Writer emits stored (uncompressed) runs; the
  /// JM/DSS LZ payload variant is a future enhancement.
  /// </para>
  /// </summary>
  public string Description => "Microsoft DriveSpace compressed volume file MS-DOS 6.22+/Windows 95 (MDBPB/MDFAT/BitFAT layout; stored runs, VFAT LFN)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DoubleSpaceReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, -1, "DS-LZ77", e.IsDirectory, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DoubleSpaceReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // Prefer the schema-published Method knob; fall back to the top-level
    // MethodName field so callers using either path get the codec they asked for.
    var method = options.HasOption("Method") ? options.GetOption("Method", "ds-lz77") : options.MethodName;
    var w = new DoubleSpaceWriter {
      Variant = CvfVariant.DriveSpace62,
      MethodName = method,
    };
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  /// <summary>
  /// True in-place add: BitFAT bits flip, MDFAT cluster-allocation entries
  /// are written in place, inner FAT chains extended, and VFAT dirents are
  /// inserted into the root directory without rewriting any unrelated bytes.
  /// Variant auto-detected from the OEM signature in the existing image.
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

  /// <summary>
  /// Enumerates the extents.
  /// </summary>
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

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
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
        var w = new DoubleSpaceWriter { Variant = CvfVariant.DriveSpace62 };
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
