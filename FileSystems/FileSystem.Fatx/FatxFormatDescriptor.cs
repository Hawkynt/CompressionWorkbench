#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Fatx;

/// <summary>
/// R/W descriptor for Microsoft Xbox / Xbox 360 FATX volumes.
/// Magic "FATX" at offset 0; 4 KiB superblock followed by FAT16/FAT32 table.
/// Read via <see cref="FatxReader"/>, create via <see cref="FatxWriter"/>,
/// mutate via <see cref="FatxModifier"/> (in-place Add/Remove on the root
/// directory; sub-directory mutation stays out of scope).
/// </summary>
public sealed class FatxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// Creation knobs surfaced by the Convert dialog / CLI. <c>SectorsPerCluster</c>
  /// is the FATX allocation unit (512-byte sectors): leave it at "auto" (0) to let
  /// the layout optimiser minimise file-tail slack for the actual file-set, or pin
  /// a power-of-two value. Real Xbox HDDs use 32 (16 KiB clusters).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("SectorsPerCluster", "Sectors per cluster", FormatOptionKind.Enum, "0",
      AllowedValues: ["0", "4", "8", "16", "32", "64", "128"],
      Description: "FATX cluster size in 512-byte sectors (0 = auto-optimise for least slack; 32 = 16 KiB Xbox default)."),
    new("VolumeId", "Volume ID", FormatOptionKind.String, "",
      Description: "32-bit volume identifier (hex or decimal). Blank = 0x12345678."),
  ];
  public string Id => "Fatx";
  public string DisplayName => "FATX (Xbox)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".fatx";
  public IReadOnlyList<string> Extensions => [".fatx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'F', (byte)'A', (byte)'T', (byte)'X'], Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Xbox/Xbox 360 FATX filesystem image (R/W: list/extract/create/add/remove at root; FAT16+FAT32 width-aware).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FatxReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FatxReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new FatxReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Emits a fresh FATX volume containing <paramref name="inputs"/> via
  /// <see cref="FatxWriter"/>. Path components in <c>ArchiveName</c> become
  /// nested FATX subdirectories (one cluster chain per directory); files
  /// are stored contiguously starting at the next free cluster.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new FatxWriter();
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);

    // Sectors-per-cluster: 0 (or unset) hands the choice to the writer's layout
    // optimiser; an explicit power-of-two value is honoured verbatim so pinned
    // sizes stay byte-identical.
    var spc = options.GetOptionInt("SectorsPerCluster", 0);
    var volIdStr = options.GetOption("VolumeId", "");
    var volumeId = 0x12345678u;
    if (!string.IsNullOrEmpty(volIdStr)) {
      var span = volIdStr.AsSpan();
      var hex = span.StartsWith("0x") || span.StartsWith("0X");
      if (hex
            ? uint.TryParse(span[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            : uint.TryParse(span, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed))
        volumeId = parsed;
    }

    var image = w.Build(sectorsPerCluster: spc, volumeId: volumeId);
    output.Write(image);
  }

  /// <summary>
  /// In-place add: each input becomes a new dirent in the root cluster of the
  /// existing FATX image, with its bytes written into the first contiguous
  /// free cluster run found in the FAT. Sub-directory adds are not supported
  /// by v1 — only leaf filenames go to root. The FAT16/FAT32 width is
  /// auto-detected from the on-disk geometry.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      FatxModifier.AddFile(image, input.ArchiveName, input.ReadContent());
    }
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  /// <summary>
  /// In-place remove: tombstones each named dirent (name_length = 0xE5) and
  /// frees + wipes every data cluster in the file's FAT chain. Unknown names
  /// are silently skipped (consistent with how WORM Extract treats them).
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var name in entryNames)
      FatxModifier.RemoveFile(image, name);
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  // ── ILayoutOptimizable ────────────────────────────────────────────────
  //
  // FATX is the canonical fit for this contract: the allocation unit
  // (sectors-per-cluster) is reader-agnostic, so any legal cluster size
  // round-trips, and a cluster-size change is purely a structural rebuild.
  // The per-file cluster-tail slack is exactly what the shared optimiser
  // minimises. PatchInPlace handles the metadata-only volume-id field; a
  // cluster-size change is routed to RebuildStreaming.

  /// <inheritdoc />
  public LayoutAnalysis AnalyzeLayout(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.CanSeek) image.Position = 0;
    var reader = new FatxReader(image);
    var fileSizes = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Size).ToList();
    var current = reader.ClusterSize;

    int[] candidates = [2048, 4096, 8192, 16384, 32768, 65536];
    var optimal = Compression.Core.Layout.LayoutOptimizerAdapter.SelectAllocationUnit(
      candidates,
      fileSizes,
      fixedOverhead: clusterBytes => {
        var dataClusters = Compression.Core.Layout.FilesystemLayoutOptimizer.DataClusters(fileSizes, clusterBytes);
        var entryBytes = dataClusters < 0xFFF4 ? 2L : 4L;
        return (((dataClusters + 2) * entryBytes) + 0xFFFL) & ~0xFFFL;
      });

    var currentSlack = Compression.Core.Layout.LayoutOptimizerAdapter.SlackAt(fileSizes, current);
    var optimalSlack = Compression.Core.Layout.LayoutOptimizerAdapter.SlackAt(fileSizes, optimal);
    return new LayoutAnalysis {
      ImageSize = image.CanSeek ? image.Length : 0,
      CurrentUnitSize = current,
      CurrentSlackBytes = currentSlack,
      OptimalUnitSize = optimal,
      OptimalSlackBytes = optimalSlack,
      InPlaceChanges = ["volume id"],
      RequiresRebuild = optimal != current ? ["cluster size"] : [],
      Notes = optimal == current
        ? ["Cluster size is already optimal for this file-set."]
        : [$"Rebuild at {optimal}-byte clusters saves {currentSlack - optimalSlack} slack bytes."],
    };
  }

  /// <inheritdoc />
  public void PatchInPlace(Stream image, LayoutPatch patch) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(patch);
    if (patch.SerialNumber is { } serial) {
      // FATX volume_id lives at superblock offset 0x04 (little-endian u32).
      Span<byte> buf = stackalloc byte[4];
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, serial);
      image.Position = 0x04;
      image.Write(buf);
    }
    // FATX carries no on-disk volume label, so VolumeLabel is a no-op here.
  }

  /// <inheritdoc />
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);
    if (source.CanSeek) source.Position = 0;
    var reader = new FatxReader(source);
    var w = new FatxWriter();
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      w.AddFile(e.Name, reader.Extract(e));
    }
    // UnitSize 0 = auto-optimise; an explicit byte size maps to sectors-per-cluster.
    var spc = options.UnitSize > 0 ? options.UnitSize / FatxReader.SectorSize : 0;
    var image = w.Build(sectorsPerCluster: spc);
    target.Write(image);
    options.OnProgress?.Invoke(image.Length, image.Length);
  }
}
