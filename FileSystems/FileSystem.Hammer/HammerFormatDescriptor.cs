#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Hammer;

/// <summary>
/// Read-only descriptor for HAMMER (DragonFly BSD original) filesystem images.
/// Surfaces the volume header at offset 0 plus a structured metadata bundle and
/// the raw image. Walking the HAMMER B-tree (zone blockmap → cluster → inode →
/// records) is explicitly out of scope (multi-week effort).
///
/// Magic: 8-byte uint64 <c>vol_signature = 0xC8414D4DC5523031</c> ("HAMMER01")
/// at offset 0, serialised LE on disk as <c>31 30 52 C5 4D 4D 41 C8</c>.
/// Confidence 0.85: an 8-byte magic value at offset 0 is high-confidence but
/// HAMMER lacks an additional sanity check at this stage of detection
/// (the <c>vol_fstype</c> UUID at offset 64 is not validated against a
/// well-known constant).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/DragonFlyBSD/DragonFlyBSD/blob/master/sys/vfs/hammer/hammer_disk.h</c></description></item>
///   <item><description><c>https://www.dragonflybsd.org/hammer/</c></description></item>
/// </list>
/// </summary>
public sealed class HammerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IArchiveCreatable, IFormatOptionsSchema, ILayoutOptimizable , IFilesystemExtentMap, IWipeEmpty {

