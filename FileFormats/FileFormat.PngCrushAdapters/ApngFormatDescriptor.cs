#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Apng;
using FileFormat.Core;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Animated PNG (APNG) — PNG with acTL/fcTL/fdAT chunks; each frame is surfaced as one image with disposal/blend applied.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.w3.org/TR/png-3/</c> — W3C PNG Third Edition — APNG folded into the core PNG spec</description></item>
///   <item><description><c>https://wiki.mozilla.org/APNG_Specification</c> — original Mozilla APNG specification</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/APNG</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class ApngFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Apng";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "APNG (animated PNG)";
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
public string DefaultExtension => ".apng";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".apng"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // APNG shares the PNG magic 89 50 4E 47 0D 0A 1A 0A; we only attach via extension
  // so static .png files don't get hijacked away from FileFormat.Png.
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
public string Description => "Animated PNG; each frame is one image with disposal/blend applied.";

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
    MultiImageArchiveHelper.ToRawImages<ApngFile>(ApngReader.FromStream(s));
}
