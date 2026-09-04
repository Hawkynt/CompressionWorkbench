#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.SmartFs;

/// <summary>
/// Descriptor for SmartFS — the wear-levelled raw-flash filesystem in Apache
/// NuttX RTOS. Recognises the "SMRT" format signature near the start of the
/// format sector (NuttX CONFIG_SMARTFS_FORMAT_SIG), walks the root directory
/// and each file's sector chain, and writes volumes in the state
/// <c>mksmartfs</c> leaves behind. An existing volume is edited by reading its
/// files out and laying it out again through the same writer.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/apache/nuttx/tree/master/fs/smartfs</c> — reference implementation (Apache NuttX)</description></item>
///   <item><description>Apache NuttX "SmartFS" documentation and SmartFS Design Document (NuttX project wiki)</description></item>
/// </list>
/// </summary>
public sealed class SmartFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap {

  /// <summary>
  /// Largest volume the in-place pass is offered for. Its guard holds a copy of
  /// the image to compare payloads across the pass.
  /// </summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  /// <summary>
  /// Where the volume keeps its bytes: the format sector and the directory
  /// chain pinned, every sector a file's chain runs through as its own.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => SmartFsExtentMap.Enumerate(image);

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "SmartFs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "SmartFS";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".smartfs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".smartfs", ".smart"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // SMRT signature commonly appears at offset 10 (after 5-byte per-sector
    // header + 5-byte format sector prefix). We declare two offsets so the
    // FormatDetector recognises both common NuttX configurations.
    new("SMRT"u8.ToArray(), Offset: 10, Confidence: 0.85),
    new("SMRT"u8.ToArray(), Offset: 8,  Confidence: 0.80),
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
    "SmartFS wear-levelled raw-flash filesystem (Apache NuttX). Reads the format sector, walks " +
    "the root directory and each file's sector chain, and writes a volume in the state mksmartfs " +
    "leaves behind: logical sector N in physical sector N, sequence numbers at zero, free " +
    "sectors erased. Wear-level rotation and CRC-protected sector headers are what a running " +
    "NuttX target adds afterwards; neither is needed to read or lay out a volume.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SmartFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SmartFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Lays a fresh volume out holding the inputs. Sector size is the caller's
  /// choice among the five SmartFS allows; the volume is sized to its contents
  /// unless a larger one is asked for.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();

    var sectorSize = options.GetOptionInt("SectorSize", 1024);
    output.Write(BuildImage(FilesOnly(inputs).ToList(), sectorSize));
  }

  /// <summary>
  /// Adds or replaces files: the volume's files are read out, the inputs merged
  /// in by name, and the volume laid out again at its own sector size.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var sectorSize = SectorSizeOf(archive);
    ModifyRebuilder.Add(archive, inputs, ReadEntries, files => BuildImage(files, sectorSize), StringComparer.Ordinal);
  }

  /// <summary>
  /// Removes the named files and lays the volume out again without them, so
  /// nothing of their bytes remains.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var sectorSize = SectorSizeOf(archive);
    ModifyRebuilder.Remove(archive, entryNames, ReadEntries, files => BuildImage(files, sectorSize), StringComparer.Ordinal);
  }

  /// <summary>The sector size the volume was formatted with, so a rebuild keeps it.</summary>
  private static int SectorSizeOf(Stream archive) {
    archive.Position = 0;
    var reader = new SmartFsReader(archive);
    return reader.ValidFormatSector && reader.SectorSize > 0 ? (int)reader.SectorSize : 1024;
  }

  /// <summary>The files a volume holds — never the entries that describe the volume.</summary>
  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    stream.Position = 0;
    var reader = new SmartFsReader(stream);
    return reader.Entries
      .Where(e => !e.IsDirectory && !IsSynthetic(e.Name))
      .Select(e => (e.Name, reader.Extract(e)))
      .ToList();
  }

  /// <summary>A fresh volume holding exactly the given files.</summary>
  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files, int sectorSize) {
    var writer = new SmartFsWriter { SectorSize = sectorSize };
    foreach (var (name, data) in files) {
      var leaf = Path.GetFileName(name);
      if (IsSynthetic(leaf)) continue;
      writer.AddFile(leaf, data);
    }
    return writer.Build();
  }

  /// <summary>
  /// Rewrites the volume with every file's sectors consecutive. SmartFS chains
  /// its sectors rather than requiring them to be adjacent, so the gain is
  /// sequential reads rather than a structural repair — and the rebuild is what
  /// produces it, since a fresh layout is contiguous by construction.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the volume out again: a file is
    // a chain of sectors, and each sector is named by exactly one field — the
    // directory entry, or the sector before it. So a move is the copy plus two
    // bytes, and putting a file's sectors in order is what makes it read in one
    // sweep instead of hopping about the flash.
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

    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new SmartFsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory && !IsSynthetic(e.Name))
                        .Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new SmartFsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    var reader = new SmartFsReader(stream);
    return reader.Entries
      .Where(e => !e.IsDirectory && !IsSynthetic(e.Name))
      .Select(reader.Extract)
      .ToList();
  }

  /// <summary>Plans the new layout and moves the sectors into it, repointing as it goes.</summary>
  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new SmartFsBlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var extents = SmartFsExtentMap.Enumerate(archive).ToList();
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
    var postExtents = SmartFsExtentMap.Enumerate(archive).ToList();

    // Whichever sectors moved, what is free is known only once they all have:
    // a sector's old home is routinely another's new one.
    mover.SettleFreeSectors(archive, postExtents
      .Where(e => e.Kind != DefragBlockKind.Free)
      .Select(e => (e.Offset, e.Length)));

    archive.Position = 0;
    postExtents = SmartFsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// The entries the reader surfaces that are not files on the volume — the raw
  /// image and the format-sector summary.
  /// </summary>
  private static bool IsSynthetic(string name)
    => name is "FULL.smartfs" or "metadata.ini";
}
