#pragma warning disable CS1591
using FileFormat.Core;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Cheap-metadata + on-demand-pixels two-tier source for the
/// <see cref="MultiImageArchiveHelper"/> archive view. Implementations must
/// guarantee that <see cref="GetMetadata"/> reads only header-shaped bytes
/// (kilobytes, not megabytes) so that <c>List()</c> on a multi-megabyte
/// container completes in &lt;100 ms regardless of pixel data size.
/// </summary>
/// <remarks>
/// This contract was introduced to fix the JPEG <c>List()</c> freeze: prior
/// to its existence the helper called the underlying decoder eagerly just to
/// know <c>(W, H, bpp)</c>, which on a 10 MB JPEG meant a full DCT/IDCT pass
/// (~seconds) before the first archive entry could be enumerated.
/// </remarks>
public interface IFrameSource {

  /// <summary>Number of frames the source can describe. Cheap.</summary>
  int FrameCount { get; }

  /// <summary>
  /// Cheap (~kilobytes of I/O) metadata for the given frame. Must NOT decode
  /// pixels. If metadata is genuinely unavailable from headers alone the
  /// implementation should return a permissive fallback
  /// <c>(0, 0, 24, false)</c> so <c>List()</c> still emits stable entry names.
  /// </summary>
  FrameMetadata GetMetadata(int frameIndex);

  /// <summary>
  /// Expensive — performs the full pixel decode for the given frame. Only
  /// called from <c>Extract()</c>, never from <c>List()</c>.
  /// </summary>
  RawImage GetFrame(int frameIndex);
}

/// <summary>
/// Header-shaped frame description. <see cref="HasAlpha"/> drives whether the
/// helper announces an <c>Alpha.png</c> entry next to the composite frame.
/// JPEG never has alpha (CMYK is 4 components but no alpha plane), so
/// <see cref="JpegFrameSource"/> always returns <c>HasAlpha=false</c>.
/// </summary>
public readonly record struct FrameMetadata(int Width, int Height, int BitsPerPixel, bool HasAlpha);
