#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

public sealed class MpoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
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
  public string Description =>
    "Multi-Picture Object (stereoscopic JPEG) surfaced as a pseudo-archive: " +
    "FULL.mpo + metadata.ini (picture count) + one JPEG per embedded picture " +
    "(pictures/picture_NN.jpg), split by SOI..EOI marker pairs.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    StructuralArchiveHelper.ToArchiveEntries(
      StructuralArchiveHelper.DecomposeMpo(StructuralArchiveHelper.ReadAllBytes(stream)));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    StructuralArchiveExtract.Extract(
      StructuralArchiveHelper.DecomposeMpo(StructuralArchiveHelper.ReadAllBytes(stream)), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    StructuralArchiveExtract.ExtractEntry(
      StructuralArchiveHelper.DecomposeMpo(StructuralArchiveHelper.ReadAllBytes(input)), entryName, output);
}
