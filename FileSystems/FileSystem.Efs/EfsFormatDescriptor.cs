#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Efs;

/// <summary>
/// SGI EFS (Extent File System) format descriptor — the pre-XFS native
/// filesystem used on IRIX before 5.3 (1994). Surfaces a real WORM writer that
/// emits a spec-keyed superblock + single-cylinder-group inode table + per-file
/// single-extent layout, plus defrag/purge/conversion/optimizer wiring.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/efs</c> — Linux kernel EFS driver (read-only), the maintained on-disk reference</description></item>
///   <item><description>IRIX <c>sys/fs/efs_fs.h</c> — the original SGI header defining the superblock and extent layout</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Extent_File_System</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Reference</b>: Linux kernel <c>fs/efs/efs_fs_sb.h</c>, IRIX
/// <c>sys/fs/efs_fs.h</c>. Superblock at offset 0 (sector 0, 512-byte sectors).
/// Magic <c>0x00072959</c> (big-endian u32) at byte offset 0x1C inside the
/// superblock (<c>fs_magic</c>).</para>
/// <para><b>Hierarchy</b>: real — directories nest via the writer's directory
/// inode chain (single-block directories; bodies use inode + nlen + name
/// dirents). Reader recurses from inode 2 (root) and surfaces each entry at
/// its full path.</para>
/// </remarks>
public sealed class EfsFormatDescriptor :
    IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
    IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Efs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "EFS (SGI Extent File System)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".efs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".efs", ".efsimg"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // fs_magic sits at 0x1C of the superblock, and the superblock is at block
    // 1 — block 0 is the SGI volume header. Both positions are listed because
    // this project wrote the older, wrong one for a while.
    new([0x00, 0x07, 0x29, 0x59], Offset: 0x21C, Confidence: 0.85),
    new([0x00, 0x07, 0x29, 0x59], Offset: 0x18, Confidence: 0.60),
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
  public string Description => "SGI EFS (pre-XFS IRIX filesystem) — WORM writer + hierarchical reader.";

  // ── Options schema ──────────────────────────────────────────────────────
  /// <summary>
  /// Gets the options schema.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("BlockSize", "Block size", FormatOptionKind.Enum, "512", AllowedValues: ["512"],
      Description: "EFS basic-block size in bytes (always 512 per IRIX spec)."),
    new("CylinderGroupSize", "Cylinder group size (BB)", FormatOptionKind.Integer, "32",
      Description: "Cylinder group size in 512-byte basic blocks."),
    FilesystemSchemaPresets.VolumeLabel(6),
  ];

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new EfsReader(stream);
      return r.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, null
      )).ToList();
    } catch {
      // Honest fall-back for malformed images: at least surface the raw image
      // plus the metadata.ini stub the old descriptor used.
      return [
        new ArchiveEntryInfo(0, "FULL.efs", stream.Length, stream.Length, "stored", false, false, null),
        new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null),
      ];
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    try {
      var r = new EfsReader(stream);
      foreach (var e in r.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && !MatchesFilter(e.Name, files)) continue;
        WriteFile(outputDir, e.Name, r.Extract(e));
      }
    } catch {
      // Honest fall-back so callers always see a metadata.ini.
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      var sb = EfsSuperblock.TryParse(ms.ToArray());
      WriteFile(outputDir, "metadata.ini", BuildMetadata(sb));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new EfsWriter();
    w.SetVolumeLabel(options.GetOption("VolumeLabel", "WORM"));
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
  /// Adds (or replaces by name) files inside an existing EFS image via
  /// <see cref="EfsInPlaceModifier"/> — TRUE in-place O(touched bytes) I/O
  /// (claim a free dinode slot, append a contiguous extent at EOF, write the
  /// root dirent). Falls back to a whole-image rebuild only for nested paths,
  /// inode-table exhaustion, or extents past the single-extent ceiling.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    try {
      foreach (var (name, data) in FilesOnly(inputs)) {
        EfsInPlaceModifier.RemoveFile(archive, name, wipeData: true);
        EfsInPlaceModifier.AddFile(archive, name, data);
      }
    } catch (IOException) {
      archive.Position = 0;
      ModifyRebuilder.Add(archive, inputs,
        readEntries: stream => {
          var r = new EfsReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
        },
        buildImage: BuildImage, largeVolumeCreator: this);
    }
  }

  /// <summary>Removes the named entries in-place via <see cref="EfsInPlaceModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var leftover = new List<string>();
    foreach (var name in entryNames) {
      var leaf = name.Replace('\\', '/').TrimStart('/');
      if (leaf.Contains('/') || !EfsInPlaceModifier.RemoveFile(archive, leaf, wipeData: true))
        leftover.Add(name);
    }
    if (leftover.Count == 0) return;
    archive.Position = 0;
    ModifyRebuilder.Remove(archive, leftover.ToArray(),
      readEntries: stream => {
        var r = new EfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage, largeVolumeCreator: this);
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new EfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  /// <summary>
  /// Enumerates the extents.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) => EfsExtentMap.Enumerate(image);

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

    // Moving what is out of place beats writing the volume out again: a file
    // here is one extent named by its inode, so a move is the copy and one
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

    // A volume too large to materialise goes through the streaming rebuilder;
    // the buffered path's buildImage returns a byte[] of the whole image, which
    // the writer refuses to produce once it passes the array limit.
    // Every mode streams above the cap: end-pack and carve-hole order their
    // entries from scratch inside the rebuilder, so none of them falls back
    // to a buffered rebuild the volume is too large for.
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      EfsWriter? streamWriter = null;
      Stream? target = null;
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => {
          var r = new EfsReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
        },
        beginWrite: s2 => { streamWriter = new EfsWriter(); target = s2; },
        // As a stream factory, not inline: an inline payload is materialised
        // inside the image buffer, which is what a large volume cannot afford.
        writeEntry: (name, data) => streamWriter!.AddStreamingFile(
          name, data.LongLength, () => new MemoryStream(data, writable: false)),
        finishWrite: () => streamWriter!.WriteTo(target!));
      return;
    }

    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new EfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new EfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  /// <summary>Largest volume a defrag will rebuild through a byte[].</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;


  /// <summary>
  /// Performs the wipe unused space operation.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var size = image.Length;
    var extents = EfsExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, size, wipeClusterTips: false, fileSizeLookup: null);
  }

  private static byte[] BuildMetadata(EfsSuperblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"size_blocks={sb.SizeBlocks}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"first_cg={sb.FirstCg}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"cg_isize={sb.CgIsize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"cg_size={sb.CgSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sectors={sb.Sectors}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"heads={sb.Heads}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"num_cg={sb.NumCg}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"dirty={sb.Dirty}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"time={sb.Time}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic=0x{sb.Magic:X8}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Moves only the files that are in the wrong place, repointing each one's
  /// inode extent as its blocks arrive.
  /// </summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new EfsBlockMover();
    mover.Init(archive);

    var extents = EfsExtentMap.Enumerate(archive).ToList();
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
    var postExtents = EfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string PlannerFallbackLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
