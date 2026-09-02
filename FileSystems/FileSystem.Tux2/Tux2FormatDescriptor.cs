#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux2;

/// <summary>
/// Read+WORM descriptor for TUX2 — Daniel Phillips's 2002 phase-tree
/// filesystem proposal (OLS 2002 paper, never-stabilised research format).
/// Recognises a deterministic header pattern (magic "TUX2FS\0\0" at offset 0)
/// so research images we generate round-trip through the reader. Writer emits
/// a single-phase image only (no alpha/beta phases, no version chain) — real
/// legacy prototype images would need a custom parser matching the specific
/// snapshot of the in-progress code that produced them.
///
/// References:
/// <list type="bullet">
///   <item><description>Daniel Phillips, "The Tux2 Filesystem" (Ottawa Linux Symposium 2002 proceedings) — the defining paper</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Tux3</c> — Wikipedia article covering the phase-tree lineage</description></item>
/// </list>
/// </summary>
/// <summary>
/// Why there is nothing here to lay out again.
/// </summary>
/// <remarks>
/// <para>The records run end to end from the header: a name length, the name, a
/// data length, the data, then the next one. The reader walks that by adding
/// each record's length to a cursor, so a gap anywhere makes everything after
/// it unreadable — the only layout this format can express is packed from the
/// front.</para>
///
/// <para>Which is the layout it is always already in. Removing a file writes
/// the container out compacted rather than leaving a hole, so there is never
/// space between records to close up. A pass over one of these would find
/// nothing to move on every volume it was ever handed.</para>
/// </remarks>
public sealed class Tux2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  // ── Synthetic, non-file entries the reader always surfaces ──────────────
  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.OrdinalIgnoreCase) { "FULL.tux2", "metadata.ini" };

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The single tunable the single-phase WORM writer honours: the on-disk
  /// format version stamped into the header at offset 0x08. <see cref="Tux2Writer.Version"/>
  /// is written verbatim and <see cref="Tux2Reader.Version"/> reads it back, so
  /// the knob round-trips. Defaults to 1 (the version the reader documents).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Version", DisplayName: "Image version", Kind: FormatOptionKind.Integer, Default: "1",
      Description: "Format version stamped into the TUX2 header at offset 0x08."),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Tux2";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "TUX2";
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
  public string DefaultExtension => ".tux2";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".tux2"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("TUX2FS\0\0"u8.ToArray(), Offset: 0, Confidence: 0.90),
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
  public string Description => "TUX2 phase-tree research filesystem (Daniel Phillips, OLS 2002) — single-phase synthetic image.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Tux2Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Tux2Reader(stream);
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
  /// Emits a fresh single-phase TUX2 image: 16-byte header (magic + version +
  /// file count) followed by per-file records (u16 name length, UTF-8 name,
  /// u32 data length, raw bytes). Round-trips through <see cref="Tux2Reader"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var version = (uint)Math.Max(0, options.GetOptionInt("Version", 1));
    var w = new Tux2Writer { Version = version };
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
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
    using var reader = new Tux2Reader(stream);
    return reader.Entries
      .Where(e => !SyntheticNames.Contains(e.Name))
      .Select(reader.Extract)
      .ToList();
  }

  /// <summary>Plans a record-level layout and moves the records into it.</summary>
  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Tux2BlockMover();

    archive.Position = 0;
    var extents = Tux2RecordMap.Enumerate(archive).ToList();
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
    var postExtents = Tux2RecordMap.Enumerate(archive).ToList();
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
        "Tux2 defragmentation cannot carve a hole: the rebuild always start-packs the volume.");

    var tempPath = Path.GetTempFileName();
    try {
      using (var temp = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite)) {
        using (var reader = new Tux2Reader(archive)) {
          var w = new Tux2Writer();
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
  // Tux2InPlaceModifier appends/overwrites inline records, leaving the header
  // and all preceding records byte-identical at their original offsets. New
  // entries are append-only; same-size replaces overwrite data in place;
  // resize/delete tail-rewrite from the changed record onward (still O(tail)).
  // The rebuild fallback only fires on a malformed image.

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

    Tux2InPlaceModifier.Add(archive, inputs,
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

    Tux2InPlaceModifier.Remove(archive, entryNames,
      (a, n) => ModifyRebuilder.Remove(a, n, ReadEntries, BuildImage, largeVolumeCreator: this));
  }

  // ── Shared rebuild delegates (exclude the reader's synthetic entries) ────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Tux2InPlaceModifier.ReadRealEntries(ms.ToArray()).ToList();
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new Tux2Writer();
    foreach (var (n, d) in files)
      if (!SyntheticNames.Contains(n))
        w.AddFile(n, d);
    return w.Build();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>Header, per-record prefixes and any tail slack are metadata; each record body is the file that owns it.</summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new Tux2Reader(image);
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
