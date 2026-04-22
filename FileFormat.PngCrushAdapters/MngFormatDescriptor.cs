#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Mng;

namespace FileFormat.PngCrushAdapters;

public sealed class MngFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Mng";
  public string DisplayName => "MNG (multi-image PNG)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mng";
  public IReadOnlyList<string> Extensions => [".mng"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x8A, 0x4D, 0x4E, 0x47], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Multiple-image Network Graphics; each frame is one image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "frame", ReadAll);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "frame", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<MngFile>(MngReader.FromStream(s));
}
