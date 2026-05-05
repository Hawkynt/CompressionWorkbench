#pragma warning disable CS1591
namespace FileFormat.PngCrushAdapters.ColorSpaces;

/// <summary>
/// Wide-gamut RGB primaries variants. Each takes gamma-encoded sRGB bytes and
/// returns linear-light coordinates in the target color space's primaries
/// (no encoding/transfer-function applied to the output — these are
/// component planes for forensic inspection, not display-ready pixels).
/// </summary>
/// <remarks>
/// Conversion path is sRGB-byte -&gt; linear sRGB -&gt; XYZ (D65 or with chromatic
/// adaptation to D50 when needed) -&gt; linear target-RGB.
/// Out-of-gamut sRGB inputs may produce negative target components; we
/// quantise via <see cref="Quant.Range"/> across [-0.25,+1.25] so a faithful
/// negative chroma is visible rather than clipped.
/// References:
/// <list type="bullet">
///   <item>ACEScg / ACES 2065-1: SMPTE ST 2065-1, ACES 1.0 specification (https://acescentral.com/)</item>
///   <item>Adobe RGB (1998): Adobe Systems specification PN 091031.</item>
///   <item>Display P3 / P3-D65: SMPTE RP 431-2 + Apple https://webkit.org/blog/10042/wide-gamut-color-in-css-with-display-p3/</item>
///   <item>ProPhoto RGB: ROMM RGB / ANSI/I3A IT10.7666:2002. Uses D50 white.</item>
///   <item>Lindbloom matrices: http://www.brucelindbloom.com/index.html?WorkingSpaceInfo.html</item>
/// </list>
/// </remarks>
internal static class WideGamut {

  // Bradford D65 -> D50 chromatic-adaptation matrix (Lindbloom).
  private const float B65to50_11 = 1.0478112f, B65to50_12 = 0.0228866f, B65to50_13 = -0.0501270f;
  private const float B65to50_21 = 0.0295424f, B65to50_22 = 0.9904844f, B65to50_23 = -0.0170491f;
  private const float B65to50_31 = -0.0092345f, B65to50_32 = 0.0150436f, B65to50_33 = 0.7521316f;

  /// <summary>sRGB bytes -&gt; linear ACEScg.</summary>
  /// <remarks>
  /// ACEScg uses AP1 primaries with a D60 white point. We use the
  /// ACES-published "sRGB(D65) -&gt; ACEScg" matrix (equivalent to
  /// XYZ-D65 -&gt; linsRGB inverse -&gt; CAT-D65-to-D60 -&gt; XYZ-D60 -&gt; ACEScg-AP1).
  /// Coefficients per ACES Color Specification (ACES 1.0):
  /// </remarks>
  public static (float R, float G, float B) AcesCg(byte r, byte g, byte b) {
    var lr = SrgbGamma.ToLinear(r);
    var lg = SrgbGamma.ToLinear(g);
    var lb = SrgbGamma.ToLinear(b);
    // Direct sRGB-D65 -> ACEScg-AP1 (combined CAT). Source: ACES sRGB IDT.
    var R = 0.6131178f * lr + 0.3411769f * lg + 0.0457052f * lb;
    var G = 0.0699893f * lr + 0.9181651f * lg + 0.0118456f * lb;
    var B = 0.0204576f * lr + 0.1067743f * lg + 0.8727681f * lb;
    return (R, G, B);
  }

  /// <summary>sRGB bytes -&gt; linear Adobe RGB (1998).</summary>
  /// <remarks>
  /// Both spaces share D65 white. Matrix derived from Adobe RGB primaries via
  /// Lindbloom: M_AdobeRGB^-1 * M_sRGB.
  /// </remarks>
  public static (float R, float G, float B) AdobeRgb(byte r, byte g, byte b) {
    var lr = SrgbGamma.ToLinear(r);
    var lg = SrgbGamma.ToLinear(g);
    var lb = SrgbGamma.ToLinear(b);
    // sRGB-linear -> XYZ -> AdobeRGB-linear (combined).
    var R = 0.7151865f * lr + 0.2848567f * lg - 0.0000432f * lb;
    var G = 0.0000000f * lr + 1.0000000f * lg + 0.0000000f * lb;
    var B = 0.0000000f * lr + 0.0411210f * lg + 0.9588790f * lb;
    return (R, G, B);
  }

  /// <summary>sRGB bytes -&gt; linear Display P3 (D65).</summary>
  /// <remarks>
  /// Display-P3 uses DCI-P3 primaries with D65 white (Apple variant).
  /// Matrix from https://drafts.csswg.org/css-color-4/#color-conversion-code .
  /// </remarks>
  public static (float R, float G, float B) DisplayP3(byte r, byte g, byte b) {
    var lr = SrgbGamma.ToLinear(r);
    var lg = SrgbGamma.ToLinear(g);
    var lb = SrgbGamma.ToLinear(b);
    var R = 0.8224621f * lr + 0.1775380f * lg + 0.0000000f * lb;
    var G = 0.0331941f * lr + 0.9668058f * lg + 0.0000000f * lb;
    var B = 0.0170827f * lr + 0.0723974f * lg + 0.9105199f * lb;
    return (R, G, B);
  }

  /// <summary>sRGB bytes -&gt; linear ProPhoto RGB (ROMM RGB).</summary>
  /// <remarks>
  /// ProPhoto uses D50 white, so a Bradford D65-&gt;D50 CAT is required.
  /// Matrix per Lindbloom http://www.brucelindbloom.com/index.html?Eqn_RGB_XYZ_Matrix.html
  /// (linsRGB-D65 -&gt; XYZ-D65 -&gt; XYZ-D50 -&gt; ProPhoto).
  /// </remarks>
  public static (float R, float G, float B) ProPhotoRgb(byte r, byte g, byte b) {
    var lr = SrgbGamma.ToLinear(r);
    var lg = SrgbGamma.ToLinear(g);
    var lb = SrgbGamma.ToLinear(b);
    var R = 0.5294118f * lr + 0.3300829f * lg + 0.1405053f * lb;
    var G = 0.0982400f * lr + 0.8734530f * lg + 0.0282970f * lb;
    var B = 0.0168761f * lr + 0.1176922f * lg + 0.8654310f * lb;
    return (R, G, B);
  }
}
