namespace Compression.Core.Image;

/// <summary>
/// Decomposes an RGB/RGBA raster image into per-plane 8-bit grayscale payloads
/// encoded as binary PGM (portable graymap). Downstream tooling can load a plane
/// in any image viewer without a custom decoder, which is the whole point of
/// surfacing images as archives — "R.pgm" opens in GIMP/Photoshop as a grayscale
/// image even though the source format was JPEG or PNG.
/// </summary>
public static class PlaneSplitter {
  /// <summary>Pixel layouts recognised by <see cref="Split"/>.</summary>
  public enum PixelLayout {
    /// <summary>3 bytes per pixel: R, G, B.</summary>
    Rgb,
    /// <summary>4 bytes per pixel: R, G, B, A.</summary>
    Rgba,
    /// <summary>1 byte per pixel: luminance.</summary>
    Grayscale,
  }

  /// <summary>
  /// Splits a packed 8-bit-per-channel raster into per-plane PGMs.
  /// Channel order in <paramref name="pixels"/> must match <paramref name="layout"/>:
  /// Rgb → RGBRGBRGB…, Rgba → RGBARGBARGBA…, Grayscale → LLLL….
  /// </summary>
  public static IReadOnlyList<(string Name, byte[] Pgm)> Split(
      byte[] pixels, int width, int height, PixelLayout layout) {
    if (width <= 0 || height <= 0)
      throw new ArgumentException("Width and height must be positive.");
    var pixelCount = (long)width * height;

    switch (layout) {
      case PixelLayout.Grayscale:
        if (pixels.Length < pixelCount)
          throw new ArgumentException("pixels buffer too short for grayscale layout.");
        return [("L.pgm", ToPgm(pixels, width, height, 0, 1, pixelCount))];

      case PixelLayout.Rgb:
        if (pixels.Length < pixelCount * 3)
          throw new ArgumentException("pixels buffer too short for RGB layout.");
        return [
          ("R.pgm", ToPgm(pixels, width, height, 0, 3, pixelCount)),
          ("G.pgm", ToPgm(pixels, width, height, 1, 3, pixelCount)),
          ("B.pgm", ToPgm(pixels, width, height, 2, 3, pixelCount)),
        ];

      case PixelLayout.Rgba:
        if (pixels.Length < pixelCount * 4)
          throw new ArgumentException("pixels buffer too short for RGBA layout.");
        return [
          ("R.pgm", ToPgm(pixels, width, height, 0, 4, pixelCount)),
          ("G.pgm", ToPgm(pixels, width, height, 1, 4, pixelCount)),
          ("B.pgm", ToPgm(pixels, width, height, 2, 4, pixelCount)),
          ("A.pgm", ToPgm(pixels, width, height, 3, 4, pixelCount)),
        ];

      default:
        throw new ArgumentOutOfRangeException(nameof(layout));
    }
  }

  private static byte[] ToPgm(byte[] pixels, int width, int height, int channelOffset, int stride, long pixelCount) {
    var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
    var plane = new byte[pixelCount];
    for (long i = 0; i < pixelCount; ++i)
      plane[i] = pixels[i * stride + channelOffset];
    var result = new byte[header.Length + plane.Length];
    header.CopyTo(result, 0);
    plane.CopyTo(result, header.Length);
    return result;
  }
}