  /// <summary>
  /// Sole tunable the HAMMER writer honours: the filesystem label
  /// (<c>newfs_hammer -L</c>), written into the volume header and the PFS#0
  /// data and surfaced back as <c>vol_label</c>. Volume size is intentionally
  /// not exposed — the UNDO-FIFO floor pins it at ~1 GB regardless. An empty
  /// label falls back to the writer default ("hammer").
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Label", DisplayName: "Filesystem label", Kind: FormatOptionKind.String, Default: "",
      Description: "Volume label (newfs_hammer -L); max 63 ASCII chars."),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Hammer";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "HAMMER (DragonFly BSD)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".hammer";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".hammer"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(HammerVolumeOndisk.MagicBytesLE, Offset: 0, Confidence: 0.85),
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
  public string Description =>
    "HAMMER (DragonFly BSD original) filesystem image — volume header surface only. " +
    "WORM emit deferred: HAMMER1 requires a real cluster B-tree (zone blockmap → " +
    "cluster → inode → records with hammer_crc_t CRCs across every node), a per-volume " +
    "TID generator with monotonic ordering across the whole transaction log, and a " +
    "valid undo-fifo head/tail — none of which we can validate without a running " +
    "DragonFly BSD instance. Multi-week effort, deferred to a future phase.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      // Stream blew up before we got anywhere — irreducible minimum.
      entries.Add(new ArchiveEntryInfo(0, "FULL.hammer", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    HammerVolumeOndisk hdr;
    try {
      hdr = HammerVolumeOndisk.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.hammer", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    // Walk the B-Tree for the real files. The header parse above used a bounded
    // read; re-read the whole image for the walk only when the header is valid
    // (a deliberately-opened HAMMER archive, not speculative carving).
    var found = hdr.Valid ? ReadFiles(stream) : [];

    // A volume that carries files lists exactly those. Surfacing the synthetic
    // header entries alongside them would make every rebuild (shrink, defrag)
    // fold them back in as real files, so they stay on the carver path — empty
    // or foreign images, where the header IS all we can offer.
    var idx = 0;
    if (found.Count == 0) {
      entries.Add(new ArchiveEntryInfo(idx++, "FULL.hammer", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
      if (hdr.Valid)
        entries.Add(new ArchiveEntryInfo(idx++, "volume_header.bin", hdr.HeaderRaw.LongLength, hdr.HeaderRaw.LongLength, "stored", false, false, null));
      return entries;
    }

    foreach (var f in found)
      entries.Add(new ArchiveEntryInfo(idx++, f.Path, f.Content.LongLength, f.Content.LongLength, "stored", false, false, null));

    return entries;
  }

  // Walks the HAMMER B-Tree and returns the regular files it holds. Never throws
  // — returns nothing when the walk fails.
  private static IReadOnlyList<HammerReader.FileEntry> ReadFiles(Stream stream) {
    try {
      if (stream.CanSeek)
        stream.Position = 0;
      using var reader = HammerReader.Open(stream);
      return reader.ReadFiles();
    } catch {
      return [];
    }
  }

  /// <summary>
  /// Produces a fresh, mountable single-volume HAMMER image from <paramref name="inputs"/>.
  /// HAMMER's UNDO FIFO floor forces a volume size of ~1 GB minimum; see
  /// <see cref="HammerWriter"/>. Each input becomes an inode + directory-entry +
  /// data record in the global B-Tree. The DragonFly kernel mounts the image and
  /// reads every file's contents byte-exact (validated via <c>mount_hammer</c> +
  /// <c>cksum</c>, including multi-block files spanning the large- and small-data
  /// zones); the image also passes <c>hammer show</c> and <c>hammer checkmap</c>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var writer = new HammerWriter();
    var label = options?.GetOption("Label", "hammer");
    if (!string.IsNullOrEmpty(label))
      writer.Label = label;

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      writer.AddFile(input.ArchiveName, input.ReadContent());
    }

    // The default 1 GB is the UNDO-FIFO floor, not a ceiling: grow the volume to
    // whatever the payload needs or the blockmap runs out of big-blocks.
    writer.VolumeSize = Math.Max(writer.VolumeSize, writer.ComputeAutoSize());
    writer.WriteTo(output);
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    HammerVolumeOndisk hdr;
    try {
      hdr = HammerVolumeOndisk.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.hammer", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    // Materialise the real files by walking the B-Tree. The header parse above
    // used a bounded read, so re-read the whole image for the walk. The header
    // surface is written only for a volume that holds no files, mirroring List.
    var found = hdr.Valid ? ReadFiles(stream) : [];
    if (found.Count > 0) {
      foreach (var f in found)
        WriteIfMatch(outputDir, f.Path, f.Content, files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.hammer", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hdr), files);
    if (hdr.Valid)
      WriteIfMatch(outputDir, "volume_header.bin", hdr.HeaderRaw, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(HammerVolumeOndisk h) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={(h.Valid ? "ok" : "partial")}\n");
    b.Append(ic, $"vol_signature=0x{h.VolSignature:X16}\n");
    if (h.Valid) {
      b.Append(ic, $"vol_label={h.VolLabel}\n");
      b.Append(ic, $"vol_no={h.VolNo}\n");
      b.Append(ic, $"vol_count={h.VolCount}\n");
      b.Append(ic, $"vol_version={h.VolVersion}\n");
      b.Append(ic, $"vol_flags=0x{h.VolFlags:X8}\n");
      b.Append(ic, $"vol_rootvol={h.VolRootVol}\n");
      b.Append(ic, $"vol_crc=0x{h.VolCrc:X8}\n");
      b.Append(ic, $"fs_uuid_hex={h.VolFsidHex}\n");
      b.Append(ic, $"fs_type_uuid_hex={h.VolFsTypeHex}\n");
      b.Append(ic, $"vol_bot_beg=0x{h.VolBotBeg:X16}\n");
      b.Append(ic, $"vol_mem_beg=0x{h.VolMemBeg:X16}\n");
      b.Append(ic, $"vol_buf_beg=0x{h.VolBufBeg:X16}\n");
      b.Append(ic, $"vol_buf_end=0x{h.VolBufEnd:X16}\n");
      b.Append(ic, $"vol0_btree_root=0x{h.Vol0BtreeRoot:X16}\n");
      b.Append(ic, $"vol0_next_tid=0x{h.Vol0NextTid:X16}\n");
      b.Append(ic, $"vol0_stat_bigblocks={h.Vol0StatBigblocks}\n");
      b.Append(ic, $"vol0_stat_freebigblocks={h.Vol0StatFreeBigblocks}\n");
      b.Append(ic, $"vol0_stat_inodes={h.Vol0StatInodes}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // Bounded read — must NOT pull multi-GB images into memory when the carver
  // runs us speculatively. The HAMMER volume header lives entirely in the first
  // ~2 KB; 64 KB is comfortable headroom.
  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Lays the volume out again. A file's bytes live in data records whose
  /// B-tree elements carry the offset they start at, so a move is the copy,
  /// that field, and the checksum over the node the element lives in —
  /// cheaper than reading every file out and writing a fresh volume, which is
  /// what the inherited default did for the one mode it offered.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // The in-place pass is kept only if every payload still reads back: it can
    // refuse partway — a destination past what the freemap accounts for is not
    // taken, because the freemap is not rewritten here — and a rebuild is the
    // honest answer when it does.
    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => ReadFileEntries(stream).Select(e => e.Data).ToList(),
      inPlace: () => DefragmentWithPlanner(archive, options),
      rebuild: () => DefragRebuilder.Rebuild(archive, options,
        readEntries: stream => ReadFileEntries(stream).ToList(),
        buildImage: files => {
          var writer = new HammerWriter();
          foreach (var (name, data) in files) writer.AddFile(name, data);
          writer.VolumeSize = Math.Max(writer.VolumeSize, writer.ComputeAutoSize());
          using var built = new MemoryStream();
          writer.WriteTo(built);
          var bytes = built.ToArray();
          if (bytes.Length >= archive.Length) return bytes;
          var padded = new byte[archive.Length];
          Array.Copy(bytes, padded, bytes.Length);
          return padded;
        }));
  }

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new HammerBlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var extents = HammerExtentMap.Enumerate(archive).ToList();
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
    var postExtents = HammerExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>Every file's name and bytes, for the rebuild and the guard.</summary>
  private static List<(string Name, byte[] Data)> ReadFileEntries(Stream stream) {
    stream.Position = 0;
    using var reader = HammerReader.Open(stream);
    return reader.ReadFiles().Select(f => (f.Path, f.Content)).ToList();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the extents.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => HammerExtentMap.Enumerate(image);

  /// <summary>
  /// Zero-fills everything the freemap leaves unallocated: free big-blocks
  /// outright, and the tail of a partly-used one past its append point.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = HammerExtentMap.Enumerate(image).ToList();
    if (extents.Count == 0) return 0;
    // The freemap accounts per big-block, not per file, so there is no
    // per-file tail for the wiper to trim.
    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
