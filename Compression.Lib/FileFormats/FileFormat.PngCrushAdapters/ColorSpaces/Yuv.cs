#pragma warning disable CS1591
namespace FileFormat.PngCrushAdapters.ColorSpaces;

/// <summary>
/// Y'-CbCr / YDbDr (SECAM) / YIQ (NTSC) projectors.
/// All operate on gamma-encoded sRGB (Y' is luma, not luminance) per their
/// respective standards.
/// </summary>
internal static class Yuv {

  /// <summary>YCbCr per ITU-R BT.601 (SDTV). Y∈[0,1], Cb/Cr∈[-0.5,+0.5].</summary>
  public static (float Y, float Cb, float Cr) YCbCr(byte rb, byte gb, byte bb) {
    var r = rb / 255f;
    var g = gb / 255f;
    var b = bb / 255f;
    var y = 0.299f * r + 0.587f * g + 0.114f * b;
    var cb = -0.168736f * r - 0.331264f * g + 0.5f * b;
    var cr = 0.5f * r - 0.418688f * g - 0.081312f * b;
    return (y, cb, cr);
  }

  /// <summary>YDbDr (SECAM, French TV) per ITU-R Recommendation BT.470-7.</summary>
  /// <remarks>
  /// Y = 0.299R + 0.587G + 0.114B
  /// Db = -0.450R - 0.883G + 1.333B  (range [-1.333,+1.333])
  /// Dr = -1.333R + 1.116G + 0.217B  (range [-1.333,+1.333])
  /// Reference: https://en.wikipedia.org/wiki/YDbDr
  /// </remarks>
  public static (float Y, float Db, float Dr) YDbDr(byte rb, byte gb, byte bb) {
    var r = rb / 255f;
    var g = gb / 255f;
    var b = bb / 255f;
    var y = 0.299f * r + 0.587f * g + 0.114f * b;
    var db = -0.450f * r - 0.883f * g + 1.333f * b;
    var dr = -1.333f * r + 1.116f * g + 0.217f * b;
    return (y, db, dr);
  }

  /// <summary>YIQ (NTSC) per FCC/SMPTE 170M.</summary>
  /// <remarks>
  /// Y = 0.299R + 0.587G + 0.114B
  /// I = 0.5959R - 0.2746G - 0.3213B  (range ~[-0.5957,+0.5957])
  /// Q = 0.2115R - 0.5227G + 0.3112B  (range ~[-0.5226,+0.5226])
  /// Reference: https://en.wikipedia.org/wiki/YIQ — modern (post-1953-FCC) coefficients.
  /// </remarks>
  public static (float Y, float I, float Q) Yiq(byte rb, byte gb, byte bb) {
    var r = rb / 255f;
    var g = gb / 255f;
    var b = bb / 255f;
    var y = 0.299f * r + 0.587f * g + 0.114f * b;
    var i = 0.5959f * r - 0.2746f * g - 0.3213f * b;
    var q = 0.2115f * r - 0.5227f * g + 0.3112f * b;
    return (y, i, q);
  }
}
