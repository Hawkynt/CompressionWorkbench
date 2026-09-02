#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Reiser4;

/// <summary>
/// Read-only descriptor for Reiser4 filesystem images (successor to ReiserFS 3.6
/// — completely different on-disk layout). Surfaces the master superblock at
/// offset 65536 and, when present, the format40 superblock that follows it,
/// plus a structured metadata bundle and the raw image. Walking the twig-level
/// B-tree is explicitly out of scope (multi-week effort).
///
/// Magic:
/// <list type="bullet">
///   <item><description><c>"ReIsEr4"</c> at offset 65536 — master superblock <c>ms_magic[16]</c>.</description></item>
/// </list>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://archive.kernel.org/oldwiki/reiser4.wiki.kernel.org/</c> — archived Reiser4 wiki (format40 layout, plugin system)</description></item>
///   <item><description>reiser4progs (<c>mkfs.reiser4</c> / <c>debugfs.reiser4</c>) — canonical userspace tooling</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Reiser4</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class Reiser4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Knobs the empty-filesystem writer actually honours. <c>VolumeLabel</c> is
  /// written into the master superblock label field (and the backup record) and
  /// surfaces through <c>fsck.reiser4</c> / our metadata readback;
  /// <c>ImageSize</c> drives <see cref="Reiser4Writer.BlockCount"/> (4&#160;KB
  /// blocks, clamped to the writer minimum). The 4&#160;KB block size is fixed —
  /// the embedded mkfs.reiser4 templates are byte-exact 4096-byte captures — so
  /// it is intentionally not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
    FilesystemSchemaPresets.ImageSize(["16 MB", "32 MB", "64 MB", "128 MB"]),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Reiser4";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Reiser4";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".reiser4";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".reiser4"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "ReIsEr4" at byte offset 65536 (= 16 * 4096). Confidence 0.9: the 7-byte
    // magic is highly unlikely to land at exactly this offset by chance, but
    // it shares the "ReIsEr" prefix with the older ReiserFS 3.6 magic
    // ("ReIsErFs", "ReIsEr2Fs", "ReIsEr3Fs") which lives at offset 65536+52.
    // The disambiguation is unambiguous (different offsets, different suffixes)
    // so 0.9 is appropriate — slightly below the 0.95 used for unique 4+ byte
    // magics that live at position 0.
    new("ReIsEr4"u8.ToArray(), Offset: (int)Reiser4MasterSb.MasterOffset, Confidence: 0.9),
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
public string Description => "Reiser4 filesystem image — master + format40 superblock surface only.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      // Stream blew up before we got anywhere. Surface the irreducible minimum.
      entries.Add(new ArchiveEntryInfo(0, "FULL.reiser4", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    Reiser4MasterSb sb;
    try {
      sb = Reiser4MasterSb.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.reiser4", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    // A volume that carries files lists exactly those. Surfacing the synthetic
    // header entries alongside them would make every rebuild (shrink, defrag)
    // fold them back in as real files, so they stay on the carver path — empty
    // or foreign images, where the header IS all we can offer.
    var payload = ReadPayload(stream);
    var idx = 0;
    if (payload.Count > 0) {
      foreach (var e in payload)
        entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(idx++, "FULL.reiser4", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "master_superblock.bin", sb.MasterRaw.LongLength, sb.MasterRaw.LongLength, "stored", false, false, null));
    if (sb.Format40Present)
      entries.Add(new ArchiveEntryInfo(idx++, "format40_superblock.bin", sb.Format40Raw.LongLength, sb.Format40Raw.LongLength, "stored", false, false, null));
    return entries;
  }

  /// <summary>Files the workbench-layout payload area holds. Never throws — empty when there are none.</summary>
  private static IReadOnlyList<Reiser4Reader.Entry> ReadPayload(Stream stream) {
    try {
      if (stream.CanSeek) stream.Position = 0;
      using var reader = new Reiser4Reader(stream);
      return reader.Entries;
    } catch {
      return [];
    }
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

    Reiser4MasterSb sb;
    try {
      sb = Reiser4MasterSb.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.reiser4", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    // A volume that carries files extracts exactly those, mirroring List.
    if (stream.CanSeek) stream.Position = 0;
    using (var reader = new Reiser4Reader(stream)) {
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

    WriteIfMatch(outputDir, "FULL.reiser4", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "master_superblock.bin", sb.MasterRaw, files);
    if (sb.Format40Present)
      WriteIfMatch(outputDir, "format40_superblock.bin", sb.Format40Raw, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Reiser4MasterSb sb) {
    var b = new StringBuilder();
    b.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    b.Append(CultureInfo.InvariantCulture, $"master_offset={Reiser4MasterSb.MasterOffset}\n");
    b.Append(CultureInfo.InvariantCulture, $"blocksize={sb.BlockSize}\n");
    b.Append(CultureInfo.InvariantCulture, $"disk_plugin_id={sb.DiskPluginId}\n");
    b.Append(CultureInfo.InvariantCulture, $"uuid_hex={sb.UuidHex}\n");
    b.Append(CultureInfo.InvariantCulture, $"label={sb.Label}\n");
    b.Append(CultureInfo.InvariantCulture, $"format40_present={sb.Format40Present}\n");
    if (sb.Format40Present) {
      b.Append(CultureInfo.InvariantCulture, $"block_count={sb.BlockCount}\n");
      b.Append(CultureInfo.InvariantCulture, $"free_blocks={sb.FreeBlocks}\n");
      b.Append(CultureInfo.InvariantCulture, $"root_block={sb.RootBlock}\n");
      b.Append(CultureInfo.InvariantCulture, $"file_count={sb.FileCount}\n");
      b.Append(CultureInfo.InvariantCulture, $"mkfs_id=0x{sb.MkfsId:X8}\n");
      b.Append(CultureInfo.InvariantCulture, $"tree_height={sb.TreeHeight}\n");
      b.Append(CultureInfo.InvariantCulture, $"tail_policy={sb.Policy}\n");
      b.Append(CultureInfo.InvariantCulture, $"format40_version={sb.Format40Version}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // ── IArchiveCreatable ────────────────────────────────────────────────
  // The reserved blocks are byte-exact mkfs.reiser4 captures describing an empty
  // storage tree; growing that tree (extent40 item bodies keyed by file offset,
  // cde40 directory units) is not reproduced here. Files go in the workbench-layout
  // payload area past those blocks, with the block-allocator bitmap and
  // sb_free_blocks kept consistent so the volume stays internally coherent.
  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var w = new Reiser4Writer();

    // Volume label: prefer the schema knob, falling back to the legacy
    // password-slot mapping (kept for callers that pre-date the schema).
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(options?.Password))
      label = options.Password;
    if (!string.IsNullOrEmpty(label))
      w.Label = label;

    // Image size: the writer counts 4 KB blocks. "Auto (fit to files)" and any
    // unset/unparsable value leave the default (and writer minimum) in place.
    var sizes = new List<long>();
    if (inputs != null)
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

    // The requested size is a floor: the volume has to be at least large enough
    // for the payload's directory chain, data blocks and bitmaps.
    var sizeBytes = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
    var requested = sizeBytes > 0 ? (ulong)Math.Max(1, sizeBytes / Reiser4Writer.BlockSize) : 0UL;
    w.BlockCount = Math.Max(requested, Reiser4Writer.EstimateBlockCount(sizes));

    w.Write(output);
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Largest volume the in-place pass is offered for. Its guard holds a copy of
  /// the image to compare payloads across the pass.
  /// </summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    using var reader = new Reiser4Reader(stream, leaveOpen: true);
    return reader.Entries.Where(e => e.Size > 0).Select(reader.Extract).ToList();
  }

  /// <summary>Plans the new layout and moves the runs into it, repointing at the end.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Reiser4BlockMover();
    mover.Init(archive);

    archive.Position = 0;
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

    // The directory is written once every run has landed: a file's position is
    // one field, and what it means depends on where all of its blocks are.
    mover.SettleDirectory(archive);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// Rewrites the volume with every file laid out contiguously from the start of
  /// the payload area. Each entry is spilled to scratch and the writer pulls it
  /// back, so the rebuild is not bounded by what a byte[] can hold.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the volume out again: a file is
    // the run of blocks that starts where its directory entry says, so a move
    // is the copy plus those eight bytes. What the format will not describe is
    // a file whose blocks are no longer one sequence, and the pass refuses
    // rather than write that down.
    if (archive.CanSeek && archive.Length <= MaxBufferedImageBytes) {
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

    if (options.Mode is not (DefragMode.ConsolidateAtStart or DefragMode.FillHolesLazy))
      throw new NotSupportedException(
        $"Reiser4 defragmentation supports ConsolidateAtStart and FillHolesLazy; got {options.Mode}.");

    Stream? target = null;
    var spill = new List<(string Name, string Path, long Size)>();
    DefragRebuilder.RebuildStreaming(archive, options,
      readEntries: ReadEntries,
      beginWrite: s => target = s,
      writeEntry: (name, data) => {
        // The volume has to be sized before the first byte is written, so the
        // entries are collected first and the writer is built in finishWrite.
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, data);
        spill.Add((name, path, data.LongLength));
      },
      finishWrite: () => {
        try {
          var w = new Reiser4Writer {
            BlockCount = Reiser4Writer.EstimateBlockCount(spill.ConvertAll(e => e.Size)),
          };
          foreach (var (name, path, size) in spill) {
            var captured = path;
            w.AddStreamingFile(name, size, () => File.OpenRead(captured));
          }
          w.Write(target!);
        } finally {
          foreach (var (_, path, _) in spill)
            try { File.Delete(path); } catch { /* scratch file already gone */ }
        }
      });
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    using var reader = new Reiser4Reader(stream);
    foreach (var e in reader.Entries) {
      using var buffer = new MemoryStream();
      reader.ExtractTo(e, buffer);
      yield return (e.Name, buffer.ToArray());
    }
  }

  // ── IArchiveWriteConstraints ─────────────────────────────────────────
  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    reason = null;
    return true;
  }
  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the min total archive size.
  /// </summary>
public long? MinTotalArchiveSize => Reiser4Writer.BlockSize * (long)Reiser4Writer.MinBlockCount; // 16 MB
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "Reiser4 image; files are stored in the workbench-layout payload area past the reserved blocks.";

  // Bounded read — must NOT pull multi-GB images into memory when the carver
  // runs us speculatively. Master SB is at 65536, format40 SB at 65536+blocksize
  // (4 KB typical). Cap at 96 KB so the format40 block at 69632..70112 always
  // fits even when blocksize is 4 KB.
  private const int HeaderReadCap = 96 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

  // ── IFilesystemExtentMap + IWipeEmpty ─────────────────────────────────

  /// <summary>
  /// Reports the volume's layout: the reserved blocks and the payload directory
  /// chain as metadata, then each file's blocks. A file's blocks are consecutive
  /// apart from the block-allocator bitmaps they step over.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    List<DefragBlockInfo> result = [];
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new Reiser4Reader(image);
      if (!reader.Valid) return [];

      var blockSize = reader.BlockSize;
      // Everything before the first file's blocks is reserved or directory.
      var firstData = reader.Length;
      List<DefragBlockInfo> files = [];
      foreach (var e in reader.Entries) {
        if (e.Size <= 0) continue;
        // A file is not one run: the allocator bitmaps inside it are stepped
        // over, so its bytes continue past where its length alone would end.
        foreach (var (offset, length) in reader.EnumerateRuns(e)) {
          if (offset < firstData) firstData = offset;
          files.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, e.Name));
        }
      }

      var metadataEnd = files.Count > 0 ? firstData : Math.Min(reader.Length, 25L * blockSize);
      result.Add(new DefragBlockInfo(0, metadataEnd, DefragBlockKind.MetadataReserved,
        "Reserved blocks and the payload directory"));

      // The bitmaps sit at stride boundaries inside the payload area.
      for (var block = Reiser4Writer.BlocksPerBitmap;
           (long)block * blockSize + blockSize <= reader.Length;
           block += Reiser4Writer.BlocksPerBitmap)
        result.Add(new DefragBlockInfo((long)block * blockSize, blockSize,
          DefragBlockKind.MetadataReserved, "Block-allocator bitmap"));

      result.AddRange(files);
    } catch {
      return [];
    }
    return result;
  }

  /// <summary>Zeros every byte no live file and no metadata block occupies.</summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    _ = wipeDeletedEntries;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
