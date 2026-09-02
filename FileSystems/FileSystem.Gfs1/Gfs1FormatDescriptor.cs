#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Gfs1;

/// <summary>
/// Sistina/Red Hat GFS (pre-GFS2) format descriptor. WORM writer + reader with
/// real nested subdirectories, defrag/purge/conversion, fileset optimizer,
/// and an options schema (BlockSize / JournalCount / LockProto / LockTable).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://sourceforge.net/projects/opengfs/</c> — OpenGFS, the open continuation of Sistina GFS whose headers define the GFS1 on-disk structures</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Global_File_System_2</c> — Wikipedia article covering GFS history and its GFS2 successor</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Reference</b>: Sistina GFS / OpenGFS (the pre-Red Hat patches).
/// Meta-header magic <c>0x01161970</c> appears at every metadata block start.
/// Superblock at byte offset 65536. GFS vs GFS2 disambiguated by
/// <c>sb_multihost_format = 1900</c> (GFS) vs <c>1901</c> (GFS2). We anchor
/// the magic at offset 65536 + 0x40 so detection doesn't collide with
/// <c>FileSystem.Gfs2</c>.</para>
/// <para><b>Hierarchy</b>: real — directories nest via the writer's inode +
/// (4-byte BE inode + 1-byte nlen + name) dirent chain (single-block dirs
/// cap one BB of entries).</para>
/// <para><b>Lock proto / table</b>: GFS1 requires <c>sb_lockproto</c>
/// (<c>"lock_nolock"</c> for standalone, <c>"lock_dlm"</c> for clustered) +
/// <c>sb_locktable</c>. The writer emits these via the options schema; the
/// real distributed-lock protocol negotiation is out of WORM scope.</para>
/// </remarks>
public sealed class Gfs1FormatDescriptor :
    IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
    IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Gfs1";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "GFS (Sistina/Red Hat, original)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".gfs";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".gfs", ".gfs1"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // GFS1 and GFS2 share mh_magic 0x01161970 at the superblock, so magic alone
    // sent every GFS1 volume to the GFS2 descriptor. sb_fs_format at +0x18 is
    // what separates them: 1309 here, 18xx for GFS2. Everything between the two
    // fields is masked out.
    new([0x01, 0x16, 0x19, 0x70,
         0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
         0x00, 0x00, 0x05, 0x1D],
      Offset: 65536, Confidence: 0.92,
      Mask: [0xFF, 0xFF, 0xFF, 0xFF,
             0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
             0xFF, 0xFF, 0xFF, 0xFF]),
    // The second meta header the writer lays down right after the superblock.
    new([0x01, 0x16, 0x19, 0x70], Offset: 65536 + 0x40, Confidence: 0.65),
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
public string Description => "Sistina GFS (pre-GFS2) — WORM writer + nested-directory reader.";

    /// <summary>
  /// Gets the options schema.
  /// </summary>
