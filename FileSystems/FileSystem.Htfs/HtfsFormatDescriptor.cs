#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Htfs;

/// <summary>
/// SCO HTFS (High Throughput File System) — S5-derived FS introduced in SCO
/// OpenServer 5. Now exposes a WORM writer + reader with real nested
/// subdirectories, defrag/purge/conversion, fileset optimizer, and an options
/// schema (BlockSize / InodeCount / VolumeLabel).
///
/// References:
/// <list type="bullet">
///   <item><description>SCO OpenServer 5 Development System documentation, <c>sys/fs/htfs/htfs_fs.h</c> — the vendor header defining the on-disk structures (no stable public URL)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/SCO_OpenServer</c> — Wikipedia overview of the host OS</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Reference</b>: SCO OpenServer Development System docs,
/// <c>sys/fs/htfs/htfs_fs.h</c>. Superblock at byte offset 512 (sector 1).
/// Magic <c>0x012FD15D</c> (LE u32) at byte offset 0 of the superblock.</para>
/// <para><b>Hierarchy</b>: real — directories nest via the writer's inode +
/// 16-byte dirent chain (single-block dirs cap one BB of entries each).</para>
/// </remarks>
public sealed class HtfsFormatDescriptor :
    IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
    IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Htfs";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "HTFS (SCO High Throughput File System)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".htfs";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".htfs", ".s5"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x5D, 0xD1, 0x2F, 0x01], Offset: 512, Confidence: 0.85),
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
public string Description => "SCO HTFS — WORM writer + nested-directory reader.";

    /// <summary>
  /// Gets the options schema.
  /// </summary>
public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("BlockSize", "Block size", FormatOptionKind.Enum, "512",
      AllowedValues: ["512", "1024", "2048"],
      Description: "Block size in bytes (S5-style HTFS supports 512/1024/2048)."),
    new("InodeCount", "Inode count", FormatOptionKind.Integer, "64",
      Description: "Reserved inode slots in the inode array (default 64; cap 256)."),
    FilesystemSchemaPresets.VolumeLabel(16),
  ];

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new HtfsReader(stream);
      return r.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, null
      )).ToList();
    } catch {
      return [
        new ArchiveEntryInfo(0, "FULL.htfs", stream.Length, stream.Length, "stored", false, false, null),
        new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null),
      ];
    }
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    try {
      var r = new HtfsReader(stream);
      foreach (var e in r.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && !MatchesFilter(e.Name, files)) continue;
        WriteFile(outputDir, e.Name, r.Extract(e));
      }
    } catch {
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      var sb = HtfsSuperblock.TryParse(ms.ToArray());
      WriteFile(outputDir, "metadata.ini", BuildMetadata(sb));
    }
  }

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new HtfsWriter();
    w.SetVolumeLabel(options.GetOption("VolumeLabel", "WORM"));
    // NOTE: the block-size auto-optimiser is intentionally NOT wired here. The
    // HTFS reader's block-size detection only recovers 512-byte images, so a
    // non-512 default would not round-trip — see HtfsReader.DetectBlockSize. The
    // BlockSize knob therefore stays an explicit, caller-pinned choice only.
    var blockSize = options.GetOptionInt("BlockSize", 512);
    if (blockSize is 512 or 1024 or 2048) w.SetBlockSize(blockSize);
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

    /// <summary>
  /// Enumerates the extents.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) => HtfsExtentMap.Enumerate(image);

  // ── IArchiveModifiable (true in-place R/W) ──────────────────────────
  //
  // HtfsInPlaceModifier claims a free inode slot inside the existing inode-block
  // region and a fresh contiguous data run appended at image end, inserts the
  // dirent into the single-block root directory, and bumps s_fsize — leaving
  // every untouched inode + data block byte-identical. Nested-directory adds and
  // any case that would need a new inode block or a multi-block root dir fall
  // back to ModifyRebuilder so the user always gets a working image.

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

    HtfsInPlaceModifier.Add(archive, inputs,
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

    HtfsInPlaceModifier.Remove(archive, entryNames,
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

    // Moving what is out of place beats writing the volume out again: a rebuild
    // reads and rewrites every file to fix a handful of runs. A file here is one
    // contiguous run recorded in a single inode field, so a move is the copy and
    // one four-byte write. Anything the planner will not commit to falls through
    // to the rebuild below.
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

    // A volume too large to materialise goes through the streaming rebuilder;
    // the buffered path's buildImage returns a byte[] of the whole image, which
    // the writer refuses to produce once it passes the array limit.
    // Every mode streams above the cap: end-pack and carve-hole order their
    // entries from scratch inside the rebuilder, so none of them falls back
    // to a buffered rebuild the volume is too large for.
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      HtfsWriter? streamWriter = null;
      Stream? target = null;
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => {
          var r = new HtfsReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
        },
        beginWrite: s2 => { streamWriter = new HtfsWriter(); target = s2; },
        // As a stream factory, not inline: an inline payload is materialised
        // inside the image buffer, which is what a large volume cannot afford.
        writeEntry: (name, data) => streamWriter!.AddStreamingFile(
          name, data.LongLength, () => new MemoryStream(data, writable: false)),
        finishWrite: () => streamWriter!.WriteTo(target!));
    }
  }

  /// <summary>Largest volume a defrag will rebuild through a byte[].</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;


  // ── Shared rebuild delegates ────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new HtfsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new HtfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

    /// <summary>
  /// Performs the wipe unused space operation.
  /// </summary>
public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var size = image.Length;
    var extents = HtfsExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, size, wipeClusterTips: false, fileSizeLookup: null);
  }

  private static byte[] BuildMetadata(HtfsSuperblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic=0x{sb.Magic:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"isize={sb.Isize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsize={sb.Fsize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"nfree={sb.Nfree}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"ninode={sb.Ninode}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Moves only the files that are in the wrong place, repointing each one's
  /// inode as its blocks arrive.
  /// </summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new HtfsBlockMover();
    mover.Init(archive);

    var extents = HtfsExtentMap.Enumerate(archive).ToList();
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
    var postExtents = HtfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string PlannerFallbackLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
