#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Apng;
using FileFormat.Core;

namespace FileFormat.PngCrushAdapters;

public sealed class ApngFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Apng";
  public string DisplayName => "APNG (animated PNG)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".apng";
  public IReadOnlyList<string> Extensions => [".apng"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // APNG shares the PNG magic 89 50 4E 47 0D 0A 1A 0A; we only attach via extension
  // so static .png files don't get hijacked away from FileFormat.Png.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Animated PNG; each frame is one image with disposal/blend applied.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "frame", ReadAll);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "frame", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<ApngFile>(ApngReader.FromStream(s));
}
