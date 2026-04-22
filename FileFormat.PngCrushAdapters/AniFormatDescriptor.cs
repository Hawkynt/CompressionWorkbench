#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Ani;
using FileFormat.Core;

namespace FileFormat.PngCrushAdapters;

public sealed class AniFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Ani";
  public string DisplayName => "ANI (animated cursor)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ani";
  public IReadOnlyList<string> Extensions => [".ani"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // RIFF "ACON" container; the leading bytes are 'RIFF' + size + 'ACON'.
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([(byte)'R', (byte)'I', (byte)'F', (byte)'F'], Confidence: 0.40)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Windows animated cursor; each frame is one image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "frame", ReadAll);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "frame", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<AniFile>(AniReader.FromStream(s));
}
