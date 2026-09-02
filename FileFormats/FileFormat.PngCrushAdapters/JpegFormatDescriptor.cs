#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Default routing for plain JPEG files (.jpg/.jpeg/.jpe/.jfif). Surfaces the
/// JPEG as a multi-image archive view through <see cref="MultiImageArchiveHelper"/>:
/// the FULL frame plus the colorspace pseudo-tree (per-component grayscale PNGs
/// across ~29 colorspaces) — same shape as APNG/MPO/etc. so the UI tree can
/// browse a single JPEG's color planes uniformly.
/// <para>
/// <b>Lazy enumeration</b>: this descriptor uses <see cref="JpegFrameSource"/>
/// (the <see cref="IFrameSource"/> path) so <c>List()</c> reads only the SOF
/// marker (~kilobytes), not the full pixel stream. A 10 MB JPEG lists in
/// &lt;100 ms; libjpeg's full DCT/IDCT pipeline only runs when <c>Extract()</c>
/// actually needs pixels.
/// </para>
/// <para>
/// The legacy <c>JpegArchive</c> descriptor (APP-marker / EXIF thumbnail
/// extraction) is still reachable explicitly via <c>cwb list --format JpegArchive</c>;
/// only the magic + extensions for default routing are owned by this descriptor.
/// </para>
/// </summary>
public sealed class JpegFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Jpeg";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "JPEG image";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Image;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".jpg";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".jpg", ".jpeg", ".jpe", ".jfif"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xFF, 0xD8, 0xFF], Confidence: 0.95),
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
public string Description => "JPEG (single image surfaced as colorspace pseudo-archive).";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "image", OpenSource);

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "image", OpenSource);

  private static IFrameSource OpenSource(Stream s) => new JpegFrameSource(s);
}
