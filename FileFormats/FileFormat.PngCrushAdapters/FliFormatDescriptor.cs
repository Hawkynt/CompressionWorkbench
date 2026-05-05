#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Fli;

namespace FileFormat.PngCrushAdapters;

public sealed class FliFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Fli";
  public string DisplayName => "FLI/FLC (Autodesk animation)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".fli";
  public IReadOnlyList<string> Extensions => [".fli", ".flc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // FLI/FLC magic lives at offset 4 (frame size at offset 0 first); extension routing covers detection.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Autodesk Animator FLI/FLC; each frame is one image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "frame", ReadAll);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "frame", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<FliFile>(FliReader.FromStream(s));
}
