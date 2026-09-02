#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Mng;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Multiple-image Network Graphics (MNG) — PNG-family multi-frame container; each frame is one image.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://www.libpng.org/pub/mng/spec/</c> — MNG 1.0 specification (PNG Development Group)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Multiple-image_Network_Graphics</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class MngFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Mng";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "MNG (multi-image PNG)";
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
public string DefaultExtension => ".mng";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".mng"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x8A, 0x4D, 0x4E, 0x47], Confidence: 0.95)];
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
public string Description => "Multiple-image Network Graphics; each frame is one image.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "frame", ReadAll);

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "frame", ReadAll);

  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<MngFile>(MngReader.FromStream(s));
}
