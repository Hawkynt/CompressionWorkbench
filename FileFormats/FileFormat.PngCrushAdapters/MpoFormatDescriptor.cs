#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// MPO stereoscopic/multi-picture JPEG container — concatenated JPEG images split by SOI..EOI marker pairs.
///
/// References:
/// <list type="bullet">
///   <item><description>CIPA DC-007 "Multi-Picture Format" — the defining standard (Camera and Imaging Products Association)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Multi_Picture_Object</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class MpoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Mpo";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "MPO (stereoscopic JPEG)";
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
public string DefaultExtension => ".mpo";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".mpo"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // MPO shares the JPEG SOI marker; extension routing avoids stealing single-image .jpg files.
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
    "Multi-Picture Object (stereoscopic JPEG) surfaced as a pseudo-archive: " +
    "FULL.mpo + metadata.ini (picture count) + one JPEG per embedded picture " +
    "(pictures/picture_NN.jpg), split by SOI..EOI marker pairs.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    StructuralArchiveHelper.ToArchiveEntries(
      StructuralArchiveHelper.DecomposeMpo(StructuralArchiveHelper.ReadAllBytes(stream)));

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    StructuralArchiveExtract.Extract(
      StructuralArchiveHelper.DecomposeMpo(StructuralArchiveHelper.ReadAllBytes(stream)), outputDir, files);

    /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    StructuralArchiveExtract.ExtractEntry(
      StructuralArchiveHelper.DecomposeMpo(StructuralArchiveHelper.ReadAllBytes(input)), entryName, output);
}
