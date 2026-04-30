#pragma warning disable CS1591
namespace FileFormat.PngCrushAdapters.ColorSpaces;

/// <summary>
/// sRGB &lt;-&gt; linear-RGB transfer-function helpers per IEC 61966-2-1 (sRGB) /
/// the official W3C sRGB specification. Many of the colorspaces in this folder
/// (XYZ, Lab, Luv, Oklab, JzAzBz, ICtCp, ACEScg, etc.) are defined on
/// <em>linear</em> RGB — applying them to gamma-encoded sRGB bytes directly
/// would silently produce wrong values. Always pass byte components through
/// <see cref="ToLinear(byte)"/> first.
/// </summary>
internal static class SrgbGamma {

  /// <summary>Decodes an 8-bit gamma-encoded sRGB component to linear-light [0,1].</summary>
  /// <remarks>Reference: IEC 61966-2-1:1999 / https://en.wikipedia.org/wiki/SRGB</remarks>
  public static float ToLinear(byte b) {
    var x = b / 255f;
    return ToLinear(x);
  }

  /// <summary>Decodes a [0,1] gamma-encoded sRGB scalar to linear-light [0,1].</summary>
  public static float ToLinear(float c)
    => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

  /// <summary>Encodes a linear-light scalar back to gamma-encoded sRGB [0,1].</summary>
  public static float ToSrgb(float linear) {
    if (linear <= 0.0031308f) return 12.92f * linear;
    return 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
  }

  /// <summary>Encodes a linear-light scalar back to a gamma-encoded sRGB byte.</summary>
  public static byte ToSrgbByte(float linear) {
    var s = ToSrgb(linear);
    if (s <= 0f) return 0;
    if (s >= 1f) return 255;
    return (byte)(s * 255f + 0.5f);
  }
}
