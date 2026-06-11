#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Default routing for plain PNG files. Surfaces a static PNG as a chunk-structured
/// pseudo-archive through <see cref="StructuralArchiveHelper.DecomposePng"/>:
/// <c>FULL.png</c> + <c>metadata.ini</c> (IHDR fields) + one
/// <c>chunks/NN_&lt;TYPE&gt;.bin</c> per chunk, with tEXt collected to
/// <c>comments.txt</c> and the first iCCP/eXIf payloads exposed as
/// <c>icc.bin</c>/<c>exif.bin</c>.
/// </summary>
/// <remarks>
/// <para>
/// The decomposition is purely structural (raw chunk byte slices), so listing is
/// independent of any pixel decoder and never throws — a malformed PNG still lists
/// FULL + metadata.ini (with <c>parse_status = partial</c>) and whatever chunks
/// were walkable.
/// </para>
/// <para>
/// APNG owns the <c>.apng</c> extension and has empty magic specifically so
/// static PNGs don't get hijacked into the animated path. This descriptor
/// claims the PNG magic + <c>.png</c> extension as the static fallback.
/// </para>
/// </remarks>
public sealed class PngFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IFileInternalLayoutMap, IFileInternalChunkMover {
  public string Id => "Png";
  public string DisplayName => "PNG image";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".png";
  public IReadOnlyList<string> Extensions => [".png"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], Confidence: 0.99),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "PNG chunk stream surfaced as a pseudo-archive: FULL.png + metadata.ini " +
    "(IHDR width/height/bit-depth/color-type) + one chunks/NN_<TYPE>.bin per chunk, " +
    "with tEXt collected to comments.txt and iCCP/eXIf payloads to icc.bin/exif.bin.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    StructuralArchiveHelper.ToArchiveEntries(
      StructuralArchiveHelper.DecomposePng(StructuralArchiveHelper.ReadAllBytes(stream)));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    StructuralArchiveExtract.Extract(
      StructuralArchiveHelper.DecomposePng(StructuralArchiveHelper.ReadAllBytes(stream)), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    StructuralArchiveExtract.ExtractEntry(
      StructuralArchiveHelper.DecomposePng(StructuralArchiveHelper.ReadAllBytes(input)), entryName, output);

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file) => PngLayoutMap.Enumerate(file);

  /// <inheritdoc />
  public void Optimize(Stream file) => PngOptimizer.Optimize(file);

  /// <inheritdoc />
  public void Optimize(Stream file, MetadataPlacementProfile? profile) => PngOptimizer.Optimize(file, profile);
}
