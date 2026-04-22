#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.BigTiff;
using FileFormat.Core;

namespace FileFormat.PngCrushAdapters;

public sealed class BigTiffFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "BigTiff";
  public string DisplayName => "BigTIFF (large multi-page)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".btf";
  public IReadOnlyList<string> Extensions => [".btf", ".bigtiff"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x49, 0x49, 0x2B, 0x00], Confidence: 0.90),
    new([0x4D, 0x4D, 0x00, 0x2B], Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BigTIFF (>4 GB) container; each IFD is one page.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "page", ReadAll);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "page", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<BigTiffFile>(BigTiffReader.FromStream(s));
}
