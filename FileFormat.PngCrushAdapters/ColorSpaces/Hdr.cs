#pragma warning disable CS1591
namespace FileFormat.PngCrushAdapters.ColorSpaces;

/// <summary>
/// HDR / canonical CIE projectors:
/// <list type="bullet">
///   <item>XYZ (CIE 1931 tristimulus, the canonical reference).</item>
///   <item>xyY (chromaticity + luminance).</item>
///   <item>ICtCp (BT.2100 IPT-like, HDR/UHDTV).</item>
///   <item>JzAzBz (Hellwig-Fairchild 2017 perceptual).</item>
///   <item>JzCzhz (JzAzBz cylindrical).</item>
/// </list>
/// </summary>
internal static class Hdr {

  /// <summary>sRGB bytes -&gt; CIE XYZ (D65, Y-normalised so white = 1.0).</summary>
  /// <remarks>Reference: http://www.brucelindbloom.com/index.html?Eqn_RGB_XYZ_Matrix.html (sRGB D65).</remarks>
  public static (float X, float Y, float Z) Xyz(byte r, byte g, byte b)
    => Lab.SrgbBytesToXyz(r, g, b);

  /// <summary>sRGB bytes -&gt; xyY chromaticity + luminance.</summary>
  /// <remarks>
  /// x = X/(X+Y+Z), y = Y/(X+Y+Z); Y unchanged. Reference:
  /// http://www.brucelindbloom.com/index.html?Eqn_XYZ_to_xyY.html .
  /// Component ranges: x,y∈[0,~0.74], Y∈[0,1].
  /// </remarks>
  public static (float x, float y, float Y) XyY(byte r, byte g, byte b) {
    var (X, Y, Z) = Lab.SrgbBytesToXyz(r, g, b);
    var sum = X + Y + Z;
    if (sum <= 0f) {
      // Convention: when stimulus has no power, return D65 chromaticity.
      return (0.31271f, 0.32902f, 0f);
    }
    return (X / sum, Y / sum, Y);
  }

  // ---- ICtCp (BT.2100, non-PQ "Linear" form) -------------------------------
  // Reference: ITU-R BT.2100 / Dolby ICtCp white paper
  //   https://www.dolby.com/us/en/technologies/dolby-vision/ICtCp-white-paper.pdf
  //
  // Pipeline: linear-sRGB -> CAT to BT.2020 -> LMS via BT.2100 matrix -> PQ
  // (Perceptual Quantizer) encoding -> ICtCp matrix.
  // For low-luminance SDR sources (sRGB) the PQ step compresses heavily; we
  // scale the 0..1 sRGB linear range to ~0.01..0.1 of the 10000-nit reference
  // before PQ so the SDR dynamic range maps to a useful slice of the PQ curve.
  // Honest scope note: SDR-content ICtCp values are reference-source-uncertain
  // (the spec targets HDR). We pick the conventional 100-nit-peak SDR mapping
  // (Y * 0.01) so 100% sRGB white -> ~0.51 PQ-encoded -> Iz ~0.51.

  public static (float I, float Ct, float Cp) ICtCp(byte r, byte g, byte b) {
    var lr = SrgbGamma.ToLinear(r);
    var lg = SrgbGamma.ToLinear(g);
    var lb = SrgbGamma.ToLinear(b);

    // sRGB-linear (D65) -> BT.2020-linear (CAT through XYZ; matrix from BT.2407).
    var R2020 = 0.62740390f * lr + 0.32928304f * lg + 0.04331307f * lb;
    var G2020 = 0.06909729f * lr + 0.91954040f * lg + 0.01136231f * lb;
    var B2020 = 0.01639144f * lr + 0.08801331f * lg + 0.89559525f * lb;

    // BT.2020 -> LMS (BT.2100 ICtCp Annex E).
    var L = (1688f * R2020 + 2146f * G2020 + 262f * B2020) / 4096f;
    var M = (683f * R2020 + 2951f * G2020 + 462f * B2020) / 4096f;
    var S = (99f * R2020 + 309f * G2020 + 3688f * B2020) / 4096f;

    // PQ-encode each. Treat sRGB 1.0 as 100 nits => 0.01 of 10000-nit range.
    var Lp = Pq(L * 0.01f);
    var Mp = Pq(M * 0.01f);
    var Sp = Pq(S * 0.01f);

    // L'M'S' -> ICtCp.
    var I = 0.5f * Lp + 0.5f * Mp;
    var Ct = (6610f * Lp - 13613f * Mp + 7003f * Sp) / 4096f;
    var Cp = (17933f * Lp - 17390f * Mp - 543f * Sp) / 4096f;
    return (I, Ct, Cp);
  }

