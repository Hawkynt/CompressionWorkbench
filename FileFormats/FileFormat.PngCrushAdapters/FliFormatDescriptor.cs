#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Fli;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Autodesk Animator FLI/FLC animation; each frame is surfaced as one image.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.compuphase.com/flic.htm</c> — "The FLIC file format" (CompuPhase) — the classic public format description</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/FLIC_(file_format)</c> — Wikipedia overview</description></item>
///   <item><description>Autodesk Animator / Animator Pro — the defining tools</description></item>
/// </list>
/// </summary>
public sealed class FliFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Fli";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "FLI/FLC (Autodesk animation)";
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
  public string DefaultExtension => ".fli";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".fli", ".flc"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // FLI/FLC magic lives at offset 4 (frame size at offset 0 first); extension routing covers detection.
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
  public string Description => "Autodesk Animator FLI/FLC; each frame is one image.";

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
    MultiImageArchiveHelper.ToRawImages<FliFile>(FliReader.FromStream(s));
}
