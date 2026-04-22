#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Ico;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Exposes a Windows <c>.ico</c> as an archive of its individual icon images,
/// using PngCrushCS's <see cref="IcoFile"/> reader. Extraction encodes each entry
/// as PNG so the output opens in any image viewer.
/// </summary>
public sealed class IcoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Ico";
  public string DisplayName => "ICO (Windows icon)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ico";
  public IReadOnlyList<string> Extensions => [".ico"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x00, 0x00, 0x01, 0x00], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Windows ICO container; each directory entry is one image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "icon", ReadAll);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "icon", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<IcoFile>(IcoReader.FromStream(s));
}
