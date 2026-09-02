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
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Png";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "PNG image";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Image;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".png";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".png"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], Confidence: 0.99),
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
    "PNG chunk stream surfaced as a pseudo-archive: FULL.png + metadata.ini " +
    "(IHDR width/height/bit-depth/color-type) + one chunks/NN_<TYPE>.bin per chunk, " +
    "with tEXt collected to comments.txt and iCCP/eXIf payloads to icc.bin/exif.bin.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    StructuralArchiveHelper.ToArchiveEntries(
      StructuralArchiveHelper.DecomposePng(StructuralArchiveHelper.ReadAllBytes(stream)));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    StructuralArchiveExtract.Extract(
      StructuralArchiveHelper.DecomposePng(StructuralArchiveHelper.ReadAllBytes(stream)), outputDir, files);

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    StructuralArchiveExtract.ExtractEntry(
      StructuralArchiveHelper.DecomposePng(StructuralArchiveHelper.ReadAllBytes(input)), entryName, output);

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the chunks.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file) => PngLayoutMap.Enumerate(file);

  /// <inheritdoc />
  /// <summary>
  /// Performs the optimize operation.
  /// </summary>
public void Optimize(Stream file) => PngOptimizer.Optimize(file);

  /// <inheritdoc />
  /// <summary>
  /// Performs the optimize operation.
  /// </summary>
public void Optimize(Stream file, MetadataPlacementProfile? profile) => PngOptimizer.Optimize(file, profile);
}
