#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Dcx;

namespace FileFormat.PngCrushAdapters;

public sealed class DcxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Dcx";
  public string DisplayName => "DCX (multi-image PCX)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dcx";
  public IReadOnlyList<string> Extensions => [".dcx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0xB1, 0x68, 0xDE, 0x3A], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Multi-image PCX (Intel paintbrush); each entry is one image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "image", ReadAll);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "image", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<DcxFile>(DcxReader.FromStream(s));
}
