#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.BigTiff;
using FileFormat.Core;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// BigTIFF container (64-bit TIFF variant for files over 4 GB); each IFD is surfaced as one page.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.awaresystems.be/imaging/tiff/bigtiff.html</c> — BigTIFF design description (AWare Systems)</description></item>
///   <item><description><c>https://libtiff.gitlab.io/libtiff/</c> — libtiff — the reference TIFF/BigTIFF implementation</description></item>
///   <item><description>Adobe "TIFF Revision 6.0" specification (1992) — the baseline BigTIFF extends</description></item>
/// </list>
/// </summary>
public sealed class BigTiffFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BigTiff";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "BigTIFF (large multi-page)";
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
  public string DefaultExtension => ".btf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".btf", ".bigtiff"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x49, 0x49, 0x2B, 0x00], Confidence: 0.90),
    new([0x4D, 0x4D, 0x00, 0x2B], Confidence: 0.90),
  ];
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
  public string Description => "BigTIFF (>4 GB) container; each IFD is one page.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "page", ReadAll);

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "page", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<BigTiffFile>(BigTiffReader.FromStream(s));
}
