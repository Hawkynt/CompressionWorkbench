#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Multi-page TIFF container; each IFD is extracted as its own single-page TIFF with strip/tile data re-based.
///
/// References:
/// <list type="bullet">
///   <item><description>Adobe "TIFF Revision 6.0" specification (June 1992) — the canonical format definition</description></item>
///   <item><description><c>https://libtiff.gitlab.io/libtiff/</c> — libtiff — the reference implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/TIFF</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class TiffFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IFileInternalLayoutMap {
  public string Id => "Tiff";
  public string DisplayName => "TIFF (multi-page)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tif";
  public IReadOnlyList<string> Extensions => [".tif", ".tiff"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x49, 0x49, 0x2A, 0x00], Confidence: 0.85), // little-endian TIFF
    new([0x4D, 0x4D, 0x00, 0x2A], Confidence: 0.85), // big-endian TIFF
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Multi-page TIFF surfaced as a pseudo-archive: FULL.tif + metadata.ini " +
    "(byte-order, page count) + one self-contained single-page TIFF per IFD " +
    "(pages/page_NNN.tif) with strip/tile data re-based into each page.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    StructuralArchiveHelper.ToArchiveEntries(
      StructuralArchiveHelper.DecomposeTiff(StructuralArchiveHelper.ReadAllBytes(stream)));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    StructuralArchiveExtract.Extract(
      StructuralArchiveHelper.DecomposeTiff(StructuralArchiveHelper.ReadAllBytes(stream)), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    StructuralArchiveExtract.ExtractEntry(
      StructuralArchiveHelper.DecomposeTiff(StructuralArchiveHelper.ReadAllBytes(input)), entryName, output);

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file) => TiffLayoutMap.Enumerate(file);
}
