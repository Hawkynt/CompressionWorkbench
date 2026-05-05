#pragma warning disable CS1591
namespace FileFormat.PngCrushAdapters.ColorSpaces;

/// <summary>
/// Component-quantisation helpers. Each colorspace component has a different
/// natural range; these helpers map those ranges to 0..255 so the splitter
/// can emit standard 8-bit grayscale PNGs while the float-domain math stays
/// honest. The numeric ranges baked in below are documented next to each
/// colorspace's projector with a citation.
/// </summary>
internal static class Quant {

  /// <summary>Maps [0,1] to [0,255] (saturating).</summary>
  public static byte Unit(float v) {
    if (v <= 0f) return 0;
    if (v >= 1f) return 255;
    return (byte)(v * 255f + 0.5f);
  }

  /// <summary>Maps [-range,+range] to [0,255] with 128 as the zero-point (saturating).</summary>
  public static byte Signed(float v, float range) {
    var t = (v / range + 1f) * 0.5f;
    if (t <= 0f) return 0;
    if (t >= 1f) return 255;
    return (byte)(t * 255f + 0.5f);
  }

  /// <summary>Maps [min,max] to [0,255] (saturating).</summary>
  public static byte Range(float v, float min, float max) {
    if (max <= min) return 0;
    var t = (v - min) / (max - min);
    if (t <= 0f) return 0;
    if (t >= 1f) return 255;
    return (byte)(t * 255f + 0.5f);
  }

  /// <summary>Maps a hue in degrees [0,360) to [0,255]. 360-degree input is wrapped to 0.</summary>
  public static byte Hue(float degrees) {
    var h = degrees;
    while (h < 0f) h += 360f;
    while (h >= 360f) h -= 360f;
    return (byte)(h / 360f * 255f + 0.5f);
  }
}
