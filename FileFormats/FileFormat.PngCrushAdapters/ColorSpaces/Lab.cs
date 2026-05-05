#pragma warning disable CS1591
namespace FileFormat.PngCrushAdapters.ColorSpaces;

/// <summary>
/// CIE Lab and Oklab projectors. Inputs are gamma-encoded sRGB bytes; the
/// projectors apply the sRGB transfer-function decode before the matrix step.
/// </summary>
/// <remarks>
/// References:
/// <list type="bullet">
///   <item>http://www.brucelindbloom.com/index.html?Eqn_RGB_XYZ_Matrix.html (sRGB D65 matrix)</item>
///   <item>http://www.brucelindbloom.com/index.html?Eqn_XYZ_to_Lab.html (Lab D65)</item>
///   <item>https://bottosson.github.io/posts/oklab/ (Oklab original definition, Björn Ottosson 2020)</item>
/// </list>
/// </remarks>
internal static class Lab {

  // sRGB -> XYZ (D65) matrix per Bruce Lindbloom / IEC 61966-2-1.
  internal const float MX_R = 0.4124564f, MX_G = 0.3575761f, MX_B = 0.1804375f;
  internal const float MY_R = 0.2126729f, MY_G = 0.7151522f, MY_B = 0.0721750f;
  internal const float MZ_R = 0.0193339f, MZ_G = 0.1191920f, MZ_B = 0.9503041f;

  // CIE D65 reference white (observer 2 deg) per CIE 15:2004.
  internal const float Xn = 0.95047f;
  internal const float Yn = 1.00000f;
  internal const float Zn = 1.08883f;

  // Lab non-linearity constants: epsilon = (6/29)^3, kappa = (29/3)^3.
  private const float Eps = 216f / 24389f;
  private const float Kap = 24389f / 27f;

  /// <summary>Linear-RGB (D65) -&gt; XYZ. Pass linear-light components.</summary>
  public static (float X, float Y, float Z) LinearRgbToXyz(float r, float g, float b) => (
    MX_R * r + MX_G * g + MX_B * b,
    MY_R * r + MY_G * g + MY_B * b,
    MZ_R * r + MZ_G * g + MZ_B * b
  );

  /// <summary>Convenience: gamma-encoded sRGB bytes -&gt; XYZ (D65).</summary>
  public static (float X, float Y, float Z) SrgbBytesToXyz(byte r, byte g, byte b) {
    var lr = SrgbGamma.ToLinear(r);
    var lg = SrgbGamma.ToLinear(g);
    var lb = SrgbGamma.ToLinear(b);
    return LinearRgbToXyz(lr, lg, lb);
  }

  /// <summary>XYZ (D65) -&gt; Lab.</summary>
  public static (float L, float a, float b) XyzToLab(float X, float Y, float Z) {
    var fx = F(X / Xn);
    var fy = F(Y / Yn);
    var fz = F(Z / Zn);
    var L = 116f * fy - 16f;
    var a = 500f * (fx - fy);
    var b = 200f * (fy - fz);
    return (L, a, b);
  }

  /// <summary>sRGB bytes -&gt; CIE Lab (D65).</summary>
  public static (float L, float a, float b) Project(byte r, byte g, byte b) {
    var (X, Y, Z) = SrgbBytesToXyz(r, g, b);
    return XyzToLab(X, Y, Z);
  }

  private static float F(float t) => t > Eps ? MathF.Cbrt(t) : (Kap * t + 16f) / 116f;
}

/// <summary>Oklab (Björn Ottosson, 2020).</summary>
internal static class Oklab {

  /// <summary>Linear-RGB -&gt; Oklab.</summary>
  /// <remarks>Reference: https://bottosson.github.io/posts/oklab/</remarks>
  public static (float L, float a, float b) FromLinearRgb(float r, float g, float bl) {
    // Linear RGB -> LMS (Ottosson's "M1" matrix).
    var l = 0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * bl;
    var m = 0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * bl;
    var s = 0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * bl;
    var lp = MathF.Cbrt(l);
    var mp = MathF.Cbrt(m);
    var sp = MathF.Cbrt(s);
    // LMS-cbrt -> Oklab ("M2").
    var L = 0.2104542553f * lp + 0.7936177850f * mp - 0.0040720468f * sp;
    var a = 1.9779984951f * lp - 2.4285922050f * mp + 0.4505937099f * sp;
    var b = 0.0259040371f * lp + 0.7827717662f * mp - 0.8086757660f * sp;
    return (L, a, b);
  }

  /// <summary>sRGB bytes -&gt; Oklab.</summary>
  public static (float L, float a, float b) Project(byte r, byte g, byte b) {
    var lr = SrgbGamma.ToLinear(r);
    var lg = SrgbGamma.ToLinear(g);
    var lb = SrgbGamma.ToLinear(b);
    return FromLinearRgb(lr, lg, lb);
  }
}
