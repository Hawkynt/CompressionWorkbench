#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.BcacheFs;

/// <summary>
/// Descriptor for bcachefs volumes: a superblock at offset 4096, and b-trees under
/// it holding the names, the metadata and the positions of every file's bytes.
/// Volumes written here are read by the kernel driver, and read back by
/// <see cref="BcacheFsReader" /> — which understands both the packed keys
/// <c>mkfs.bcachefs</c> writes and the plain ones this project does.
///
/// <para>What such a volume does not carry is allocation information: the trees a
/// running filesystem keeps so it can decide where to write next. bcachefs's own
/// image tooling leaves them out too, and rebuilds them on the first read-write
/// mount. Which of the two mounts a volume is written for is an option; the
/// default is reading. See <see cref="BcacheFsWriter" />.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://bcachefs.org</c> — official site, incl. the "Principles of Operation" on-disk documentation</description></item>
///   <item><description><c>https://github.com/koverstreet/bcachefs</c> — canonical source tree (Kent Overstreet); <c>bcachefs_format.h</c> defines <c>bch_sb</c></description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Bcachefs</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class BcacheFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// What the writer can be asked for. <c>VolumeLabel</c> is the superblock's
  /// 32-byte label and <c>ImageSize</c> is the volume's capacity, which must leave
  /// room for the superblock copies. The 512-byte block size is fixed, so it is not
  /// offered.
  /// </summary>
  /// <remarks>
  /// There was a third, <c>MountFor</c>, choosing whether the volume was written to
  /// be mounted read-only or read-write. It existed because a volume written whole
  /// carried no allocation information and had to ask to be let past the check that
  /// would have built it, and the two mounts wanted opposite bits set for that. The
  /// allocation trees are written now, so one volume serves both and there is
  /// nothing left to choose.
  /// </remarks>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 31),
    FilesystemSchemaPresets.ImageSize(["128 MB", "256 MB", "512 MB"],
      description: "Total image capacity. Must be at least 128 MB so the superblock copies fit."),
  ];

  public string Id => "BcacheFs";
  public string DisplayName => "BcacheFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".bcachefs";
  public IReadOnlyList<string> Extensions => [".bcachefs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 16-byte BcacheFS magic UUID at file offset 4120 (= 4096-byte pre-area +
    // 24 bytes into struct bch_sb to skip csum/version/version_min/pad).
    new(BcacheFsSuperblock.MagicUuid, Offset: BcacheFsSuperblock.MagicOffset, Confidence: 0.85f),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "BcacheFS Linux filesystem image — R/W (WORM, SB-validated only — fsck parity pending).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.bcachefs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    BcacheFsSuperblock sb;
    try {
      sb = BcacheFsSuperblock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.bcachefs", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    // A volume that carries files lists exactly those. Surfacing the synthetic
    // header entries alongside them would make every rebuild (shrink, defrag)
    // fold them back in as real files, so they stay on the carver path — empty
    // or foreign images, where the header IS all we can offer.
    var payload = ReadPayload(stream);
    if (payload.Count > 0) {
      var idx = 0;
      foreach (var e in payload)
        entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.bcachefs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(2, "superblock.bin", sb.RawBytes.LongLength, sb.RawBytes.LongLength, "stored", false, false, null));
    return entries;
  }

  /// <summary>The files the volume holds. Never throws — empty when it holds none.</summary>
  private static IReadOnlyList<BcacheFsReader.Entry> ReadPayload(Stream stream) {
    try {
      if (stream.CanSeek) stream.Position = 0;
      using var reader = new BcacheFsReader(stream);
      return reader.Entries;
    } catch {
      return [];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    BcacheFsSuperblock sb;
    try {
      sb = BcacheFsSuperblock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.bcachefs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    // A volume that carries files extracts exactly those, mirroring List.
    if (stream.CanSeek) stream.Position = 0;
    using (var reader = new BcacheFsReader(stream)) {
      if (reader.Entries.Count > 0) {
        foreach (var e in reader.Entries) {
          if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
          var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
          Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
          using var output = File.Create(target);
          reader.ExtractTo(e, output);
        }
        return;
      }
    }

    WriteIfMatch(outputDir, "FULL.bcachefs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "superblock.bin", sb.RawBytes, files);
  }

  /// <summary>
  /// Writes a bcachefs volume: the superblock and the layout naming its copies, the
  /// member entry describing the single device, and the b-trees that hold the input
  /// files' names, metadata and bytes.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new BcacheFsWriter();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label))
      w.SetLabel(label);

    var sizes = new List<long>();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var length = i.InMemoryContent?.LongLength ?? new FileInfo(i.FullPath).Length;
      sizes.Add(length);
      if (i.InMemoryContent is { } bytes) {
        w.AddFile(i.ArchiveName, bytes);
        continue;
      }
      var path = i.FullPath;
      w.AddStreamingFile(i.ArchiveName, length, () => File.OpenRead(path));
    }

    // The requested size is a floor: the volume has to be at least large enough for
    // its b-trees and every file's bytes.
    var sizeBytes = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
    w.SetImageSize(Math.Max(sizeBytes, BcacheFsWriter.EstimateSize(sizes)));
    w.WriteTo(output);
  }

  // ── In-place R/W assessment: LEFT REBUILDING (no CanModify) ───────────────
  //
  // Genuine in-place add is impossible here and cannot even be verified: the
  // writer emits an SB-only image (struct bch_sb + four-copy bch_sb_layout +
  // members_v2) with NO b-tree — extents/dirents/inodes are never written, and
  // Create() deliberately puts no file content into the image. There is also
  // no reader that walks the b-tree object graph, so a round-trip of any added
  // file is unobservable. The descriptor therefore keeps create-only WORM
  // semantics and does NOT advertise FormatCapabilities.CanModify; a genuine
  // R/W path is gated on first building a bcachefs b-tree reader + writer (the
  // documented multi-week follow-up).

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Lays the volume's files out as asked, moving them where that can be done and
  /// writing the volume out again where it cannot.
  /// </summary>
  /// <remarks>
  /// Moving is the first choice because in bcachefs it is cheap: where a run of
  /// bytes sits is one word in one key of the extents b-tree, so a move is the copy
  /// plus that word. The rebuild is kept for the layouts the planner declines and
  /// for the case where the moved volume no longer reads back.
  /// </remarks>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the volume out again, and it is
    // also what lets every mode work: the rebuild can only lay files out from the
    // front, so it answers for two of the modes and nothing else.
    {
      var planned = false;
      // The in-place pass is kept only if every payload still reads back: it
      // can refuse partway, and a rebuild is the honest answer when it does.
      DefragContentGuard.RunOrRebuild(archive,
        readContents: stream => ReadEntries(stream).Select(e => e.Data).ToList(),
        inPlace: () => { this.DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }

    if (options.Mode is not (DefragMode.ConsolidateAtStart or DefragMode.FillHolesLazy))
      throw new NotSupportedException(
        $"BcacheFS defragmentation supports ConsolidateAtStart and FillHolesLazy; got {options.Mode}.");

    Stream? target = null;
    var spill = new List<(string Name, string Path, long Size)>();
    DefragRebuilder.RebuildStreaming(archive, options,
      readEntries: ReadEntries,
      beginWrite: s => target = s,
      writeEntry: (name, data) => {
        // The image has to be sized before the first byte is written, so the
        // entries are collected first and the writer is built in finishWrite.
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, data);
        spill.Add((name, path, data.LongLength));
      },
      finishWrite: () => {
        try {
          var w = new BcacheFsWriter();
          w.SetImageSize(BcacheFsWriter.EstimateSize(spill.ConvertAll(e => e.Size)));
          foreach (var (name, path, size) in spill) {
            var captured = path;
            w.AddStreamingFile(name, size, () => File.OpenRead(captured));
          }
          w.WriteTo(target!);
        } finally {
          foreach (var (_, path, _) in spill)
            try { File.Delete(path); } catch { /* scratch file already gone */ }
        }
      });
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    using var reader = new BcacheFsReader(stream);
    foreach (var e in reader.Entries) {
      using var buffer = new MemoryStream();
      reader.ExtractTo(e, buffer);
      yield return (e.Name, buffer.ToArray());
    }
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(BcacheFsSuperblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"uuid={sb.Uuid}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"user_uuid={sb.UserUuid}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"label={sb.Label}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version={sb.FormatVersion()}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_raw={sb.Version}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_min_raw={sb.VersionMin}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={sb.BlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"dev_idx={sb.DevIdx}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"nr_devices={sb.NrDevices}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"u64s={sb.U64s}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"offset={sb.Offset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"seq={sb.Seq}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  // Bounded read — only the superblock area (offset 4096 + ~1 KiB) is needed.
  // The 64 KiB cap keeps speculative carver scans from materialising multi-GB
  // candidate windows.
  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

  // ── IFilesystemExtentMap + IWipeEmpty ─────────────────────────────────

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new BcacheFsBlockMover();
    mover.Init(archive);

    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    // The volume ends before the file does: the last superblock slot sits at the
    // tail, and a layout that runs into it writes over the copy a reader falls
    // back on.
    var volumeEnd = archive.Length - (long)BcacheFsFormat.SbSlotSectors * BcacheFsFormat.SectorSize;

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, volumeEnd, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      volumeEnd, reinitAfterMove: null);

    // Every pointer the pass moved is written back at once: they all live in one
    // b-tree node, under one checksum.
    mover.Settle(archive);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// Reports the volume's layout: everything up to the first file's bytes is the
  /// superblock, the journal and the b-trees; each file's extents are its own; and
  /// the slot at the tail holds the last copy of the superblock.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    List<DefragBlockInfo> result = [];
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new BcacheFsReader(image);
      if (!reader.Valid) return [];

      List<DefragBlockInfo> files = [];
      foreach (var entry in reader.Entries)
        foreach (var extent in entry.Extents)
          files.Add(new DefragBlockInfo(extent.FirstSector * BcacheFsFormat.SectorSize,
            (long)extent.Sectors * BcacheFsFormat.SectorSize, DefragBlockKind.Used, entry.Name));

      // The volume's own structures occupy a fixed run at the front, whatever the
      // files have since been moved to; saying "up to the first file" instead would
      // put free space out of reach the moment a layout pushed the files back.
      const long metadataEnd = BcacheFsFormat.MetadataEndBytes;
      result.Add(new DefragBlockInfo(0, metadataEnd, DefragBlockKind.MetadataReserved,
        "Superblock slots, the journal and the b-trees"));
      files.Sort((a, b) => a.Offset.CompareTo(b.Offset));
      result.AddRange(files);

      // The last superblock slot sits at the end of the device.
      var tail = reader.Length - (long)BcacheFsFormat.SbSlotSectors * BcacheFsFormat.SectorSize;
      if (tail > metadataEnd)
        result.Add(new DefragBlockInfo(tail, reader.Length - tail,
          DefragBlockKind.MetadataReserved, "The superblock copy at the end of the device"));
    } catch {
      return [];
    }
    return result;
  }

  /// <summary>Zeros every byte no live file and no superblock occupies.</summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    _ = wipeDeletedEntries;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
