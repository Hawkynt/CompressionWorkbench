#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Gfs2;

/// <summary>
/// GFS2 (Global File System 2) descriptor — Red Hat's cluster filesystem,
/// mainline Linux since 2.6.19.
///
/// We parse the superblock at offset 65536, surface block size + lock
/// proto/table + UUID + master/root inode pointers, walk the inline root
/// directory, and extract both stuffed files and files stored through GFS2's
/// indirect block tree.
///
/// On-disk layout reverse-validated against real <c>mkfs.gfs2</c> output
/// (gfs2-utils 3.5.1): the <c>gfs2_meta_header</c> is 24 bytes, the sb carries
/// a reserved <c>__pad2</c> inum between master and root, and the
/// <c>gfs2_dirent</c> header is 40 bytes. See
/// <c>Gfs2ExternalConformanceTests</c> and <c>Gfs2WriterExternalTests</c> for the
/// mkfs.gfs2 / fsck.gfs2 gates.
///
/// <para>Creation (<see cref="Create"/>, <see cref="Gfs2Writer"/>) emits a fresh
/// standalone (<c>lock_nolock</c>, single-journal) volume — superblock, resource
/// groups and allocation bitmaps, the master directory and its system inodes,
/// a formatted 8&#160;MB journal, the root directory, and caller files. Small files
/// are stuffed into their dinodes; larger files use one or more levels of
/// indirect blocks. Large volumes are split into multiple resource groups.
/// Existing-image add/replace/remove uses a verified rebuild while preserving
/// the original image size as a floor and preserving <c>sb_locktable</c>.</para>
///
/// Out of scope: ExHash multi-leaf/nested directory writing, journal replay,
/// cluster lock manager state, and extended attributes.
///
/// Magic: <c>mh_magic = 0x01161970</c> (BE u32) at the start of the
/// superblock meta header. On disk at byte offset 65536 this serialises as
/// <c>01 16 19 70</c>. Confidence 0.85 — well-known constant at a fixed
/// offset, but GFS2 shares this magic with GFS1 at slightly different
/// layouts, so we keep a small margin below the 0.9-0.95 reserved for
/// formats with a structurally unique header.
///
/// References:
/// <list type="bullet">
///   <item><description>Linux kernel <c>fs/gfs2/</c> — <c>include/uapi/linux/gfs2_ondisk.h</c></description></item>
///   <item><description>Red Hat Cluster Suite / Resilient Storage Add-On documentation</description></item>
/// </list>
/// </summary>
public sealed class Gfs2FormatDescriptor
    : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty, ISyntheticEntryNames {

  /// <inheritdoc />
  public IReadOnlySet<string> SyntheticEntryNames => SyntheticEntries;

  // ── Synthetic, non-file entries the reader always surfaces ──────────────
  private static readonly IReadOnlySet<string> SyntheticEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "FULL.gfs2", "metadata.ini", "superblock.bin",
  };

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// <c>ImageSize</c> drives the writer's total size; <c>LockTable</c> is written
  /// into <c>sb_locktable</c> and read back as <c>Gfs2Reader.LockTable</c>. The
  /// 4&#160;KB block size and <c>lock_nolock</c> protocol are fixed by this standalone
  /// writer profile.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.ImageSize(["16 MB", "32 MB", "64 MB", "128 MB", "256 MB"],
      description: "Total volume size; larger explicit legacy size values are also accepted and split across resource groups."),
    new FormatOptionDescriptor(
      Key: "LockTable", DisplayName: "Lock table", Kind: FormatOptionKind.String, Default: "",
      Description: "Cluster lock-table name stamped into sb_locktable (empty for a standalone volume)."),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Gfs2";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "GFS2 (Global File System 2)";
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
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".gfs2";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".gfs2"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x01161970 BE at offset 65536 — start of gfs2_meta_header.mh_magic for the SB.
    new([0x01, 0x16, 0x19, 0x70], Offset: (int)Gfs2Reader.SbByteOffset, Confidence: 0.85),
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
    "GFS2 (Red Hat cluster filesystem) — reads/writes stuffed and indirect-tree regular files in a standalone lock_nolock volume; existing-image edits use a verified geometry-preserving rebuild.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    Gfs2Reader? r;
    try {
      r = new Gfs2Reader(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.gfs2", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    // A volume that carries files lists exactly those. Surfacing the synthetic
    // header entries alongside them would make every rebuild (shrink, defrag)
    // fold them back in as real files, so they stay on the carver path — empty
    // or foreign images, where the header IS all we can offer.
    if (r.Entries.Count > 0) {
      foreach (var e in r.Entries)
        entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, e.LastModified));
      return entries;
    }

    var imageLen = TryGetImageLen(r);
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.gfs2", imageLen, imageLen, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (r.SuperblockValid) {
      var raw = r.SuperblockRaw;
      entries.Add(new ArchiveEntryInfo(idx++, "superblock.bin", raw.LongLength, raw.LongLength, "stored", false, false, null));
    }
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    Gfs2Reader r;
    try {
      r = new Gfs2Reader(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    using (r) {
      if (r.Entries.Count > 0) {
        foreach (var e in r.Entries) {
          if (e.IsDirectory) continue;
          if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
          var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
          Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
          using var output = File.Create(target);
          r.ExtractTo(e, output);
        }
        return;
      }

      var imageLen = TryGetImageLen(r);
      var imageBytes = r.SuperblockValid ? null : ReadBackImageIfSmall(stream);
      WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(r, imageLen), files);

      if (r.SuperblockValid)
        WriteIfMatch(outputDir, "superblock.bin", r.SuperblockRaw, files);
      else if (imageBytes != null)
        WriteIfMatch(outputDir, "FULL.gfs2", imageBytes, files);
    }
  }

  /// <summary>
  /// Creates a fresh standalone (<c>lock_nolock</c>, single-journal) GFS2 volume
  /// and populates its root with the supplied regular files. The volume size is
  /// a floor: it defaults to 32&#160;MB, may be selected through <c>ImageSize</c> or
  /// the legacy raw <c>size</c> option, and grows when the payload requires it.
  /// Small files are stuffed into their dinode; larger files are addressed by
  /// the indirect metadata tree written by <see cref="Gfs2Writer"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);

    var lockTable = options?.GetOption("LockTable", "") ?? "";
    var sizes = new List<long>();
    var files = new List<(string Name, ArchiveInputInfo Input)>();
    if (inputs != null)
      foreach (var i in inputs) {
        if (i.IsDirectory) continue;
        var length = i.InMemoryContent?.LongLength ?? new FileInfo(i.FullPath).Length;
        sizes.Add(length);
        files.Add((i.ArchiveName, i));
      }

    // The requested size is a floor: the volume has to be at least large enough
    // for the payload's dinodes, data blocks and indirect blocks.
    var size = Math.Max(ParseSizeOption(options), Gfs2Writer.EstimateSize(sizes));
    var writer = new Gfs2Writer(size, lockTable: lockTable);
    foreach (var (name, input) in files) {
      if (input.InMemoryContent is { } bytes) {
        writer.AddFile(name, bytes);
        continue;
      }
      var path = input.FullPath;
      writer.AddStreamingFile(name, new FileInfo(path).Length, () => File.OpenRead(path));
    }
    writer.Build(output);
  }

  private static long ParseSizeOption(FormatCreateOptions? options) {
    const long defaultSize = 32L * 1024 * 1024;
    // Accept the schema's "ImageSize" enum ("32 MB", "Auto (fit to files)", …)
    // as well as the legacy raw "size" key (bytes with optional K/M/G suffix).
    var imageSize = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
    if (imageSize > 0)
      return Math.Max((long)imageSize, 16L * 1024 * 1024);

    var raw = options?.GetOption("size", "");
    if (string.IsNullOrWhiteSpace(raw))
      return defaultSize;

    raw = raw.Trim();
    var mult = 1L;
    var last = char.ToUpperInvariant(raw[^1]);
    if (last is 'K' or 'M' or 'G') {
      mult = last switch { 'K' => 1024L, 'M' => 1024L * 1024, _ => 1024L * 1024 * 1024 };
      raw = raw[..^1].Trim();
    }
    if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
      return defaultSize;
    var bytes = n * mult;
    return Math.Max(bytes, 16L * 1024 * 1024);
  }

  private readonly record struct EditProfile(long ImageSize, string LockTable);

  private static EditProfile ReadEditProfile(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanSeek)
      throw new ArgumentException("GFS2 mutation requires a seekable image stream.", nameof(archive));
    archive.Position = 0;
    using var reader = new Gfs2Reader(archive);
    if (!reader.SuperblockValid)
      throw new InvalidDataException("GFS2 mutation requires a valid superblock.");
    var profile = new EditProfile(archive.Length, reader.LockTable);
    archive.Position = 0;
    return profile;
  }

  private static byte[] BuildEditedImage(IReadOnlyList<(string Name, byte[] Data)> files, EditProfile profile) {
    var size = Math.Max(profile.ImageSize, Gfs2Writer.EstimateSize(files.Select(f => f.Data.LongLength)));
    var writer = new Gfs2Writer(size, lockTable: profile.LockTable);
    foreach (var (name, data) in files)
      writer.AddFile(name, data);
    return writer.Build();
  }

  private sealed class PreservingCreator(Gfs2FormatDescriptor descriptor, EditProfile profile) : IArchiveCreatable {
    public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
      var preserved = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
          ["size"] = profile.ImageSize.ToString(CultureInfo.InvariantCulture),
          ["LockTable"] = profile.LockTable,
        },
      };
      descriptor.Create(output, inputs, preserved);
    }
  }

  /// <summary>
  /// Adds or replaces regular files while preserving the existing volume size as
  /// a floor and preserving <c>sb_locktable</c>. Small volumes use the shared
  /// in-memory rebuild; large volumes use the scratch-file path so the edit is
  /// not bounded by <see cref="Array.MaxLength"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var profile = ReadEditProfile(archive);
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, new PreservingCreator(this, profile), SyntheticEntries);
      return;
    }
    ModifyRebuilder.Add(archive, inputs, ReadEntries,
      files => BuildEditedImage(files, profile));
  }

  /// <summary>
  /// Removes regular files with the same geometry/lock-table preservation as
  /// <see cref="Add"/>.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var profile = ReadEditProfile(archive);
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, new PreservingCreator(this, profile), SyntheticEntries);
      return;
    }
    ModifyRebuilder.Remove(archive, entryNames, ReadEntries,
      files => BuildEditedImage(files, profile));
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Rewrites the volume with every file laid out contiguously from the start of
  /// the data area. Each entry is spilled to scratch and the writer pulls it back
  /// while laying out the metadata tree, so the rebuild is not bounded by what a
  /// byte[] can hold.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the volume out again: a file's
    // out-of-line bytes are addressed by eight-byte tree pointers, so a move is
    // the copy, those pointers, and the two bits per block in the resource
    // group that say whether it is taken.
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd
        or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      var planned = false;
      // The in-place pass is kept only if every payload still reads back: it
      // can refuse partway — a file stuffed into its own dinode has no run of
      // its own — and a rebuild is the honest answer when it does.
      DefragContentGuard.RunOrRebuild(archive,
        readContents: stream => ReadEntries(stream).Select(e => e.Data).ToList(),
        inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }

    // Every mode streams: end-pack and carve-hole order their entries from
    // scratch inside the rebuilder, so none of them has to fall back to the
    // buffered path that a volume past two gigabytes cannot use.
    {
      Gfs2Writer? writer = null;
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
            writer = new Gfs2Writer(Gfs2Writer.EstimateSize(spill.ConvertAll(e => e.Size)));
            foreach (var (name, path, size) in spill) {
              var captured = path;
              writer.AddStreamingFile(name, size, () => File.OpenRead(captured));
            }
            writer.Build(target!);
          } finally {
            foreach (var (_, path, _) in spill)
              try { File.Delete(path); } catch { /* scratch file already gone */ }
          }
        });
    }
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    using var r = new Gfs2Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      using var buffer = new MemoryStream();
      r.ExtractTo(e, buffer);
      yield return (e.Name, buffer.ToArray());
    }
  }

  private static long TryGetImageLen(Gfs2Reader r)
    => r.SuperblockRaw.LongLength > 0 ? Gfs2Reader.SbByteOffset + r.SuperblockRaw.LongLength : 0;

  private static byte[]? ReadBackImageIfSmall(Stream s) {
    try {
      if (!s.CanSeek) return null;
      s.Position = 0;
      if (s.Length > 64 * 1024) return null;
      var buf = new byte[s.Length];
      var read = 0;
      while (read < buf.Length) {
        var n = s.Read(buf, read, buf.Length - read);
        if (n == 0) break;
        read += n;
      }
      return buf;
    } catch {
      return null;
    }
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Gfs2Reader r, long imageSize) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={(r.SuperblockValid ? "ok" : "partial")}\n");
    b.Append(ic, $"superblock_valid={r.SuperblockValid}\n");
    if (r.SuperblockValid) {
      b.Append(ic, $"block_size={r.BlockSize}\n");
      b.Append(ic, $"block_size_shift={r.BlockSizeShift}\n");
      b.Append(ic, $"root_inode_block={r.RootInodeBlock}\n");
      b.Append(ic, $"root_formal_ino={r.RootFormalIno}\n");
      b.Append(ic, $"master_inode_block={r.MasterInodeBlock}\n");
      b.Append(ic, $"master_formal_ino={r.MasterFormalIno}\n");
      b.Append(ic, $"lock_proto={r.LockProto}\n");
      b.Append(ic, $"lock_table={r.LockTable}\n");
      b.Append(ic, $"uuid_hex={r.UuidHex}\n");
      b.Append(ic, $"root_entry_count={r.Entries.Count}\n");
    }
    if (imageSize > 0)
      b.Append(ic, $"approx_image_size={imageSize}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <inheritdoc />
  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Gfs2BlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var extents = Gfs2ExtentMap.Enumerate(archive).ToList();
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
    var postExtents = Gfs2ExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// Enumerates the extents.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => Gfs2ExtentMap.Enumerate(image);

  /// <summary>
  /// Zero-fills every block the resource-group bitmaps report as free — which
  /// is where a removed file's bytes stay until something else claims them.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = Gfs2ExtentMap.Enumerate(image).ToList();
    if (extents.Count == 0) return 0;
    // The bitmap is per block and says nothing about where a file ends inside
    // its last one, so there are no cluster tips to trim from it.
    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
