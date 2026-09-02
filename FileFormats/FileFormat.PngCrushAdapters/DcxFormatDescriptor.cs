#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// DCX multi-image PCX container — a 1024-entry page-offset table in front of concatenated PCX pages.
///
/// References:
/// <list type="bullet">
///   <item><description>ZSoft "PCX Technical Reference Manual" — defines PCX and the DCX multi-page envelope</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/PCX</c> — Wikipedia PCX article (covers the DCX fax container)</description></item>
/// </list>
/// </summary>
public sealed class DcxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Dcx";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "DCX (multi-image PCX)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".dcx";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".dcx"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0xB1, 0x68, 0xDE, 0x3A], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Multi-page PCX (DCX) surfaced as a pseudo-archive: FULL.dcx + metadata.ini " +
    "(page count) + one PCX per page (pages/page_NNN.pcx), split via the DCX " +
    "page-offset table.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    StructuralArchiveHelper.ToArchiveEntries(
      StructuralArchiveHelper.DecomposeDcx(StructuralArchiveHelper.ReadAllBytes(stream)));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    StructuralArchiveExtract.Extract(
      StructuralArchiveHelper.DecomposeDcx(StructuralArchiveHelper.ReadAllBytes(stream)), outputDir, files);

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    StructuralArchiveExtract.ExtractEntry(
      StructuralArchiveHelper.DecomposeDcx(StructuralArchiveHelper.ReadAllBytes(input)), entryName, output);
}
