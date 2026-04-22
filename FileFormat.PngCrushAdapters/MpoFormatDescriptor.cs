#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Mpo;

namespace FileFormat.PngCrushAdapters;

public sealed class MpoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Mpo";
  public string DisplayName => "MPO (stereoscopic JPEG)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mpo";
  public IReadOnlyList<string> Extensions => [".mpo"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // MPO shares the JPEG SOI marker; extension routing avoids stealing single-image .jpg files.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Multi-Picture Object (stereoscopic JPEG); each view is one image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "view", ReadAll);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "view", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<MpoFile>(MpoReader.FromStream(s));
}
