#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Bbc;

public sealed class BbcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for BBC DFS creation. DFS stores a 12-character disk title
  /// across the two catalog sectors, plus a 2-bit "boot option" that controls
  /// what SHIFT-BREAK does. Disk geometry is fixed at 40-track SSD (100 KB).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 12),
    new FormatOptionDescriptor(
      Key: "BootOption",
      DisplayName: "Boot option (*OPT 4)",
      Kind: FormatOptionKind.Enum,
      Default: "None",
      AllowedValues: ["None", "LOAD", "RUN", "EXEC"],
      Description: "What SHIFT-BREAK does with $.!BOOT: None = nothing; " +
        "LOAD = *LOAD $.!BOOT; RUN = *RUN $.!BOOT; EXEC = *EXEC $.!BOOT. " +
        "Stored at catalog sector 1 byte 6 bits 4-5."),
  ];

  /// <summary>
  /// Walks the catalog (sectors 0-1 per side) and yields the actual
  /// on-disk byte layout — catalog sectors as
  /// <see cref="DefragBlockKind.MetadataReserved"/>, every file as a
  /// single contiguous run starting at its <c>(start_sector, length)</c>,
  /// and unallocated sectors as Free.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => BbcExtentMap.Enumerate(image);

  // 40-track SSD: 40 * 10 * 256 = 102 400 bytes. Writer emits this canonical size.
  public long? MaxTotalArchiveSize => BbcWriter.DiskSize40;
  public string AcceptedInputsDescription =>
    "BBC Micro Acorn DFS disk image (40/80-track, single or double sided).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>Canonical BBC DFS image sizes: 40-track SSD (102 400) and 80-track SSD (204 800).</summary>
  public IReadOnlyList<long> CanonicalSizes => [BbcWriter.DiskSize40, BbcWriter.DiskSize40 * 2L];

  public string Id => "Bbc";
  public string DisplayName => "BBC DFS";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Bbc image.
  /// Uses <c>BbcModifier</c> for true O(touched bytes) random-access
  /// I/O — only the two catalog sectors and the file's contiguous data
  /// run are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      BbcModifier.RemoveFile(archive, name, wipeData: true);
      BbcModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing Bbc image. Uses
  /// <c>BbcModifier</c> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      BbcModifier.RemoveFile(archive, name, wipeData: true);
  }


  public string DefaultExtension => ".ssd";
  public IReadOnlyList<string> Extensions => [".ssd", ".dsd"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // DFS has no magic bytes — the catalog is just raw ASCII padded with spaces.
  // Detection is extension-based.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BBC Micro Acorn DFS floppy disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var doubleSided = false;  // Callers who know better can pass the right reader directly.
    using var r = new BbcReader(stream, doubleSided);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.FullName, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new BbcReader(stream, doubleSided: false);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FullName, files)) continue;
      // Translate BBC "$.NAME" to a filesystem-safe "NAME" (or keep dir prefix as subdir
      // if it's not the default '$').
      var outName = e.Directory == '$' ? e.Name : $"{e.Directory}/{e.Name}";
      WriteFile(outputDir, outName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var total = 0L;
    foreach (var i in inputs) if (!i.IsDirectory) total += i.InMemoryContent?.LongLength ?? new FileInfo(i.FullPath).Length;
    if (total > BbcWriter.DiskSize40)
      throw new InvalidOperationException(
        $"BBC DFS: combined input size {total} bytes exceeds 40-track SSD capacity ({BbcWriter.DiskSize40} bytes).");

    var w = new BbcWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);

    var title = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(title)) title = "WORMDISK";
    var bootOpt = (options?.GetOption("BootOption", "None") ?? "None") switch {
      "LOAD" => 1,
      "RUN"  => 2,
      "EXEC" => 3,
      _      => 0,
    };
    output.Write(w.Build(title, bootOpt));
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new BbcBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new BbcBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware BBC DFS defragmentor. Tries the planner-driven in-place path
  /// first, falling back to the rebuild path on error or for <see cref="DefragMode.CarveHole"/>.
  /// The source DFS directory prefix and load/exec/locked metadata are preserved per file.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch {
        archive.Position = 0;
      }
    }
    DefragmentWithRebuild(archive, options);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    using var snap = new MemoryStream();
    archive.CopyTo(snap);
    var imageData = snap.ToArray();
    var extents = BbcExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    var mover = new BbcBlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 256, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    var meta = new Dictionary<string, (char Dir, uint Load, uint Exec, bool Locked)>();
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new BbcReader(stream, doubleSided: false);
        var list = new List<(string Name, byte[] Data)>();
        foreach (var e in r.Entries) {
          meta[e.FullName] = (e.Directory, e.LoadAddress, e.ExecAddress, e.IsLocked);
          list.Add((e.FullName, r.Extract(e)));
        }
        return list;
      },
      buildImage: files => {
        var w = new BbcWriter();
        foreach (var (fullName, data) in files) {
          var (dir, load, exec, locked) = meta.TryGetValue(fullName, out var m)
            ? m : ('$', 0x1900u, 0x1900u, false);
          var name = fullName.Length >= 2 && fullName[1] == '.' ? fullName[2..] : fullName;
          w.AddFile(name, data, directory: dir, loadAddr: load, execAddr: exec, locked: locked);
        }
        return w.Build();
      });
  }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in a BBC DFS image: every sector not claimed by a
  /// live file, plus the cluster-tip slack — the bytes between a file's logical
  /// length and the end of its last (256-byte) sector. DFS stores each file as a
  /// single contiguous sector run starting at the catalog's start-sector, so the
  /// generic <see cref="UnusedSpaceWiper"/> driven by the DFS extent map plus a
  /// catalog-entry file-size lookup wipes tips precisely.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var doubleSided = imageSize == 400_000;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new BbcReader(image, doubleSided);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          sizeMap[entry.FullName] = entry.Size;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = BbcExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }
}
