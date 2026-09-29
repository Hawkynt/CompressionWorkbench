namespace Compression.NativeUI.Controls;

/// <summary>Shrinks a picture into the square a thumbnail view draws.</summary>
internal static class Thumbnail {
  /// <summary>The edge of a thumbnail, in pixels.</summary>
  public const int Size = 96;

  /// <summary>
  /// Fits a <paramref name="width"/> × <paramref name="height"/> ARGB picture into a
  /// <paramref name="size"/> × <paramref name="size"/> square: aspect ratio kept, centred, the margin
  /// left transparent. A picture already smaller than the square keeps its pixels one for one rather
  /// than being blown up into blocks. Each target pixel is the average of the source pixels it
  /// covers, so fine detail shrinks into a tone instead of flickering in and out.
  /// </summary>
  public static int[] Fit(ReadOnlySpan<int> argb, int width, int height, int size = Size) {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
    var result = new int[size * size];
    if (width <= 0 || height <= 0 || argb.Length < width * height) return result;

    var scale = Math.Min(1.0, Math.Min((double)size / width, (double)size / height));
    var w = Math.Clamp((int)Math.Round(width * scale), 1, size);
    var h = Math.Clamp((int)Math.Round(height * scale), 1, size);
    var left = (size - w) / 2;
    var top = (size - h) / 2;

    for (var y = 0; y < h; ++y) {
      var sy0 = (int)((long)y * height / h);
      var sy1 = Math.Max(sy0 + 1, (int)((long)(y + 1) * height / h));
      for (var x = 0; x < w; ++x) {
        var sx0 = (int)((long)x * width / w);
        var sx1 = Math.Max(sx0 + 1, (int)((long)(x + 1) * width / w));

        long a = 0, r = 0, g = 0, b = 0;
        for (var sy = sy0; sy < sy1; ++sy)
          for (var sx = sx0; sx < sx1; ++sx) {
            var p = (uint)argb[sy * width + sx];
            a += p >> 24;
            r += (p >> 16) & 0xFF;
            g += (p >> 8) & 0xFF;
            b += p & 0xFF;
          }

        var n = (sy1 - sy0) * (sx1 - sx0);
        result[(top + y) * size + left + x] = (int)((uint)(a / n) << 24 | (uint)(r / n) << 16 | (uint)(g / n) << 8 | (uint)(b / n));
      }
    }

    return result;
  }
}
