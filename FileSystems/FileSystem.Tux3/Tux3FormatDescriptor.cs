#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux3;

/// <summary>
/// Read+WORM descriptor for TUX3 — Daniel Phillips's version-tree
/// successor to TUX2 (linux-tux3 prototype). Magic "TUX3SUPR" sits
/// at file offset 4096 (the start of the superblock block). The WORM
/// writer emits a single-version image (no version chain, no atomic-commit
/// log) — the documented superblock prefix plus a sentinel "TUX3WORM" file
/// table at block 2 that <see cref="Tux3Reader"/> walks. Full
/// itable/otable/atable B-tree traversal of real linux-tux3 prototype dumps
/// is out of scope.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/OGAWAHirofumi/linux-tux3</c> — the linux-tux3 prototype tree — canonical source</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Tux3</c> — Wikipedia article</description></item>
///   <item><description>Daniel Phillips's Tux3 design postings (LKML / tux3 mailing list)</description></item>
/// </list>
/// </summary>
/// <summary>
/// Why there is nothing here to lay out again.
/// </summary>
/// <remarks>
/// The records run end to end and the reader walks them by adding each one's
/// length to a cursor, so a gap makes everything after it unreadable: packed
/// from the front is the only layout expressible. And it is the one the
/// container is always in, because removing a file writes it out compacted
/// rather than leaving a hole. See <see cref="Tux2FormatDescriptor" />, which
/// has the same shape.
public sealed class Tux3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty, ISyntheticEntryNames {

  /// <inheritdoc />
  public IReadOnlySet<string> SyntheticEntryNames => SyntheticNames;


