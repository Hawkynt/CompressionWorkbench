#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Cur;

namespace FileFormat.PngCrushAdapters;

public sealed class CurFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Cur";
  public string DisplayName => "CUR (Windows cursor)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".cur";
  public IReadOnlyList<string> Extensions => [".cur"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x00, 0x00, 0x02, 0x00], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Windows CUR cursor container; each directory entry is one cursor image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "cursor", ReadAll);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "cursor", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<CurFile>(CurReader.FromStream(s));
}
