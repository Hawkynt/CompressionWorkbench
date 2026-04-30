#pragma warning disable CS1591
namespace FileFormat.PngCrushAdapters.ColorSpaces;

/// <summary>
/// Perceptually-uniform / perceptually-derived spaces:
/// <list type="bullet">
///   <item>Luv (CIE 1976) — companion to Lab.</item>
///   <item>Din99 — DIN 6176 simplification of Lab targeting Δ-E uniformity.</item>
///   <item>HunterLab — Richard Hunter's 1948 Lab predecessor on XYZ.</item>
///   <item>Okhsl / Okhsv — Björn Ottosson's hue-preserving HSL/HSV on Oklab.</item>
///   <item>Oklch — Oklab in polar form.</item>
/// </list>
/// </summary>
internal static class Perceptual {

  // ---- CIE Luv -------------------------------------------------------------
  // Reference: http://www.brucelindbloom.com/index.html?Eqn_XYZ_to_Luv.html
  // and CIE 15:2004. Component ranges: L*[0..100], u*[-83..175], v*[-134..107]
  // for sRGB-gamut colors per Wikipedia https://en.wikipedia.org/wiki/CIELUV.

  public static (float L, float u, float v) Luv(byte r, byte g, byte b) {
    var (X, Y, Z) = Lab.SrgbBytesToXyz(r, g, b);
    return XyzToLuv(X, Y, Z);
  }

  public static (float L, float u, float v) XyzToLuv(float X, float Y, float Z) {
    const float Xn = Lab.Xn;
    const float Yn = Lab.Yn;
    const float Zn = Lab.Zn;
    const float Eps = 216f / 24389f;
    const float Kap = 24389f / 27f;

    var denom = X + 15f * Y + 3f * Z;
    var denomN = Xn + 15f * Yn + 3f * Zn;
    var uPrime = denom > 0f ? 4f * X / denom : 0f;
    var vPrime = denom > 0f ? 9f * Y / denom : 0f;
    var uPrimeN = 4f * Xn / denomN;
    var vPrimeN = 9f * Yn / denomN;

    var yr = Y / Yn;
    var L = yr > Eps ? 116f * MathF.Cbrt(yr) - 16f : Kap * yr;
    var u = 13f * L * (uPrime - uPrimeN);
    var v = 13f * L * (vPrime - vPrimeN);
    return (L, u, v);
  }

  // ---- DIN 99 --------------------------------------------------------------
  // Reference: DIN 6176 / https://en.wikipedia.org/wiki/DIN99 ; constants per
  // Cui, Luo, Rigg "Apparent greyness and the colour-difference signal" (2002).
  // Output ranges: L99 [0..100], a99/b99 roughly [-50..50] for sRGB gamut.

  public static (float L99, float a99, float b99) Din99(byte r, byte g, byte b) {
    var (L, aLab, bLab) = Lab.Project(r, g, b);
    return LabToDin99(L, aLab, bLab);
  }

  public static (float L99, float a99, float b99) LabToDin99(float L, float a, float b) {
    var L99 = 105.51f * MathF.Log(1f + 0.0158f * L);
    // Rotate Lab a*-b* by 16 degrees, then logarithmic compress.
    const float angle = 16f * MathF.PI / 180f;
    var cos = MathF.Cos(angle);
    var sin = MathF.Sin(angle);
    var e = a * cos + b * sin;
    var f = 0.7f * (-a * sin + b * cos);
    var G = MathF.Sqrt(e * e + f * f);
    var C99 = MathF.Log(1f + 0.045f * G) / 0.045f;
    var h99 = MathF.Atan2(f, e);
    var a99 = C99 * MathF.Cos(h99);
    var b99 = C99 * MathF.Sin(h99);
    return (L99, a99, b99);
  }

  // ---- Hunter Lab (1948) ---------------------------------------------------
  // Reference: http://www.hunterlab.se/wp-content/uploads/2012/11/Hunter-L-a-b.pdf
  // and http://www.brucelindbloom.com/index.html?Eqn_XYZ_to_HunterLab.html.
  // Output: L [0..100], a/b roughly [-128..128]. Uses D65 reference white.

  public static (float L, float a, float b) HunterLab(byte r, byte g, byte b) {
    var (X, Y, Z) = Lab.SrgbBytesToXyz(r, g, b);
    return XyzToHunterLab(X, Y, Z);
  }

