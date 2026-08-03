#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ZxScl;

/// <summary>
/// Descriptor for ZX Spectrum SCL archives ("SINCLAIR" signature) — the
/// header+catalogue TR-DOS file container convertible to .trd images.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://sinclair.wiki.zxnet.co.uk/wiki/TR-DOS_filesystem</c> — the TR-DOS catalogue structures the SCL container carries</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/TR-DOS</c> — Wikipedia article — covers the SCL container</description></item>
///   <item><description>SCL format notes in ZX Spectrum emulator documentation (World of Spectrum formats reference)</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>A file's data is found by adding up the lengths of every file before
/// it — the directory records a length in sectors and nothing else, so position
/// is implied by order. That is the whole constraint on moving one: the
/// payloads have to stay packed against the directory and in the order it lists
/// them, and the layout the reader can walk is that one and no other.</para>
///
/// <para>Which is what a container we wrote already looks like, because
/// removing a file shifts the payloads back over the gap and truncates. A pass
/// over one of those finds nothing to move and says so, instead of writing the
/// whole container out again to arrive at the same bytes.</para>
/// </remarks>
public sealed class ZxSclFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty {

  // Upper bound: max payload (40 tracks x 16 sectors x 256 bytes x 4 layers) + magic/headers/CRC.
  public long? MaxTotalArchiveSize => ZxSclReader.MaxPayloadSize;
  public string AcceptedInputsDescription =>
    "ZX Spectrum TR-DOS file (up to 655 360 bytes total; 8-char names).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>
  /// SCL is variable-size — there's no fixed canonical byte count. We declare the hard
  /// payload ceiling so <see cref="IArchiveShrinkable"/>-style consumers still have a target.
  /// </summary>
  public IReadOnlyList<long> CanonicalSizes => [];