  // ── Synthetic, non-file entries the reader always surfaces ──────────────
  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.OrdinalIgnoreCase) { "FULL.tux3", "metadata.ini", "superblock.bin" };

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The single tunable the single-version WORM writer honours: the 64-bit
  /// <c>birthday</c> field stamped into the superblock at offset 0x08.
  /// <see cref="Tux3Writer.Birthday"/> is written verbatim and
  /// <see cref="Tux3Reader.Birthday"/> reads it back, so the knob round-trips.
  /// Supplied as a hexadecimal string (with or without a leading <c>0x</c>);
  /// left blank the writer takes the moment of creation from the clock.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Birthday", DisplayName: "Birthday (hex)", Kind: FormatOptionKind.String,
      Default: "",
      Description: "64-bit creation stamp written to the superblock at offset 0x08 (hexadecimal)."),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Tux3";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "TUX3";
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
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".tux3";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".tux3"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("TUX3SUPR"u8.ToArray(), Offset: 4096, Confidence: 0.90),
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
  /// Gets the description.
  /// </summary>
  public string Description => "TUX3 version-tree research filesystem (linux-tux3) — single-version WORM image.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Tux3Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Tux3Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
    }
  }

  /// <summary>
  /// Emits a fresh single-version TUX3 image: zeroed boot region (block 0),
  /// documented superblock prefix (block 1, "TUX3SUPR" magic at offset 4096),
  /// and a sentinel WORM file table at block 2 carrying the per-file
  /// records. Round-trips through <see cref="Tux3Reader"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var birthday = ParseBirthday(options.GetOption("Birthday", ""));
    var w = birthday is { } stamp ? new Tux3Writer { Birthday = stamp } : new Tux3Writer();
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  /// <summary>Parses the hex <c>Birthday</c> knob; blank or unreadable leaves it to the writer.</summary>
  private static ulong? ParseBirthday(string value) {
    var s = value.Trim();
    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
      s = s[2..];
    return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber,
      System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Rewrites the image with every file laid out contiguously from the start.
  /// Records are copied straight from the old image into the new one, so a
  /// multi-gigabyte volume never has to fit in memory — the previous refusal
  /// was for want of wiring it up, not for want of a writer.
  /// </summary>
  /// <summary>
  /// Largest container the in-place pass is offered for. Its guard holds a copy
  /// of the image to compare payloads across the pass.
  /// </summary>
  private const long PlannerImageCap = 256L * 1024 * 1024;

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    using var reader = new Tux3Reader(stream);
    return reader.Entries
      .Where(e => !SyntheticNames.Contains(e.Name))
      .Select(reader.Extract)
      .ToList();
  }

  /// <summary>Plans a record-level layout and moves the records into it.</summary>
  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Tux3BlockMover();

    archive.Position = 0;
    var extents = Tux3RecordMap.Enumerate(archive).ToList();
    if (extents.Count == 0) return;

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

    archive.Position = 0;
    var postExtents = Tux3RecordMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the container out again, and
    // on one of ours the answer is usually that nothing is: removing a file
    // writes the records out packed, so there is no gap to close. What this is
    // for is a container that arrived from somewhere else.
    //
    // The unit that moves is the whole record — a file's bytes sit behind the
    // header naming them at an offset nothing records — and the walk only
    // reaches a record still in order with nothing before it, which is what
    // reading every payload back afterwards checks.
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
    // Every consolidate mode lands on the same layout here: the writer emits a
    // fresh volume packed from the first data block, and has no way to place
    // files against the tail. Carving a hole is the one request it cannot meet.
    if (options.Mode is DefragMode.CarveHole)
      throw new NotSupportedException(
        "Tux3 defragmentation cannot carve a hole: the rebuild always start-packs the volume.");

    var tempPath = Path.GetTempFileName();
    try {
      using (var temp = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite)) {
        using (var reader = new Tux3Reader(archive)) {
          var w = new Tux3Writer();
          foreach (var entry in reader.Entries) {
            if (entry.IsDirectory || SyntheticNames.Contains(entry.Name)) continue;
            var e = entry;
            w.AddStreamingFile(e.Name, e.Size, s => reader.ExtractTo(e, s));
          }
          w.WriteTo(temp);
        }

        options.OnProgress?.Invoke(new DefragProgressEvent(
          Phase: "commit", Fraction: 1.0, CurrentReadOffset: archive.Length,
          CurrentWriteOffset: temp.Length, ImageSize: temp.Length, BlockMap: null));

        temp.Position = 0;
        archive.Position = 0;
        temp.CopyTo(archive);
        archive.SetLength(temp.Length);
        archive.Flush();
      }
    } finally {
      File.Delete(tempPath);
    }
  }

  // ── IArchiveModifiable (genuine in-place R/W) ───────────────────────────
  //
  // Tux3InPlaceModifier appends/overwrites inline WORM-table records, keeping
  // the boot block, superblock, table header and all preceding records
  // byte-identical, then re-pads to a 4096 boundary and refreshes vol_blocks.
  // New entries are append-only; same-size replaces overwrite in place;
  // resize/delete tail-rewrite from the changed record onward.

  /// <summary>
  /// Adds the supplied entry to the target container.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this, SyntheticNames);
      return;
    }

    Tux3InPlaceModifier.Add(archive, inputs,
      (a, i) => ModifyRebuilder.Add(a, i, ReadEntries, BuildImage, largeVolumeCreator: this));
  }

  /// <summary>
  /// Removes the specified entry from the target container.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this, SyntheticNames);
      return;
    }

    Tux3InPlaceModifier.Remove(archive, entryNames,
      (a, n) => ModifyRebuilder.Remove(a, n, ReadEntries, BuildImage, largeVolumeCreator: this));
  }

  // ── Shared rebuild delegates (real WORM-table records only) ─────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Tux3InPlaceModifier.ReadRealEntries(ms.ToArray()).ToList();
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new Tux3Writer();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>Boot block, superblock, WORM-table prefixes and the tail padding are metadata; each record body is the file that owns it.</summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new Tux3Reader(image);
      var cursor = 0L;
      foreach (var e in reader.Entries
                 .Where(x => x.Offset >= 0 && x.Size > 0 && !SyntheticNames.Contains(x.Name))
                 .OrderBy(x => x.Offset)) {
        if (e.Offset > cursor)
          result.Add(new DefragBlockInfo(cursor, e.Offset - cursor, DefragBlockKind.MetadataReserved));
        result.Add(new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, e.Name));
        cursor = Math.Max(cursor, e.Offset + e.Size);
      }
      if (cursor == 0 && image.Length > 0)
        result.Add(new DefragBlockInfo(0, Math.Min(4096, image.Length), DefragBlockKind.MetadataReserved));
    } catch {
      // An image we cannot parse claims nothing, and a wipe of it would zero
      // every byte — so say it has no known extents and let the caller decide.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    // Records are packed to the byte, so there are no cluster tips to trim —
    // only the slack a removal or a shorter replacement left behind.
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }


  /// <summary>
  /// Re-lays the volume out with the requested geometry. The generic default
  /// would feed this reader's synthetic entries — the raw image and the
  /// metadata sheet — back in as files; they are excluded so the rebuilt
  /// volume holds the same files the original did.
  /// </summary>
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);

    var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
    if (options.Parameters != null)
      foreach (var kv in options.Parameters)
        parameters[kv.Key] = kv.Value;

    RebuildVerb.RebuildToStream(source, target, this, this,
      parameters.Count > 0 ? parameters : null, SyntheticNames);
  }

}
