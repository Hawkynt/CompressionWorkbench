#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

public sealed class DcxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
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
  public string Description =>
    "Multi-page PCX (DCX) surfaced as a pseudo-archive: FULL.dcx + metadata.ini " +
    "(page count) + one PCX per page (pages/page_NNN.pcx), split via the DCX " +
    "page-offset table.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    StructuralArchiveHelper.ToArchiveEntries(
      StructuralArchiveHelper.DecomposeDcx(StructuralArchiveHelper.ReadAllBytes(stream)));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    StructuralArchiveExtract.Extract(
      StructuralArchiveHelper.DecomposeDcx(StructuralArchiveHelper.ReadAllBytes(stream)), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    StructuralArchiveExtract.ExtractEntry(
      StructuralArchiveHelper.DecomposeDcx(StructuralArchiveHelper.ReadAllBytes(input)), entryName, output);
}
