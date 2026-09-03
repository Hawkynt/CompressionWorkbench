#pragma warning disable CS1591
using System.Globalization;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.SmartFs;

/// <summary>
/// SmartFS descriptor for the wear-levelled raw-flash filesystem in Apache
/// NuttX. The workbench reads and writes the logical sector-chain view; the
/// running target's MTD layer is responsible for physical wear-level rotation.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/apache/nuttx/tree/master/fs/smartfs</c> — reference implementation (Apache-2.0)</description></item>
///   <item><description>Apache NuttX SmartFS documentation — logical sectors are powers of two from 256 through 32768 bytes.</description></item>
/// </list>
/// </summary>
public sealed class SmartFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveShrinkable, IArchiveDefragmentable,
    IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap {

  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.Ordinal) { "FULL.smartfs", "metadata.ini" };

  /// <summary>
  /// Largest volume the in-place defragmenter is offered for. Its guard holds a
  /// copy of the image to compare payloads across the pass.
  /// </summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  /// <summary>The format's one real geometry knob.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("SectorSize", "Logical sector size", FormatOptionKind.Enum, "1024",
      AllowedValues: SmartFsLayout.SectorSizes.Select(static s => s.ToString(CultureInfo.InvariantCulture)).ToArray(),
      Description: "SmartFS logical sector size in bytes. NuttX stores a three-bit size code and permits powers of two from 256 through 32768; smaller sectors reduce small-file slack while larger sectors reduce mapping overhead."),
  ];

  /// <summary>Where the volume keeps its bytes.</summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => SmartFsExtentMap.Enumerate(image);

  public string Id => "SmartFs";
  public string DisplayName => "SmartFS";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".smartfs";
  public IReadOnlyList<string> Extensions => [".smartfs", ".smart"];
  public IReadOnlyList<string> CompoundExtensions => [];

  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SMRT"u8.ToArray(), Offset: 10, Confidence: 0.85),
    new("SMRT"u8.ToArray(), Offset: 8, Confidence: 0.80),
  ];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  public string Description =>
    "SmartFS wear-levelled raw-flash filesystem (Apache NuttX). Reads the format sector, walks " +
    "the root directory and each file's sector chain, and writes a freshly formatted logical " +
    "sector map. Existing images can be rebuilt for add/replace/remove, shrink, sector-size " +
    "optimization, purge and defragmentation; the MTD layer's physical wear rotation remains " +
    "the target device's job.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new SmartFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new SmartFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Lays a fresh volume out holding the inputs. Sector size is the caller's
  /// choice among the eight values SmartFS can encode. ImageSize is an internal
  /// rebuild parameter; when omitted the volume is tight-sized to its contents.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();

    var sectorSize = options.GetOptionInt("SectorSize", 1024);
    _ = SmartFsLayout.SizeCode(sectorSize); // validate before reading inputs

    var writer = new SmartFsWriter { SectorSize = sectorSize };
    foreach (var (name, data) in FilesOnly(inputs))
      writer.AddFile(Path.GetFileName(name), data);

    var totalSectors = 0;
    var requestedImageSize = options.GetString("ImageSize");
    if (!string.IsNullOrWhiteSpace(requestedImageSize)
        && long.TryParse(requestedImageSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var imageSize)
        && imageSize > 0) {
      totalSectors = checked((int)(imageSize / sectorSize));
      if (totalSectors > ushort.MaxValue)
        throw new InvalidOperationException(
          $"SmartFS: {totalSectors:N0} logical sectors exceed the 16-bit sector-number space.");
    }

    output.Write(writer.Build(totalSectors));
  }

  // ── Existing-instance editing ────────────────────────────────────────

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var sectorSize = ReadSectorSize(archive);
    ModifyRebuilder.Add(archive, inputs, ReadLiveFiles,
      files => BuildImage(files, sectorSize));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var sectorSize = ReadSectorSize(archive);
    ModifyRebuilder.Remove(archive, entryNames, ReadLiveFiles,
      files => BuildImage(files, sectorSize));
  }

  /// <summary>
  /// Empties the volume without changing its outer size or logical sector size.
  /// This is deliberately not implemented as Remove(all): diagnostics such as
  /// FULL.smartfs and metadata.ini are synthetic archive-view entries rather
  /// than files on the volume and must continue to exist after purge.
  /// </summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("SmartFS purge requires a readable, writable, seekable stream.", nameof(archive));

    var sectorSize = ReadSectorSize(archive);
    if (archive.Length % sectorSize != 0)
      throw new InvalidDataException("SmartFS image length is not a whole number of logical sectors.");

    var totalSectors = checked((int)(archive.Length / sectorSize));
    var empty = BuildImage([], sectorSize, totalSectors);
    if (empty.LongLength != archive.Length)
      throw new InvalidOperationException("SmartFS purge would change the outer image size.");

    archive.Position = 0;
    archive.Write(empty);
    archive.SetLength(empty.Length);
    archive.Flush();
  }

  // ── Shrink / layout optimization ────────────────────────────────────

  /// <summary>
  /// Rebuilds with the existing logical sector size and no surplus sectors.
  /// The source is copied through unchanged when rebuilding is not smaller or
  /// cannot be verified.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var sectorSize = ReadSectorSize(input);
    var tempPath = Path.Combine(Path.GetTempPath(), "cwb_smartfs_shrink_" + Guid.NewGuid().ToString("N") + ".tmp");
    var useRebuilt = false;
    try {
      using (var rebuilt = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
               64 * 1024, FileOptions.DeleteOnClose | FileOptions.SequentialScan)) {
        try {
          RebuildVerb.RebuildToStream(input, rebuilt, this, this,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
              ["SectorSize"] = sectorSize.ToString(CultureInfo.InvariantCulture),
            }, SyntheticNames);
          useRebuilt = rebuilt.Length > 0 && rebuilt.Length < input.Length;
        } catch {
          useRebuilt = false;
        }

        output.Position = 0;
        output.SetLength(0);
        if (useRebuilt) {
          rebuilt.Position = 0;
          rebuilt.CopyTo(output);
        } else {
          input.Position = 0;
          input.CopyTo(output);
        }
      }
    } finally {
      try { File.Delete(tempPath); } catch { /* DeleteOnClose or best effort */ }
    }
  }

  public LayoutAnalysis AnalyzeLayout(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek)
      throw new ArgumentException("SmartFS layout analysis requires a seekable stream.", nameof(image));

    var originalPosition = image.Position;
    try {
      image.Position = 0;
      using var reader = new SmartFsReader(image);
      var current = checked((int)reader.SectorSize);
      if (SmartFsLayout.SizeFromCode(SmartFsLayout.SizeCode(current)) != current)
        throw new InvalidDataException($"SmartFS: unsupported logical sector size {current}.");

      var sizes = reader.Entries
        .Where(static e => !e.IsDirectory && !SyntheticNames.Contains(e.Name))
        .Select(static e => (long)e.Size)
        .ToArray();
      var logicalBytes = sizes.Sum();
      var optimal = SmartFsLayout.SectorSizes
        .OrderBy(size => ProjectedImageSize(sizes, size))
        .ThenBy(size => size == current ? 0 : 1)
        .ThenBy(static size => size)
        .First();
      var optimalSize = ProjectedImageSize(sizes, optimal);

      return new LayoutAnalysis {
        ImageSize = image.Length,
        CurrentUnitSize = current,
        CurrentSlackBytes = Math.Max(0, image.Length - logicalBytes),
        OptimalUnitSize = optimal,
        OptimalSlackBytes = Math.Max(0, optimalSize - logicalBytes),
        RequiresRebuild = optimal == current ? [] : ["Logical sector size"],
        Notes = [
          $"SmartFS allocation is one logical sector chain per file; {optimal:N0}-byte sectors minimize projected image size for the current file set.",
          "Slack figures include filesystem/sector headers and free geometry because changing sector size changes both allocation slack and metadata overhead.",
        ],
      };
    } finally {
      image.Position = originalPosition;
    }
  }

  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);

    var analysis = AnalyzeLayout(source);
    var sectorSize = options.UnitSize > 0 ? options.UnitSize : analysis.OptimalUnitSize;
    _ = SmartFsLayout.SizeCode(sectorSize);

    var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["SectorSize"] = sectorSize.ToString(CultureInfo.InvariantCulture),
    };
    if (options.ImageSize > 0)
      parameters["ImageSize"] = options.ImageSize.ToString(CultureInfo.InvariantCulture);
    if (options.Parameters != null)
      foreach (var pair in options.Parameters)
        parameters[pair.Key] = pair.Value;

    RebuildVerb.RebuildToStream(source, target, this, this, parameters, SyntheticNames);
  }

  // ── Defragmentation ─────────────────────────────────────────────────

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    if (archive.CanSeek && archive.Length <= MaxBufferedImageBytes) {
      var planned = false;
      DefragContentGuard.RunOrRebuild(archive,
        readContents: ReadPayloadsForGuard,
        inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }

    var sectorSize = ReadSectorSize(archive);
    DefragRebuilder.Rebuild(archive, options,
      readEntries: ReadLiveFiles,
      buildImage: files => BuildImage(files, sectorSize));
  }

  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    using var reader = new SmartFsReader(stream);
    return reader.Entries
      .Where(e => !e.IsDirectory && !IsSynthetic(e.Name))
      .Select(reader.Extract)
      .ToList();
  }

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
    mover.SettleFreeSectors(archive, postExtents
      .Where(e => e.Kind != DefragBlockKind.Free)
      .Select(e => (e.Offset, e.Length)));

    archive.Position = 0;
    postExtents = SmartFsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  private static bool IsSynthetic(string name) => SyntheticNames.Contains(name);

  private static int ReadSectorSize(Stream stream) {
    var originalPosition = stream.CanSeek ? stream.Position : 0;
    try {
      if (stream.CanSeek) stream.Position = 0;
      using var reader = new SmartFsReader(stream);
      var result = checked((int)reader.SectorSize);
      _ = SmartFsLayout.SizeCode(result);
      return result;
    } finally {
      if (stream.CanSeek) stream.Position = originalPosition;
    }
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadLiveFiles(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var reader = new SmartFsReader(stream);
    foreach (var entry in reader.Entries)
      if (!entry.IsDirectory && !IsSynthetic(entry.Name))
        yield return (entry.Name, reader.Extract(entry));
  }

  private static byte[] BuildImage(
      IReadOnlyList<(string Name, byte[] Data)> files,
      int sectorSize,
      int totalSectors = 0) {
    var writer = new SmartFsWriter { SectorSize = sectorSize };
    foreach (var (name, data) in files)
      writer.AddFile(Path.GetFileName(name), data);
    return writer.Build(totalSectors);
  }

  private static long ProjectedImageSize(IReadOnlyList<long> fileSizes, int sectorSize) {
    _ = SmartFsLayout.SizeCode(sectorSize);
    var payload = sectorSize - SmartFsLayout.SectorHeaderSize - SmartFsLayout.ChainHeaderSize;
    var entriesPerSector = payload / SmartFsLayout.EntrySize;
    if (entriesPerSector <= 0) return long.MaxValue;

    var rootSectors = Math.Max(1L, (fileSizes.Count + entriesPerSector - 1L) / entriesPerSector);
    long fileSectors = 0;
    foreach (var size in fileSizes)
      fileSectors = checked(fileSectors + Math.Max(1L, (Math.Max(0, size) + payload - 1L) / payload));

    var sectors = checked((long)SmartFsLayout.FirstDataSector + rootSectors + fileSectors);
    return checked(sectors * sectorSize);
  }
}