  /// <summary>SMPTE ST 2084 PQ EOTF^-1 (encode linear-light to PQ-coded value).</summary>
  /// <remarks>Input is normalised: 1.0 = 10000 nits. Output is [0,1].</remarks>
  internal static float Pq(float linear) {
    if (linear <= 0f) return 0f;
    const float m1 = 2610f / 16384f;
    const float m2 = 2523f / 4096f * 128f;
    const float c1 = 3424f / 4096f;
    const float c2 = 2413f / 4096f * 32f;
    const float c3 = 2392f / 4096f * 32f;
    var Yp = MathF.Pow(linear, m1);
    var num = c1 + c2 * Yp;
    var den = 1f + c3 * Yp;
    return MathF.Pow(num / den, m2);
  }

  // ---- JzAzBz (Safdar et al. 2017) ----------------------------------------
  // Reference: M. Safdar, G. Cui, Y. J. Kim, M. R. Luo, "Perceptually uniform
  // color space for image signals including high dynamic range and wide gamut",
  // Optics Express 25(13), 15131-15151 (2017).
  // https://www.osapublishing.org/oe/abstract.cfm?uri=oe-25-13-15131
  // Component ranges: Jz∈[0,~0.17], az/bz∈[-0.05,+0.05] for SDR sRGB-gamut inputs.

  public static (float Jz, float az, float bz) JzAzBz(byte r, byte g, byte b) {
    var (X, Y, Z) = Lab.SrgbBytesToXyz(r, g, b);

    // Step 1: X', Y' premultiplications.
    const float bb = 1.15f, gg = 0.66f;
    var Xp = bb * X - (bb - 1f) * Z;
    var Yp = gg * Y - (gg - 1f) * X;
    var Zp = Z;

    // Step 2: XYZ' -> LMS (Safdar matrix).
    var L = 0.41478972f * Xp + 0.579999f * Yp + 0.014648f * Zp;
    var M = -0.20151f * Xp + 1.120649f * Yp + 0.0531008f * Zp;
    var S = -0.0166008f * Xp + 0.2648f * Yp + 0.6684799f * Zp;

    // Step 3: PQ-like nonlinearity (Hellwig variant: same form as BT.2100 PQ
    // but tuned exponents).
    static float JzPq(float v) {
      if (v <= 0f) return 0f;
      const float n = 2610f / 16384f;
      const float p = 1.7f * 2523f / 4096f * 128f;
      const float c1 = 3424f / 4096f;
      const float c2 = 2413f / 4096f * 32f;
      const float c3 = 2392f / 4096f * 32f;
      var x = MathF.Pow(v / 10000f, n);
      return MathF.Pow((c1 + c2 * x) / (1f + c3 * x), p);
    }
    var Lp = JzPq(L);
    var Mp = JzPq(M);
    var Sp = JzPq(S);

    // Step 4: L'M'S' -> Iz, az, bz.
    var Iz = 0.5f * Lp + 0.5f * Mp;
    var az = 3.524f * Lp - 4.06671f * Mp + 0.542708f * Sp;
    var bz = 0.199076f * Lp + 1.0967f * Mp - 1.295744f * Sp;

    // Step 5: Iz -> Jz.
    const float d = -0.56f;
    const float d0 = 1.6295499532821566e-11f;
    var Jz = (1f + d) * Iz / (1f + d * Iz) - d0;
    return (Jz, az, bz);
  }

  /// <summary>JzCzhz: cylindrical form of JzAzBz. Jz unchanged, Cz = sqrt(az^2+bz^2), hz∈[0,360).</summary>
  public static (float Jz, float Cz, float hz) JzCzhz(byte r, byte g, byte b) {
    var (Jz, az, bz) = JzAzBz(r, g, b);
    var Cz = MathF.Sqrt(az * az + bz * bz);
    var hz = MathF.Atan2(bz, az) * 180f / MathF.PI;
    if (hz < 0f) hz += 360f;
    return (Jz, Cz, hz);
  }
}
