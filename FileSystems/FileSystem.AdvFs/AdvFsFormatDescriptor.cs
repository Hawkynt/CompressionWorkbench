#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.AdvFs;

/// <summary>
/// Read-only descriptor for AdvFS (Tru64 UNIX Advanced File System, DEC/HP).
/// Open-sourced by HP in 2008 under the GPL; the storage domain → file set →
/// file model and the on-disk structures are described in <c>bs_ods.h</c>,
/// <c>bs_disk_block.h</c>, and <c>bs_public.h</c> of that release.
///
/// Walking the BMT (Bitfile Metadata Table) B-tree and following BFD
/// (Bitfile Descriptor) extent chains to extract user files is explicitly
/// out of scope (multi-week effort) — this descriptor surfaces:
/// <list type="bullet">
///   <item><description><c>FULL.advfs</c> — the raw image bytes</description></item>
///   <item><description><c>metadata.ini</c> — parsed BSR_DMN_ATTR/BSR_VD_ATTR/BSR_DMN_MATTR fields</description></item>
///   <item><description><c>rbmt_page0.bin</c> — 4 KB capture of RBMT page 0 (offset 131072)</description></item>
/// </list>
///
/// Detection: a 16-byte cookie <c>"ADVFS\0RBMT0\0\0\0\0\0"</c> at offset
/// 131072 (= page 16 × 8192-byte AdvFS page). This is an internal convention
/// rather than the canonical Tru64 on-disk magic (record type discriminators
/// rather than a fixed bytes-at-offset signature). Real Tru64 images that
/// don't carry the cookie will not auto-detect but can still be parsed when
/// fed to the descriptor directly.
///
/// Create / Modify: a clean-room AdvFS-WB storage-domain layout with a flat
/// file table inside RBMT page 0; <see cref="AdvFsInPlaceModifier"/> performs
/// genuine in-place add/replace/remove against that table.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://sourceforge.net/projects/advfs/</c> — HP 2008 GPL release</description></item>
///   <item><description>HP "AdvFS Technical Reference" (in the source tarball)</description></item>
///   <item><description>Wikipedia "Advanced File System"</description></item>
/// </list>
/// </summary>
public sealed class AdvFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable, IArchiveShrinkable, IArchiveModifiable, IArchiveCreatable, IFormatOptionsSchema, ILayoutOptimizable , IFilesystemExtentMap, IWipeEmpty {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The one tunable the WORM writer honours: the textual volume tag stamped
  /// into the BSR_VD_ATTR record (64-byte field, capped at 63 ASCII bytes).
  /// <see cref="AdvFsWriter.SetVolumeTag"/> writes it and
  /// <see cref="AdvFsReader.VolumeTag"/> reads it back, so the knob round-trips.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 63),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "AdvFs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "AdvFS (Tru64 UNIX)";
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
public string DefaultExtension => ".advfs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".advfs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(AdvFsReader.DetectionCookie, Offset: (int)AdvFsReader.RbmtPageOffset, Confidence: 0.80f),
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
    "AdvFS (Tru64 UNIX Advanced File System) image — header parse + WORM emit of a clean-room storage-domain layout (RBMT page 0 cookie + DMN/VD/MATTR fields + AdvFS-WB file-table extension).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.advfs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    AdvFsReader reader;
    try {
      using var ms = new MemoryStream(image, writable: false);
      reader = new AdvFsReader(ms);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.advfs", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.advfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (reader.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "rbmt_page0.bin", reader.HeaderRaw.LongLength, reader.HeaderRaw.LongLength, "stored", false, false, null));
    foreach (var f in reader.FileTableEntries)
      entries.Add(new ArchiveEntryInfo(idx++, f.Name, f.Size, f.Size, "stored", false, false, null));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // A seekable domain is walked in place: ReadAllBounded stops at FullReadCap,
    // so buffering silently truncated anything larger -- and FULL.advfs would
    // double the image in memory on top of that.
    if (stream.CanSeek && stream.Length > FullReadCapBytes) {
      ExtractStreaming(stream, outputDir, files);
      return;
    }

    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    AdvFsReader reader;
    try {
      using var ms = new MemoryStream(image, writable: false);
      reader = new AdvFsReader(ms);
    } catch {
      WriteIfMatch(outputDir, "FULL.advfs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.advfs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(reader), files);
    if (reader.Valid)
      WriteIfMatch(outputDir, "rbmt_page0.bin", reader.HeaderRaw, files);
    foreach (var f in reader.FileTableEntries)
      WriteIfMatch(outputDir, f.Name, reader.ExtractFile(f), files);
  }

  /// <summary>Largest domain this descriptor will hold in memory while extracting.</summary>
  private const long FullReadCapBytes = 64L * 1024 * 1024;

  /// <summary>
  /// Extraction for a domain too large to buffer: FULL.advfs is copied through
  /// and each payload is streamed straight out of the image.
  /// </summary>
  private static void ExtractStreaming(Stream stream, string outputDir, string[]? files) {
    stream.Position = 0;
    var reader = new AdvFsReader(stream);

    if (files == null || files.Length == 0 || MatchesFilter("FULL.advfs", files)) {
      stream.Position = 0;
      using var full = CreateEntryFile(outputDir, "FULL.advfs");
      stream.CopyTo(full);
    }
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(reader), files);
    if (reader.Valid)
      WriteIfMatch(outputDir, "rbmt_page0.bin", reader.HeaderRaw, files);

    foreach (var f in reader.FileTableEntries) {
      if (files != null && files.Length > 0 && !MatchesFilter(f.Name, files)) continue;
      using var target = CreateEntryFile(outputDir, f.Name);
      reader.ExtractFileTo(f, target);
    }
  }

  /// <summary>
  /// WORM-emits a fresh AdvFS storage-domain image carrying the supplied
  /// <paramref name="inputs"/>. Layout: zero-filled bootstrap pages 0..15,
  /// RBMT page 0 at offset 131072 with the detection cookie + DMN/VD/MATTR
  /// fields + AdvFS-WB file table, then a flat data area starting at offset
  /// 139264 holding each file's payload back-to-back.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var w = new AdvFsWriter(output, leaveOpen: true);
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label))
      w.SetVolumeTag(label);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the domain out; reading a large input
      // into a byte[] would cap it at what an array can hold.
      // Full path, not the leaf: AdvFS records the name verbatim and the
      // round-trip tests expect nested paths back unchanged.
      if (info.InMemoryContent is { } bytes)
        w.AddFile(info.ArchiveName, bytes);
      else
        w.AddStreamingFile(info.ArchiveName, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    w.Finish();
  }

  // ── IArchiveModifiable (true in-place R/W) ──────────────────────────
  //
  // AdvFsInPlaceModifier appends new payloads to the flat data area, rewrites
  // only the touched file-table rows inside RBMT page 0, and leaves every other
  // payload + table row byte-identical. When the file table would overflow the
  // 8 KB RBMT page (or the header can't be parsed) it falls back to
  // ModifyRebuilder so the user always gets a working image.

  /// <summary>
  /// Adds the supplied entry to the target container.
  /// </summary>