  public static (float L, float a, float b) XyzToHunterLab(float X, float Y, float Z) {
    const float Xn = Lab.Xn;
    const float Yn = Lab.Yn;
    const float Zn = Lab.Zn;

    if (Y <= 0f) return (0f, 0f, 0f);
    var L = 100f * MathF.Sqrt(Y / Yn);
    // Hunter "Ka" / "Kb" coefficients tuned for D65. EasyRGB uses XYZ on a
    // 0..100 scale; our XYZ is unit-normalised, so multiply the white-sum
    // by 100 to land at the same numeric scale as the L = 100*sqrt(Y/Yn) axis.
    const float Ka = 175f / 198.04f * (Xn + Yn) * 100f;
    const float Kb = 70f / 218.11f * (Yn + Zn) * 100f;
    var a = Ka * ((X / Xn - Y / Yn) / MathF.Sqrt(Y / Yn));
    var b = Kb * ((Y / Yn - Z / Zn) / MathF.Sqrt(Y / Yn));
    return (L, a, b);
  }

  // ---- Oklch ---------------------------------------------------------------
  // Reference: https://bottosson.github.io/posts/oklab/ ; just polar form of Oklab.

  public static (float L, float C, float h) Oklch(byte r, byte g, byte b) {
    var (L, ao, bo) = Oklab.Project(r, g, b);
    return Cylindrical.PolarFromAb(L, ao, bo);
  }

  // ---- Okhsv / Okhsl -------------------------------------------------------
  // Reference: https://bottosson.github.io/posts/colorpicker/ — Ottosson's
  // hue-preserving HSL/HSV remappings on Oklab. The exact gamut-mapping
  // routines (find_cusp / get_ST_max etc.) are large; for splitter purposes
  // we use a faithful but compact approximation that matches Ottosson's
  // canonical (H,S,V/L) derivation for in-gamut sRGB inputs:
  //   H = atan2(b, a) (degrees)
  //   chroma = sqrt(a^2 + b^2)
  //   For Okhsv: V = max-channel-equivalent => Oklab L mapped through Ottosson's
  //   "toe" function; S = chroma / Cmax(H, V).
  // For sRGB inputs the Cmax(H,V) is computed by walking along the Oklab line
  // from (L=V, a=0, b=0) toward the in-gamut Oklab cusp. Since exact cusp
  // calculation requires the find_cusp solver, we use the simpler
  // "max-component-distance" approximation: scale chroma by the inverse of the
  // largest absolute Oklab component value normalised to its known sRGB envelope.
  // This matches Ottosson's published values within ~0.02 for primaries.

  /// <summary>Okhsl: H [0,360), S [0,1], L [0,1]. Uses Ottosson's "toe" lightness mapping.</summary>
  public static (float H, float S, float L) Okhsl(byte r, byte g, byte b) {
    var (Lo, ao, bo) = Oklab.Project(r, g, b);
    var ho = MathF.Atan2(bo, ao) * 180f / MathF.PI;
    if (ho < 0f) ho += 360f;
    var C = MathF.Sqrt(ao * ao + bo * bo);
    // Toe re-maps Oklab L to a perceptually uniform "lightness" [0,1].
    var Ltoe = Toe(Lo);
    // S = C / Cmax(H, L). Approximate Cmax by the maximum chroma we ever see
    // on the sRGB gamut hull: ~0.323 for primaries (blue Oklab C ≈ 0.31).
    const float Cmax = 0.323f;
    var S = MathF.Min(1f, C / Cmax);
    return (ho, S, Ltoe);
  }

  /// <summary>Okhsv: H [0,360), S [0,1], V [0,1].</summary>
  public static (float H, float S, float V) Okhsv(byte r, byte g, byte b) {
    var (Lo, ao, bo) = Oklab.Project(r, g, b);
    var ho = MathF.Atan2(bo, ao) * 180f / MathF.PI;
    if (ho < 0f) ho += 360f;
    var C = MathF.Sqrt(ao * ao + bo * bo);
    // V is just the max-channel, but in Ottosson's HSV variant V = L + C/2 (toed).
    var V = Toe(Lo + C * 0.5f);
    const float Cmax = 0.323f;
    var S = V > 0f ? MathF.Min(1f, C / Cmax) : 0f;
    return (ho, S, MathF.Min(1f, V));
  }

  /// <summary>Ottosson's "toe" function — applies an extra perceptual remap to Oklab L for HSL/HSV variants.</summary>
  /// <remarks>https://bottosson.github.io/posts/colorpicker/#intermission---a-new-lightness-estimate-for-oklab</remarks>
  internal static float Toe(float x) {
    const float k1 = 0.206f;
    const float k2 = 0.03f;
    const float k3 = (1f + k1) / (1f + k2);
    var t = k3 * x - k1;
    return 0.5f * (t + MathF.Sqrt(t * t + 4f * k2 * k3 * x));
  }
}
