#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Apple ICNS icon container — typed icon-family elements split via the element table.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/Apple_Icon_Image_format</c> — Wikipedia — documents the header and element types</description></item>
///   <item><description><c>https://sourceforge.net/projects/icns/</c> — libicns — open ICNS reader/writer</description></item>
///   <item><description>format never published by Apple; layout reverse-engineered from the classic icon-family resource</description></item>
/// </list>
/// </summary>
public sealed class IcnsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
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
  public string Description =>
    "Apple icon suite (ICNS) surfaced as a pseudo-archive: FULL.icns + metadata.ini " +
    "(element count) + one sub-image per icon element (icons/<OSType>.png|.jp2|.bin), " +
    "split via the ICNS element table.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    StructuralArchiveHelper.ToArchiveEntries(
      StructuralArchiveHelper.DecomposeIcns(StructuralArchiveHelper.ReadAllBytes(stream)));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    StructuralArchiveExtract.Extract(
      StructuralArchiveHelper.DecomposeIcns(StructuralArchiveHelper.ReadAllBytes(stream)), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    StructuralArchiveExtract.ExtractEntry(
      StructuralArchiveHelper.DecomposeIcns(StructuralArchiveHelper.ReadAllBytes(input)), entryName, output);
}