public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier reads the volume into an array to walk its
    // structures, which a volume past two gigabytes does not fit in. Above that
    // the edit is applied by unpacking and relaying the volume out instead.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

    AdvFsInPlaceModifier.Add(archive, inputs,
      (a, i) => ModifyRebuilder.Add(a, i, ReadEntries, BuildImage, largeVolumeCreator: this));
  }

  /// <summary>
  /// Removes the specified entry from the target container.
  /// </summary>
public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    AdvFsInPlaceModifier.Remove(archive, entryNames,
      (a, n) => ModifyRebuilder.Remove(a, n, ReadEntries, BuildImage, largeVolumeCreator: this));
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Below the streaming cap the layout is changed by moving what is out of
    // place: a file here is one run of bytes and its file table row holds the
    // absolute offset it starts at, so a move is the copy plus eight bytes.
    // Until this was here a domain under the cap fell through every branch and
    // Defragment returned having done nothing at all.
    if (!(archive.CanSeek && archive.Length > FullReadCapBytes)
        && options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd
                        or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      // The in-place pass is kept only if every payload still reads back: it
      // can refuse partway, and a rebuild is the honest answer when it does.
      DefragContentGuard.RunOrRebuild(archive,
        readContents: stream => {
          stream.Position = 0;
          var reader = new AdvFsReader(stream);
          return reader.FileTableEntries.Select(reader.ExtractFile).ToList();
        },
        inPlace: () => this.DefragmentWithPlanner(archive, options),
        rebuild: () => DefragRebuilder.Rebuild(archive, options,
          readEntries: ReadEntries,
          buildImage: files => {
            var built = BuildImage(files);
            if (built.Length >= archive.Length) return built;
            var padded = new byte[archive.Length];
            Array.Copy(built, padded, built.Length);
            return padded;
          }));
      return;
    }

    // A domain too large to materialise goes through the streaming rebuilder;
    // BuildImage returns a byte[] of the whole image, and ReadEntries buffers
    // the source, both of which stop at the array limit.
    // Every mode streams above the cap: end-pack and carve-hole order their
    // entries from scratch inside the rebuilder, so none of them falls back
    // to a buffered rebuild the volume is too large for.
    if (archive.CanSeek && archive.Length > FullReadCapBytes) {
      AdvFsWriter? streamWriter = null;
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => {
          stream.Position = 0;
          var r = new AdvFsReader(stream);
          return r.FileTableEntries.Select(f => (f.Name, r.ExtractFile(f))).ToList();
        },
        beginWrite: s2 => streamWriter = new AdvFsWriter(s2, leaveOpen: true),
        // As a stream factory, not inline: an inline payload would go back into
        // the buffer this path exists to avoid.
        writeEntry: (name, data) => streamWriter!.AddStreamingFile(
          name, data.LongLength, () => new MemoryStream(data, writable: false)),
        finishWrite: () => { streamWriter!.Finish(); streamWriter.Dispose(); });
    }
  }

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new AdvFsBlockMover();
    mover.Init(archive);

    var extents = this.EnumerateExtents(archive).ToList();
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
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  // ── ILayoutOptimizable ─────────────────────────────────────────────────

  /// <summary>
  /// Relays the domain through the writer at the requested geometry.
  /// </summary>
  /// <remarks>
  /// The generic default drives its rebuild off the surface entry list, which
  /// carries the three synthetic views this reader exposes — the whole-image
  /// view, the metadata sheet and the RBMT page — alongside the real files.
  /// Rebuilding from that wrote the views into the new domain as files, so the
  /// entry set grew every pass and the verb refused the result as lossy. The
  /// file table is the list of what the domain actually holds.
  /// </remarks>
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);

    source.Position = 0;
    var reader = new AdvFsReader(source);
    using (var writer = new AdvFsWriter(target, leaveOpen: true)) {
      foreach (var entry in reader.FileTableEntries)
        writer.AddFile(entry.Name, reader.ExtractFile(entry));
      writer.Finish();
    }

    // A caller that asked for a larger domain gets one: the file table records
    // absolute offsets, so trailing free space costs nothing to describe.
    if (options.ImageSize > target.Length && target.CanSeek)
      target.SetLength(options.ImageSize);
  }

  // ── Shared rebuild delegates ────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var image = ReadAllBounded(stream);
    using var ms = new MemoryStream(image, writable: false);
    var reader = new AdvFsReader(ms);
    return reader.FileTableEntries.Select(f => (f.Name, reader.ExtractFile(f))).ToList();
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files)
    => AdvFsWriter.Build(files.Select(f => (f.Name, f.Data)));

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(AdvFsReader r) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={r.ParseStatus}\n");
    if (r.Valid) {
      b.Append(ic, $"domain_id_hex={r.DomainIdHex}\n");
      b.Append(ic, $"mount_id=0x{r.MountId:X16}\n");
      b.Append(ic, $"on_disk_version={r.OnDiskVersion}\n");
      b.Append(ic, $"vd_index={r.VdIndex}\n");
      b.Append(ic, $"vd_count={r.VdCount}\n");
      b.Append(ic, $"state=0x{r.State:X8}\n");
      b.Append(ic, $"vd_blk_cnt={r.VdBlkCnt}\n");
      b.Append(ic, $"vd_meta_blk_cnt={r.VdMetaBlkCnt}\n");
      b.Append(ic, $"volume_tag={r.VolumeTag}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // Capture header area + AdvFS-WB writer's bundled file payloads. The detection
  // cookie lives at offset 131072, the file payload area starts at 139264; 64 MB
  // is comfortable headroom for round-tripping a worktest payload set while
  // still bounding speculative carver scans far below the host's available RAM.
  private const int ReadCap = 64 * 1024 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < ReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// Everything ahead of the first payload is structure — the RBMT pages and
  /// the writer's directory — and each entry claims the bytes it was written
  /// to. What no entry claims is space a removal or a shorter replacement left.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      var reader = new AdvFsReader(image);
      var first = long.MaxValue;
      foreach (var e in reader.Entries) {
        if (e.IsDirectory || e.Offset < 0 || e.Size <= 0) continue;
        result.Add(new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, e.Name));
        first = Math.Min(first, e.Offset);
      }
      if (first == long.MaxValue) first = Math.Min(image.Length, 64 * 1024);
      result.Add(new DefragBlockInfo(0, first, DefragBlockKind.MetadataReserved));
    } catch {
      // An image we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
  /// <summary>
  /// Performs the wipe unused space operation.
  /// </summary>
public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    // Payloads are packed to the byte, so there are no cluster tips to trim.
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
