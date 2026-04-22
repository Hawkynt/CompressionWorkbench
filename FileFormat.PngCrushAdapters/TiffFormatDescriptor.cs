#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Tiff;

namespace FileFormat.PngCrushAdapters;

public sealed class TiffFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
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
  public string Description => "Multi-page TIFF; each IFD is one page extracted as PNG.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "page", ReadAll);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "page", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<TiffFile>(TiffReader.FromStream(s));
}