  public string Id => "ZxScl";
  public string DisplayName => "SCL (ZX Spectrum)";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing ZxScl archive via
  /// <see cref="ZxSclInPlaceModifier"/>. Each file is inserted with a single
  /// 14-byte right-shift of the payload region followed by an entry-header
  /// write and a sector-padded data append — no full image rebuild.
  /// Replacement of an existing same-named entry is handled by a prior
  /// in-place remove. SCL has no compression or random-access map so the
  /// trailing 32-bit checksum is recomputed once per mutation.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs))
      ZxSclInPlaceModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing ZxScl image via
  /// <see cref="ZxSclInPlaceModifier"/>. Later directory entries shift up by
  /// 14 bytes, the trailing payload region shifts back to close the gap, the
  /// stream is truncated and the trailing checksum is recomputed.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      ZxSclInPlaceModifier.RemoveFile(archive, name);
  }


  public string DefaultExtension => ".scl";
  public IReadOnlyList<string> Extensions => [".scl"];
  public IReadOnlyList<string> CompoundExtensions => [];

  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(ZxSclReader.Magic, Offset: 0, Confidence: 0.95)];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ZX Spectrum SCL archive (TR-DOS compact form)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new ZxSclReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new ZxSclReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var total = 0L;
    foreach (var i in inputs) if (!i.IsDirectory) total += i.InMemoryContent?.LongLength ?? new FileInfo(i.FullPath).Length;
    if (this.MaxTotalArchiveSize is long cap && total > cap)
      throw new InvalidOperationException(
        $"SCL: combined input size {total} bytes exceeds TR-DOS payload ceiling ({cap} bytes).");

    var w = new ZxSclWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // ── IArchiveDefragmentable (rebuild-based) ───────────────────────────

  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Moves the payloads that are out of place, and writes the container out
  /// again only when that cannot express what was asked.
  /// </summary>
  /// <remarks>
  /// <para>On a container we wrote, nothing is out of place: removing a file
  /// shifts the payloads back over the gap and truncates. The pass then finds
  /// no move to make and says so, which is a cheaper and truer answer than
  /// rebuilding the whole thing to arrive at the same bytes. What it is for is
  /// a container that arrived from somewhere else, with bytes behind the last
  /// payload the directory does not account for.</para>
  ///
  /// <para>What it cannot do is put a payload anywhere but directly behind the
  /// one before it. Position is implied by order here, so the layout the reader
  /// can walk is the packed one and no other; a mode asking for something else
  /// is refused by the mover, and the rebuild answers instead.</para>
  /// </remarks>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

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

    DefragRebuilder.Rebuild(archive, options, ReadEntries, BuildImage);
  }

  /// <summary>Largest container held in memory twice for the guarded pass.</summary>
  private const long PlannerImageCap = 64L * 1024 * 1024;

  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    using var reader = new ZxSclReader(stream);
    return reader.Entries.Where(e => !e.IsDirectory).Select(reader.Extract).ToList();
  }

  /// <summary>Plans a payload-level layout, moves the payloads and settles the directory.</summary>
  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    var mover = new ZxSclBlockMover();
    archive.Position = 0;
    mover.Init(archive);

    archive.Position = 0;
    var extents = ZxSclRecordMap.Enumerate(archive).ToList();
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

    // The directory is the only record of where a payload is, and one payload's
    // old place is routinely another's new one, so its order is written once
    // every move has landed.
    mover.Settle(archive);

    archive.Position = 0;
    var postExtents = ZxSclRecordMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  // ── IArchiveLayoutMap ────────────────────────────────────────────────

  /// <summary>
  /// Enumerates the byte layout of an SCL archive: 8-byte magic as
  /// MetadataReserved, 1-byte file count + N×14-byte headers as
  /// MetadataReserved, each file's sector-padded data region as Used,
  /// and the trailing 4-byte CRC as MetadataReserved.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    using var r = new ZxSclReader(archive);

    // Magic: 8 bytes
    yield return new DefragBlockInfo(0, 8, DefragBlockKind.MetadataReserved, "SINCLAIR magic");

    // File count (1 byte) + N × 14-byte headers
    var headerTableSize = 1 + r.Entries.Count * ZxSclReader.HeaderSize;
    yield return new DefragBlockInfo(8, headerTableSize, DefragBlockKind.MetadataReserved, "Directory");

    // File data regions
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.Size, DefragBlockKind.Used, e.Name);
    }

    // Trailing CRC: 4 bytes at end
    var crcOffset = archive.Length - 4;
    if (crcOffset > 0)
      yield return new DefragBlockInfo(crcOffset, 4, DefragBlockKind.MetadataReserved, "CRC32");
  }

  // ── IWipeEmpty ───────────────────────────────────────────────────────

  /// <summary>
  /// Zero-fills every byte of the SCL image that is not part of the header,
  /// the directory, a live sector-padded payload region, or the trailing CRC.
  /// SCL is densely packed by construction (removal physically compacts and
  /// truncates the stream), so on a well-formed image the only wipeable bytes
  /// are cluster tips: the slack between a file's true byte length (the
  /// TR-DOS param2 field of code/data entries) and its 256-byte sector-padded
  /// region. The wipe is bounded by the archive's own geometry — header +
  /// directory + Σ(LengthSectors × 256) + 4-byte CRC — never by the raw
  /// stream length, and the trailing 32-bit checksum is recomputed whenever
  /// any byte changed so the image stays self-consistent.
  /// <paramref name="wipeDeletedEntries"/> is accepted for interface parity
  /// but has nothing extra to do: SCL removal leaves no dead directory slots
  /// or orphaned payload behind.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);

    image.Position = 0;
    using var r = new ZxSclReader(image);

    // The archive's own extent: magic + count byte + directory + payload + CRC.
    var directoryEnd = 9L + r.Entries.Count * ZxSclReader.HeaderSize;
    var payloadEnd = directoryEnd;
    foreach (var e in r.Entries)
      payloadEnd += e.Size;
    var imageSize = Math.Min(image.Length, payloadEnd + 4);

    var extents = new List<DefragBlockInfo> {
      new(0, directoryEnd, DefragBlockKind.MetadataReserved, "Header+Directory"),
      new(payloadEnd, 4, DefragBlockKind.MetadataReserved, "CRC32"),
    };
    // TR-DOS stores only a sector count in the directory; param2 is the true
    // byte length for code ('C') and data ('D') entries, so only those tips
    // can be wiped without risking real payload bytes of other entry types.
    var trueSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in r.Entries) {
      if (e.Size <= 0)
        continue;
      extents.Add(new(e.DataOffset, e.Size, DefragBlockKind.Used, e.Name));
      if (e.FileType is 'C' or 'D' && e.Param2 > 0 && e.Param2 <= e.Size) {
        // The wiper looks tips up by name; if two entries share a display name
        // but disagree on length, wiping either could hit real bytes — skip both.
        if (!trueSizes.TryAdd(e.Name, e.Param2) && trueSizes[e.Name] != e.Param2)
          trueSizes[e.Name] = -1;
      } else {
        trueSizes[e.Name] = -1;
      }
    }

    var wiped = UnusedSpaceWiper.Wipe(
      image, extents, imageSize, wipeClusterTips,
      wipeClusterTips ? name => trueSizes.TryGetValue(name, out var s) ? s : -1 : null);

    // Any zeroed byte invalidates the trailing sum-of-bytes checksum — re-seal it.
    if (wiped > 0)
      ZxSclInPlaceModifier.WriteCrc(image, payloadEnd);

    return wiped;
  }

  // ── Shared delegates ─────────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new ZxSclReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new ZxSclWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }
}
