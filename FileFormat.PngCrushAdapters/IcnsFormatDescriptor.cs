#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Icns;

namespace FileFormat.PngCrushAdapters;

public sealed class IcnsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Icns";
  public string DisplayName => "ICNS (Apple icon)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".icns";
  public IReadOnlyList<string> Extensions => [".icns"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x69, 0x63, 0x6E, 0x73], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apple icon image format; each variant is one image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "icon", ReadAll);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "icon", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<IcnsFile>(IcnsReader.FromStream(s));
}
