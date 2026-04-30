#pragma warning disable CS1591
namespace FileFormat.PngCrushAdapters.ColorSpaces;

/// <summary>
/// Cylindrical reformulations of common spaces:
/// HSL/HSV/HSI/HWB are simple RGB-derived;
/// LCH and LChUv are Lab/Luv expressed in polar form.
/// </summary>
/// <remarks>
/// HSL/HSV/HSI/HWB references: https://en.wikipedia.org/wiki/HSL_and_HSV ,
/// https://en.wikipedia.org/wiki/HWB_color_model.
/// LCH / LChUv reference: http://www.brucelindbloom.com/index.html?Eqn_Lab_to_LCH.html .
/// </remarks>
internal static class Cylindrical {

  /// <summary>HSL: hue [0,360), saturation [0,1], lightness [0,1].</summary>
  public static (float H, float S, float L) Hsl(byte rb, byte gb, byte bb) {
    var r = rb / 255f;
    var g = gb / 255f;
    var b = bb / 255f;
    var max = MathF.Max(r, MathF.Max(g, b));
    var min = MathF.Min(r, MathF.Min(g, b));
    var l = (max + min) * 0.5f;
    if (max == min) return (0f, 0f, l);
    var d = max - min;
    var s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
    float h;
    if (max == r) h = ((g - b) / d) + (g < b ? 6f : 0f);
    else if (max == g) h = ((b - r) / d) + 2f;
    else h = ((r - g) / d) + 4f;
    return (h * 60f, s, l);
  }

  /// <summary>HSV: hue [0,360), saturation [0,1], value [0,1].</summary>
  public static (float H, float S, float V) Hsv(byte rb, byte gb, byte bb) {
    var r = rb / 255f;
    var g = gb / 255f;
    var b = bb / 255f;
    var max = MathF.Max(r, MathF.Max(g, b));
    var min = MathF.Min(r, MathF.Min(g, b));
    var v = max;
    if (max <= 0f) return (0f, 0f, 0f);
    var d = max - min;
    var s = d / max;
    if (d <= 0f) return (0f, 0f, v);
    float h;
    if (max == r) h = ((g - b) / d) + (g < b ? 6f : 0f);
    else if (max == g) h = ((b - r) / d) + 2f;
    else h = ((r - g) / d) + 4f;
    return (h * 60f, s, v);
  }

  /// <summary>HSI: hue [0,360), saturation [0,1], intensity [0,1] = (R+G+B)/3.</summary>
  /// <remarks>Reference: https://en.wikipedia.org/wiki/HSL_and_HSV#Hue_and_chroma (HSI section).</remarks>
  public static (float H, float S, float I) Hsi(byte rb, byte gb, byte bb) {
    var r = rb / 255f;
    var g = gb / 255f;
    var b = bb / 255f;
    var i = (r + g + b) / 3f;
    if (i <= 0f) return (0f, 0f, 0f);
    var min = MathF.Min(r, MathF.Min(g, b));
    var s = 1f - min / i;
    // Hexagonal-style hue (matches HSL/HSV convention used elsewhere here).
    var max = MathF.Max(r, MathF.Max(g, b));
    var d = max - min;
    if (d <= 0f) return (0f, s < 0f ? 0f : s, i);
    float h;
    if (max == r) h = ((g - b) / d) + (g < b ? 6f : 0f);
    else if (max == g) h = ((b - r) / d) + 2f;
    else h = ((r - g) / d) + 4f;
    return (h * 60f, s, i);
  }

  /// <summary>HWB: hue [0,360), whiteness [0,1], blackness [0,1].</summary>
  /// <remarks>Reference: https://en.wikipedia.org/wiki/HWB_color_model — W = min(R,G,B), B = 1 - max(R,G,B).</remarks>
  public static (float H, float W, float B) Hwb(byte rb, byte gb, byte bb) {
    var r = rb / 255f;
    var g = gb / 255f;
    var b = bb / 255f;
    var max = MathF.Max(r, MathF.Max(g, b));
    var min = MathF.Min(r, MathF.Min(g, b));
    float h;
    if (max == min) {
      h = 0f;
    } else {
      var d = max - min;
      if (max == r) h = ((g - b) / d) + (g < b ? 6f : 0f);
      else if (max == g) h = ((b - r) / d) + 2f;
      else h = ((r - g) / d) + 4f;
      h *= 60f;
    }
    return (h, min, 1f - max);
  }

  /// <summary>CIE LCH(ab): Lab in polar form. L [0,100], C [0,~150], h [0,360).</summary>
  public static (float L, float C, float h) Lch(byte r, byte g, byte b) {
    var (L, a, bb) = Lab.Project(r, g, b);
    return PolarFromAb(L, a, bb);
  }

  /// <summary>CIE LCH(uv): Luv in polar form. L [0,100], C [0,~180], h [0,360).</summary>
  public static (float L, float C, float h) LchUv(byte r, byte g, byte b) {
    var (L, u, v) = Perceptual.Luv(r, g, b);
    return PolarFromAb(L, u, v);
  }

  internal static (float L, float C, float h) PolarFromAb(float L, float a, float b) {
    var C = MathF.Sqrt(a * a + b * b);
    var h = MathF.Atan2(b, a) * 180f / MathF.PI;
    if (h < 0f) h += 360f;
    return (L, C, h);
  }
}
