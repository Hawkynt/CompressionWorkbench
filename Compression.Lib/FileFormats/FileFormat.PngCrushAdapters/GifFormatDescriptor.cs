#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Gif;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Exposes a multi-frame GIF87a/GIF89a as an archive of per-frame folders, each
/// containing the composite frame plus the full colorspace tree
/// (<see cref="ColorSpaceSplitter"/>). Frames are decoded to RGBA32 with proper
/// disposal/composition semantics so animated GIFs surface meaningful per-step
/// frames, not raw LZW slices.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.w3.org/Graphics/GIF/spec-gif89a.txt</c> — GIF89a specification (CompuServe, hosted by W3C)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/GIF</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
/// <remarks>
/// This descriptor lives in <c>FileFormat.PngCrushAdapters</c> because the
/// colorspace splitter and PNG encoder come from the sibling PngCrushCS repo. The
/// raw <see cref="GifPixelDecoder"/> stays in <c>FileFormat.Gif</c> as a
/// dependency-free utility (it produces RGBA32 byte buffers, no PngCrushCS types).
/// </remarks>
public sealed class GifFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IFileInternalLayoutMap {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Gif";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "GIF (multi-frame)";
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
  public string DefaultExtension => ".gif";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".gif"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("GIF87a"u8.ToArray(), Confidence: 0.95),
    new("GIF89a"u8.ToArray(), Confidence: 0.95),
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
  public string Description => "Animated GIF; each frame is decoded to RGBA32 with disposal/blend applied, then exposed with the full colorspace tree.";

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

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file) => GifLayoutMap.Enumerate(file);

  /// <summary>Decodes <paramref name="s"/> to a list of <see cref="RawImage"/> RGBA32 frames.</summary>
  /// <remarks>
  /// The <see cref="GifPixelDecoder"/> returns canvas-sized snapshots after disposal/blend,
  /// matching what an animated GIF viewer displays at each step.
  /// </remarks>
  private static IReadOnlyList<RawImage> ReadAll(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    var frames = new GifPixelDecoder().Decode(ms.ToArray());
    var result = new RawImage[frames.Count];
    for (var i = 0; i < frames.Count; i++) {
      result[i] = new RawImage {
        Width = frames[i].Width,
        Height = frames[i].Height,
        Format = PixelFormat.Rgba32,
        PixelData = frames[i].Rgba32,
      };
    }
    return result;
  }
}