public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("BlockSize", "Block size", FormatOptionKind.Enum, "4096", AllowedValues: ["4096"],
      Description: "GFS1 block size (always 4096 per Sistina spec)."),
    new("JournalCount", "Journal count", FormatOptionKind.Integer, "1",
      Description: "Number of per-node journals to allocate (1 standalone; >1 for clustered)."),
    new("LockProto", "Lock protocol", FormatOptionKind.Enum, "lock_nolock",
      AllowedValues: ["lock_nolock", "lock_dlm"],
      Description: "Cluster lock protocol. Use lock_nolock for single-node images."),
    new("LockTable", "Lock table", FormatOptionKind.String, "WORM:gfs1",
      Description: "Lock table identifier (format: clustername:fsname)."),
    FilesystemSchemaPresets.VolumeLabel(16),
  ];

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new Gfs1Reader(stream);
      return r.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, null)).ToList();
    } catch {
      return [
        new ArchiveEntryInfo(0, "FULL.gfs", stream.Length, stream.Length, "stored", false, false, null),
        new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null),
      ];
    }
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    try {
      var r = new Gfs1Reader(stream);
      foreach (var e in r.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && !MatchesFilter(e.Name, files)) continue;
        WriteFile(outputDir, e.Name, r.Extract(e));
      }
    } catch {
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      var sb = Gfs1Superblock.TryParse(ms.ToArray());
      WriteFile(outputDir, "metadata.ini", BuildMetadata(sb));
    }
  }

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new Gfs1Writer();
    w.SetVolumeLabel(options.GetOption("VolumeLabel", "WORM"));
    w.SetJournalCount(options.GetOptionInt("JournalCount", 1));
    w.SetLockProto(options.GetOption("LockProto", "lock_nolock"));
    w.SetLockTable(options.GetOption("LockTable", "WORM:gfs1"));
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      // Only the length is needed to lay the volume out; reading a large input
      // into a byte[] would cap the volume at what an array can hold.
      var info = i;
      if (info.InMemoryContent is { } bytes)
        w.AddFile(info.ArchiveName, bytes);
      else
        w.AddStreamingFile(info.ArchiveName, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    w.WriteTo(output);
  }

  // ── IArchiveModifiable (genuine in-place R/W) ───────────────────────────
  //
  // Gfs1InPlaceModifier writes only the changed inode slot(s), the parent dir
  // block, the appended data run, and sb_size — every untouched block stays
  // byte-identical at its original offset. Root files and one level of nested
  // directories are handled in place; deeper trees, a full inode region, or a
  // full directory block fall back to the rebuild delegate.

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

    Gfs1InPlaceModifier.Add(archive, inputs,
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

    Gfs1InPlaceModifier.Remove(archive, entryNames,
      (a, n) => ModifyRebuilder.Remove(a, n, ReadEntries, BuildImage, largeVolumeCreator: this));
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new Gfs1Reader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new Gfs1Writer();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

    /// <summary>
  /// Enumerates the extents.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) => Gfs1ExtentMap.Enumerate(image);

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Rewrites the volume with every file laid out contiguously from the start.
  /// The rebuild goes through scratch files rather than a byte[] image, so a
  /// volume larger than an array can hold still defragments.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the volume out again: a rebuild
    // reads and rewrites every file to fix a handful of runs. A file here is one
    // contiguous run named by a single inode field, so a move is the copy and one
    // write. What the planner will not commit to falls through to the rebuild.
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd
        or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch (Exception planFailure) {
        options.OnProgress?.Invoke(new DefragProgressEvent(
          "fallback", 0, -1, -1, archive.Length, null,
          $"In-place planning declined ({planFailure.GetType().Name}: " +
          $"{PlannerFallbackLine(planFailure.Message)}); rebuilding instead"));
        archive.Position = 0;
      }
    }
    // Every consolidate mode lands on the same layout here: the writer emits a
    // fresh volume packed from the first data block, and has no way to place
    // files against the tail. Carving a hole is the one request it cannot meet.
    if (options.Mode is DefragMode.CarveHole)
      throw new NotSupportedException(
        "GFS1 defragmentation cannot carve a hole: the rebuild always start-packs the volume.");

    var tempPath = Path.GetTempFileName();
    var spill = new List<string>();
    try {
      using (var temp = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite)) {
        var w = new Gfs1Writer();
        var reader = new Gfs1Reader(archive);
        foreach (var e in reader.Entries) {
          if (e.IsDirectory) continue;
          var path = Path.GetTempFileName();
          spill.Add(path);
          using (var scratch = File.Create(path))
            reader.ExtractTo(e, scratch);
          var captured = path;
          w.AddStreamingFile(e.Name, e.Size, () => File.OpenRead(captured));
        }
        w.WriteTo(temp);

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
      foreach (var path in spill)
        try { File.Delete(path); } catch { /* scratch file already gone */ }
    }
  }

    /// <summary>
  /// Performs the wipe unused space operation.
  /// </summary>
public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var size = image.Length;
    var extents = Gfs1ExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, size, wipeClusterTips: false, fileSizeLookup: null);
  }

  private static byte[] BuildMetadata(Gfs1Superblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"mh_magic=0x{sb.MhMagic:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sb_offset=65536\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sb_multihost_format={sb.MultihostFormat}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sb_fs_format={sb.FsFormat}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Moves only the files that are in the wrong place, repointing each one's
  /// inode as its blocks arrive.
  /// </summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Gfs1BlockMover();
    mover.Init(archive);

    var extents = Gfs1ExtentMap.Enumerate(archive).ToList();
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
    var postExtents = Gfs1ExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string PlannerFallbackLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
