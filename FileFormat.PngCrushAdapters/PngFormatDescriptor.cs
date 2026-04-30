#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Default routing for plain PNG files. Surfaces a static PNG as a multi-image
/// archive view through <see cref="MultiImageArchiveHelper"/>: the FULL frame
/// plus the per-component grayscale colorspace tree (RGB/YCbCr/HSL/Lab/...) —
/// same shape as JPEG/APNG/MPO so the UI tree can browse a single PNG's color
/// planes uniformly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lazy enumeration</b>: this descriptor uses <see cref="PngFrameSource"/>
/// (the <see cref="IFrameSource"/> path) so <c>List()</c> reads only the IHDR
/// chunk (~29 bytes). Pixel decode runs only when <c>Extract()</c> actually
/// needs them — a 100 MB PNG lists instantly.
/// </para>
/// <para>
/// APNG owns the <c>.apng</c> extension and has empty magic specifically so
/// static PNGs don't get hijacked into the animated path. This descriptor
/// claims the PNG magic + <c>.png</c> extension as the static fallback.
/// </para>
/// </remarks>
public sealed class PngFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Png";
  public string DisplayName => "PNG image";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".png";
  public IReadOnlyList<string> Extensions => [".png"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], Confidence: 0.99),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PNG (single image surfaced as colorspace pseudo-archive).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "image", OpenSource);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "image", OpenSource);

  private static IFrameSource OpenSource(Stream s) => new PngFrameSource(s);
}
