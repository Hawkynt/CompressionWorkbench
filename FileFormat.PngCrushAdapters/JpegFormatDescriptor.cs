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
  public string Id => "Jpeg";
  public string DisplayName => "JPEG image";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".jpg";
  public IReadOnlyList<string> Extensions => [".jpg", ".jpeg", ".jpe", ".jfif"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xFF, 0xD8, 0xFF], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "JPEG (single image surfaced as colorspace pseudo-archive).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "image", OpenSource);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "image", OpenSource);

  private static IFrameSource OpenSource(Stream s) => new JpegFrameSource(s);
}
