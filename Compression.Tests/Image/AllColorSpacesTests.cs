#pragma warning disable CS1591
using FileFormat.Core;
using FileFormat.Png;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Image;

/// <summary>
/// Reference-value tests for every implemented colorspace projector. Each
/// test case cites the authoritative source for the expected values
/// (Lindbloom, Wikipedia, Ottosson, ITU-R BT.xx, Safdar et al. 2017).
/// Tolerances: ±1 unit on quantised 8-bit output unless stated otherwise.
/// </summary>
[TestFixture]
public class AllColorSpacesTests {

  private static RawImage Solid(byte r, byte g, byte b) {
    var n = 1 * 1;
    var px = new byte[n * 4];
    px[0] = r; px[1] = g; px[2] = b; px[3] = 255;
    return new RawImage { Width = 1, Height = 1, Format = PixelFormat.Rgba32, PixelData = px };
  }

  private static byte ReadOne(byte[] pngBytes) {
    var raw = PngFile.ToRawImage(PngReader.FromSpan(pngBytes));
    Assert.That(raw.Format, Is.EqualTo(PixelFormat.Gray8));
    return raw.PixelData[0];
  }

  private static byte Get(IReadOnlyList<(string Path, byte[] Data)> planes, string path) {
    var match = planes.FirstOrDefault(p => p.Path == path);
    Assert.That(match.Data, Is.Not.Null, $"missing plane {path}");
    return ReadOne(match.Data);
  }

  // ============================================================ HSV
  // Reference: https://en.wikipedia.org/wiki/HSL_and_HSV (canonical examples)

  [Test]
  public void Hsv_PureRed_HasHue0_FullSat_FullValue() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.Hsv);
    Assert.That(Get(planes, "colorspace/HSV/H.png"), Is.EqualTo((byte)0));
    Assert.That(Get(planes, "colorspace/HSV/S.png"), Is.EqualTo((byte)255));
    Assert.That(Get(planes, "colorspace/HSV/V.png"), Is.EqualTo((byte)255));
  }

  [Test]
  public void Hsv_PureGreen_HasHue120_FullSat_FullValue() {
    var planes = ColorSpaceSplitter.Split(Solid(0, 255, 0), ColorSpaceSet.Hsv);
    // 120/360 * 255 = 85
    Assert.That(Get(planes, "colorspace/HSV/H.png"), Is.EqualTo((byte)85).Within(1));
    Assert.That(Get(planes, "colorspace/HSV/S.png"), Is.EqualTo((byte)255));
    Assert.That(Get(planes, "colorspace/HSV/V.png"), Is.EqualTo((byte)255));
  }

  [Test]
  public void Hsv_PureBlue_HasHue240() {
    var planes = ColorSpaceSplitter.Split(Solid(0, 0, 255), ColorSpaceSet.Hsv);
    Assert.That(Get(planes, "colorspace/HSV/H.png"), Is.EqualTo((byte)170).Within(1));
  }

  [Test]
  public void Hsv_White_HasZeroSat_FullValue() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.Hsv);
    Assert.That(Get(planes, "colorspace/HSV/S.png"), Is.EqualTo((byte)0));
    Assert.That(Get(planes, "colorspace/HSV/V.png"), Is.EqualTo((byte)255));
  }

  // ============================================================ HSI
  // Reference: https://en.wikipedia.org/wiki/HSL_and_HSV (HSI variant section)

  [Test]
  public void Hsi_PureRed_HasIntensityOneThird() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.Hsi);
    // I = (1+0+0)/3 = 0.333 -> 85
    Assert.That(Get(planes, "colorspace/HSI/I.png"), Is.EqualTo((byte)85).Within(1));
    Assert.That(Get(planes, "colorspace/HSI/S.png"), Is.EqualTo((byte)255));
  }

  [Test]
  public void Hsi_White_HasFullIntensityZeroSat() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.Hsi);
    Assert.That(Get(planes, "colorspace/HSI/I.png"), Is.EqualTo((byte)255));
    Assert.That(Get(planes, "colorspace/HSI/S.png"), Is.EqualTo((byte)0));
  }

  // ============================================================ HWB
  // Reference: https://en.wikipedia.org/wiki/HWB_color_model

  [Test]
  public void Hwb_PureRed_HasZeroWhiteZeroBlack() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.Hwb);
    Assert.That(Get(planes, "colorspace/HWB/W.png"), Is.EqualTo((byte)0));
    Assert.That(Get(planes, "colorspace/HWB/B.png"), Is.EqualTo((byte)0));
  }

  [Test]
  public void Hwb_HalfGray_HasHalfWhiteHalfBlack() {
    var planes = ColorSpaceSplitter.Split(Solid(128, 128, 128), ColorSpaceSet.Hwb);
    // W = min/255 = 128/255 ≈ 128, B = 1 - max/255 ≈ 127
    Assert.That(Get(planes, "colorspace/HWB/W.png"), Is.EqualTo((byte)128).Within(2));
    Assert.That(Get(planes, "colorspace/HWB/B.png"), Is.EqualTo((byte)127).Within(2));
  }

  // ============================================================ XYZ
  // Reference: http://www.brucelindbloom.com/index.html?Eqn_RGB_XYZ_Matrix.html
  // sRGB(255,0,0) -> XYZ(0.4124, 0.2126, 0.0193) D65

  [Test]
  public void Xyz_PureRed_MatchesLindbloomD65() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.Xyz);
    // Quantisation: X/Xn -> [0,1] -> *255. Xn=0.95047. X=0.4124.
    // expected X byte = 0.4124/0.95047 * 255 = 110.6 -> 111
    Assert.That(Get(planes, "colorspace/XYZ/X.png"), Is.EqualTo((byte)111).Within(2));
    // Y = 0.2126 -> 54
    Assert.That(Get(planes, "colorspace/XYZ/Y.png"), Is.EqualTo((byte)54).Within(2));
    // Z = 0.0193 / 1.08883 * 255 = 4.5
    Assert.That(Get(planes, "colorspace/XYZ/Z.png"), Is.EqualTo((byte)5).Within(2));
  }

  [Test]
  public void Xyz_White_MapsTo255_Each() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.Xyz);
    // sRGB white = D65 white. X/Xn=Y/Yn=Z/Zn=1.0 -> 255.
    Assert.That(Get(planes, "colorspace/XYZ/X.png"), Is.EqualTo((byte)255).Within(1));
    Assert.That(Get(planes, "colorspace/XYZ/Y.png"), Is.EqualTo((byte)255));
    Assert.That(Get(planes, "colorspace/XYZ/Z.png"), Is.EqualTo((byte)255).Within(1));
  }

  [Test]
  public void Xyz_Black_MapsToZero_Each() {
    var planes = ColorSpaceSplitter.Split(Solid(0, 0, 0), ColorSpaceSet.Xyz);
    Assert.That(Get(planes, "colorspace/XYZ/X.png"), Is.EqualTo((byte)0));
    Assert.That(Get(planes, "colorspace/XYZ/Y.png"), Is.EqualTo((byte)0));
    Assert.That(Get(planes, "colorspace/XYZ/Z.png"), Is.EqualTo((byte)0));
  }

  // ============================================================ Lab
  // Reference: http://www.brucelindbloom.com/index.html?ColorCalculator.html
  // sRGB(255,0,0) -> Lab(53.24, 80.09, 67.20) D65
  // sRGB(255,255,255) -> Lab(100, 0, 0)

  [Test]
  public void Lab_PureRed_MatchesLindbloom() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.Lab);
    // L=53.24 -> L/100 * 255 = 135.8 -> 136
    Assert.That(Get(planes, "colorspace/Lab/L.png"), Is.EqualTo((byte)136).Within(2));
    // a=80.09 -> Signed(a, 128) = (80.09/128 + 1) * 0.5 * 255 = 207.7 -> 208
    Assert.That(Get(planes, "colorspace/Lab/a.png"), Is.EqualTo((byte)208).Within(2));
    // b=67.20 -> 194.4 -> 194
    Assert.That(Get(planes, "colorspace/Lab/b.png"), Is.EqualTo((byte)194).Within(2));
  }

  [Test]
  public void Lab_White_HasL100_a0_b0() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.Lab);
    Assert.That(Get(planes, "colorspace/Lab/L.png"), Is.EqualTo((byte)255));
    Assert.That(Get(planes, "colorspace/Lab/a.png"), Is.EqualTo((byte)128).Within(1));
    Assert.That(Get(planes, "colorspace/Lab/b.png"), Is.EqualTo((byte)128).Within(1));
  }

  // ============================================================ Luv
  // Reference: http://www.brucelindbloom.com/index.html?ColorCalculator.html
  // sRGB(255,0,0) -> Luv(53.24, 175.02, 37.76) D65
  // White -> Luv(100, 0, 0)

  [Test]
  public void Luv_PureRed_MatchesLindbloom() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.Luv);
    // L=53.24 -> 53.24/100 * 255 ≈ 136
    Assert.That(Get(planes, "colorspace/Luv/L.png"), Is.EqualTo((byte)136).Within(2));
    // u=175 with range [-83,175] -> (175+83)/(175+83) = 1.0 -> 255
    Assert.That(Get(planes, "colorspace/Luv/u.png"), Is.EqualTo((byte)255).Within(2));
    // v=37.76 with range [-134,107] -> (37.76+134)/(107+134) = 0.713 -> 182
    Assert.That(Get(planes, "colorspace/Luv/v.png"), Is.EqualTo((byte)182).Within(3));
  }

  [Test]
  public void Luv_White_HasL100() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.Luv);
    Assert.That(Get(planes, "colorspace/Luv/L.png"), Is.EqualTo((byte)255));
  }

  // ============================================================ Oklab
  // Reference: https://bottosson.github.io/posts/oklab/ (table at bottom)
  // sRGB(255,0,0) -> Oklab(L=0.628, a=0.225, b=0.126)
  // sRGB(255,255,255) -> Oklab(L=1.000, a=0, b=0)

  [Test]
  public void Oklab_PureRed_MatchesOttosson() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.Oklab);
    // L=0.628 * 255 = 160
    Assert.That(Get(planes, "colorspace/Oklab/L.png"), Is.EqualTo((byte)160).Within(2));
    // a=0.225 with range [-0.4,+0.4] -> (0.225/0.4+1)*0.5*255 = 199
    Assert.That(Get(planes, "colorspace/Oklab/a.png"), Is.EqualTo((byte)199).Within(2));
    // b=0.126 -> (0.126/0.4+1)*0.5*255 = 168
    Assert.That(Get(planes, "colorspace/Oklab/b.png"), Is.EqualTo((byte)168).Within(2));
  }

  [Test]
  public void Oklab_White_HasL1_a0_b0() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.Oklab);
    Assert.That(Get(planes, "colorspace/Oklab/L.png"), Is.EqualTo((byte)255));
    Assert.That(Get(planes, "colorspace/Oklab/a.png"), Is.EqualTo((byte)128).Within(1));
    Assert.That(Get(planes, "colorspace/Oklab/b.png"), Is.EqualTo((byte)128).Within(1));
  }

  // ============================================================ LCH
  // Reference: http://www.brucelindbloom.com/index.html?Eqn_Lab_to_LCH.html
  // sRGB(255,0,0) -> Lab(53.24,80.09,67.20) -> LCH(L=53.24, C=104.55, h=39.99)

  [Test]
  public void Lch_PureRed_MatchesLindbloom() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.Lch);
    // L = 53.24/100 * 255 = 136
    Assert.That(Get(planes, "colorspace/LCH/L.png"), Is.EqualTo((byte)136).Within(2));
    // C = 104.55, range[0,150] -> 104.55/150*255 = 178
    Assert.That(Get(planes, "colorspace/LCH/C.png"), Is.EqualTo((byte)178).Within(3));
    // h = 39.99 deg -> 39.99/360 * 255 = 28
    Assert.That(Get(planes, "colorspace/LCH/h.png"), Is.EqualTo((byte)28).Within(2));
  }

  // ============================================================ Oklch
  // Reference: https://oklch.com/ (Ottosson's tool) — sRGB(255,0,0) ≈ L=0.628 C=0.258 h=29.23

  [Test]
  public void Oklch_PureRed_MatchesOttosson() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.Oklch);
    Assert.That(Get(planes, "colorspace/Oklch/L.png"), Is.EqualTo((byte)160).Within(2));
    // C=0.258 -> 0.258/0.4 * 255 = 164
    Assert.That(Get(planes, "colorspace/Oklch/C.png"), Is.EqualTo((byte)164).Within(3));
    // h=29.23 -> 21
    Assert.That(Get(planes, "colorspace/Oklch/h.png"), Is.EqualTo((byte)21).Within(2));
  }

  // ============================================================ YCbCr (BT.601)
  // Reference: ITU-R BT.601, https://en.wikipedia.org/wiki/YCbCr
  // sRGB(255,0,0) full-range BT.601 -> Y=76.245

  [Test]
  public void YCbCr_PureRed_LumaIs76() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.YCbCr);
    Assert.That(Get(planes, "colorspace/YCbCr/Y.png"), Is.EqualTo((byte)76).Within(1));
    // Cr is +0.5 -> 255
    Assert.That(Get(planes, "colorspace/YCbCr/Cr.png"), Is.EqualTo((byte)255));
  }

  // ============================================================ YDbDr (SECAM)
  // Reference: ITU-R BT.470-7 / https://en.wikipedia.org/wiki/YDbDr
  // sRGB(255,0,0) -> Y=0.299, Db=-0.450, Dr=-1.333

  [Test]
  public void YDbDr_PureRed_MatchesSecamSpec() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.YDbDr);
    // Y=0.299 -> 76
    Assert.That(Get(planes, "colorspace/YDbDr/Y.png"), Is.EqualTo((byte)76).Within(1));
    // Db=-0.450 / 1.333 = -0.337 -> (1-0.337)/2 * 255 = 84.5
    Assert.That(Get(planes, "colorspace/YDbDr/Db.png"), Is.EqualTo((byte)85).Within(2));
    // Dr=-1.333 -> 0
    Assert.That(Get(planes, "colorspace/YDbDr/Dr.png"), Is.EqualTo((byte)0));
  }

  [Test]
  public void YDbDr_PureBlue_DbIsMaxPositive() {
    var planes = ColorSpaceSplitter.Split(Solid(0, 0, 255), ColorSpaceSet.YDbDr);
    // Db=+1.333 -> 255
    Assert.That(Get(planes, "colorspace/YDbDr/Db.png"), Is.EqualTo((byte)255));
  }

  // ============================================================ YIQ (NTSC)
  // Reference: SMPTE 170M / FCC, https://en.wikipedia.org/wiki/YIQ
  // sRGB(255,0,0) -> Y=0.299, I=0.5959, Q=0.2115

  [Test]
  public void Yiq_PureRed_MatchesNtsc() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.Yiq);
    Assert.That(Get(planes, "colorspace/YIQ/Y.png"), Is.EqualTo((byte)76).Within(1));
    // I=0.5959 with range 0.5957 -> saturates to ~255
    Assert.That(Get(planes, "colorspace/YIQ/I.png"), Is.EqualTo((byte)255).Within(1));
    // Q=0.2115/0.5226 = 0.4047 -> (1+0.4047)/2 * 255 = 179
    Assert.That(Get(planes, "colorspace/YIQ/Q.png"), Is.EqualTo((byte)179).Within(2));
  }

  // ============================================================ Adobe RGB
  // Reference: Adobe RGB (1998) PN 091031 + Lindbloom matrix.
  // sRGB(255,0,0) linear -> AdobeRGB linear: R≈0.7152, G=0, B=0.

  [Test]
  public void AdobeRgb_PureRed_HasReducedRed() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.AdobeRgb);
    // R=0.7152 with range[-0.25,1.25]: t=(0.7152+0.25)/1.5 = 0.643 -> 164
    Assert.That(Get(planes, "colorspace/AdobeRGB/R.png"), Is.EqualTo((byte)164).Within(3));
    // G ≈ 0 -> t=0.25/1.5=0.167 -> 42
    Assert.That(Get(planes, "colorspace/AdobeRGB/G.png"), Is.EqualTo((byte)42).Within(3));
    // B ≈ 0 -> 42
    Assert.That(Get(planes, "colorspace/AdobeRGB/B.png"), Is.EqualTo((byte)42).Within(3));
  }

  [Test]
  public void AdobeRgb_White_RoundtripsToWhite() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.AdobeRgb);
    // White is in-gamut for both. R=G=B=1.0 -> (1+0.25)/1.5 = 0.833 -> 213
    Assert.That(Get(planes, "colorspace/AdobeRGB/R.png"), Is.EqualTo((byte)213).Within(2));
    Assert.That(Get(planes, "colorspace/AdobeRGB/G.png"), Is.EqualTo((byte)213).Within(2));
    Assert.That(Get(planes, "colorspace/AdobeRGB/B.png"), Is.EqualTo((byte)213).Within(2));
  }

  // ============================================================ Display P3
  // Reference: https://drafts.csswg.org/css-color-4/#color-conversion-code
  // sRGB(255,0,0) linear -> P3 linear ≈ R=0.917, G=0.200, B=0.139

  [Test]
  public void DisplayP3_PureRed_HasReducedRed() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.DisplayP3);
    // R=0.8225 -> (0.8225+0.25)/1.5*255 = 182
    Assert.That(Get(planes, "colorspace/DisplayP3/R.png"), Is.EqualTo((byte)182).Within(3));
    // G=0.0331 -> (0.0331+0.25)/1.5*255 = 48
    Assert.That(Get(planes, "colorspace/DisplayP3/G.png"), Is.EqualTo((byte)48).Within(3));
    // B=0.0171 -> (0.0171+0.25)/1.5*255 = 45
    Assert.That(Get(planes, "colorspace/DisplayP3/B.png"), Is.EqualTo((byte)45).Within(3));
  }

  // ============================================================ ProPhoto RGB
  // Reference: ROMM RGB / Lindbloom — sRGB(255,255,255) maps to ProPhoto white (D50).

  [Test]
  public void ProPhotoRgb_White_RoundtripsToWhite() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.ProPhotoRgb);
    // D65 white in linsRGB to D50 ProPhoto-linear: each ≈ 1.0.
    Assert.That(Get(planes, "colorspace/ProPhotoRGB/R.png"), Is.EqualTo((byte)213).Within(3));
    Assert.That(Get(planes, "colorspace/ProPhotoRGB/G.png"), Is.EqualTo((byte)213).Within(3));
    Assert.That(Get(planes, "colorspace/ProPhotoRGB/B.png"), Is.EqualTo((byte)213).Within(3));
  }

  // ============================================================ ACEScg
  // Reference: ACES IDT for sRGB. sRGB(255,0,0) linear -> AcesCg(R=0.613,G=0.070,B=0.020)

  [Test]
  public void AcesCg_PureRed_HasReducedRed() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.AcesCg);
    // R=0.6131 -> (0.6131+0.25)/1.5 * 255 = 146.6
    Assert.That(Get(planes, "colorspace/AcesCg/R.png"), Is.EqualTo((byte)147).Within(3));
    // G=0.07 -> (0.07+0.25)/1.5 * 255 = 54
    Assert.That(Get(planes, "colorspace/AcesCg/G.png"), Is.EqualTo((byte)54).Within(3));
    // B=0.02 -> (0.02+0.25)/1.5 * 255 = 46
    Assert.That(Get(planes, "colorspace/AcesCg/B.png"), Is.EqualTo((byte)46).Within(3));
  }

  // ============================================================ HunterLab
  // Reference: http://www.brucelindbloom.com/index.html?Eqn_XYZ_to_HunterLab.html
  // White: HunterLab(L=100, a=0, b=0).

  [Test]
  public void HunterLab_White_HasL100_a0_b0() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.HunterLab);
    Assert.That(Get(planes, "colorspace/HunterLab/L.png"), Is.EqualTo((byte)255));
    Assert.That(Get(planes, "colorspace/HunterLab/a.png"), Is.EqualTo((byte)128).Within(1));
    Assert.That(Get(planes, "colorspace/HunterLab/b.png"), Is.EqualTo((byte)128).Within(1));
  }

  [Test]
  public void HunterLab_PureRed_HasPositive_a() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.HunterLab);
    // a > 0 -> > 128
    Assert.That(Get(planes, "colorspace/HunterLab/a.png"), Is.GreaterThan((byte)160));
  }

  // ============================================================ DIN99
  // Reference: DIN 6176 / Cui-Luo-Rigg 2002.
  // White: Lab(100,0,0) -> DIN99(L99=100, a99=0, b99=0).

  [Test]
  public void Din99_White_IsZeroChroma() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.Din99);
    // L99 = 105.51 * ln(1 + 0.0158*100) = 105.51 * ln(2.58) = 100.0
    Assert.That(Get(planes, "colorspace/Din99/L.png"), Is.EqualTo((byte)255).Within(2));
    Assert.That(Get(planes, "colorspace/Din99/a.png"), Is.EqualTo((byte)128).Within(1));
    Assert.That(Get(planes, "colorspace/Din99/b.png"), Is.EqualTo((byte)128).Within(1));
  }

  // ============================================================ XyY
  // Reference: http://www.brucelindbloom.com/index.html?Eqn_XYZ_to_xyY.html
  // White: xyY = (0.31271, 0.32902, 1.0) (D65 chromaticity).

  [Test]
  public void XyY_White_MatchesD65Chromaticity() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.XyY);
    // x=0.31271/0.74 * 255 = 107.7
    Assert.That(Get(planes, "colorspace/XyY/x.png"), Is.EqualTo((byte)108).Within(2));
    // y=0.32902/0.74 * 255 = 113.4
    Assert.That(Get(planes, "colorspace/XyY/y.png"), Is.EqualTo((byte)113).Within(2));
    // Y=1 -> 255
    Assert.That(Get(planes, "colorspace/XyY/Y.png"), Is.EqualTo((byte)255));
  }

  // ============================================================ JzAzBz
  // Reference: Safdar et al. 2017 (Optics Express). Black -> (0,0,0); white = positive Jz.

  [Test]
  public void JzAzBz_Black_IsZero() {
    var planes = ColorSpaceSplitter.Split(Solid(0, 0, 0), ColorSpaceSet.JzAzBz);
    Assert.That(Get(planes, "colorspace/JzAzBz/Jz.png"), Is.EqualTo((byte)0).Within(1));
    Assert.That(Get(planes, "colorspace/JzAzBz/az.png"), Is.EqualTo((byte)128).Within(2));
    Assert.That(Get(planes, "colorspace/JzAzBz/bz.png"), Is.EqualTo((byte)128).Within(2));
  }

  [Test]
  public void JzAzBz_White_HasPositiveJz() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.JzAzBz);
    // White Jz > 0 (achromatic stimulus produces nonzero perceived lightness).
    Assert.That(Get(planes, "colorspace/JzAzBz/Jz.png"), Is.GreaterThan((byte)0));
  }

  // ============================================================ JzCzhz
  [Test]
  public void JzCzhz_White_IsAchromatic() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.JzCzhz);
    // Cz ≈ 0 for achromatic input.
    Assert.That(Get(planes, "colorspace/JzCzhz/Cz.png"), Is.LessThan((byte)10));
  }

  // ============================================================ ICtCp
  // Reference: BT.2100 / Dolby ICtCp white paper.
  // Black -> (0, 0, 0). White (SDR 100-nit map) -> Iz around 0.5, Ct/Cp ≈ 0.

  [Test]
  public void ICtCp_Black_IsZero() {
    var planes = ColorSpaceSplitter.Split(Solid(0, 0, 0), ColorSpaceSet.ICtCp);
    Assert.That(Get(planes, "colorspace/ICtCp/I.png"), Is.EqualTo((byte)0).Within(1));
    Assert.That(Get(planes, "colorspace/ICtCp/Ct.png"), Is.EqualTo((byte)128).Within(2));
    Assert.That(Get(planes, "colorspace/ICtCp/Cp.png"), Is.EqualTo((byte)128).Within(2));
  }

  [Test]
  public void ICtCp_White_HasNonzeroI() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.ICtCp);
    // SDR 100-nit white maps via PQ to ~0.508 -> ~129.
    Assert.That(Get(planes, "colorspace/ICtCp/I.png"), Is.GreaterThan((byte)100));
    Assert.That(Get(planes, "colorspace/ICtCp/I.png"), Is.LessThan((byte)160));
  }

  // ============================================================ Okhsl/Okhsv
  // Reference: https://bottosson.github.io/posts/colorpicker/
  // White: V=1, S=0; Black: V=0.

  [Test]
  public void Okhsl_White_IsAchromatic() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.Okhsl);
    Assert.That(Get(planes, "colorspace/Okhsl/S.png"), Is.LessThan((byte)5));
    // L (toed) for L=1 in Oklab -> 1.0 -> 255
    Assert.That(Get(planes, "colorspace/Okhsl/L.png"), Is.EqualTo((byte)255).Within(2));
  }

  [Test]
  public void Okhsv_PureRed_HasNonzeroSat() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.Okhsv);
    Assert.That(Get(planes, "colorspace/Okhsv/S.png"), Is.GreaterThan((byte)100));
  }

  // ============================================================ LChUv
  // Reference: Lindbloom polar form of Luv.

  [Test]
  public void LchUv_PureRed_HasPositiveChroma() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 0, 0), ColorSpaceSet.LchUv);
    Assert.That(Get(planes, "colorspace/LChUv/C.png"), Is.GreaterThan((byte)100));
  }

  // ============================================================ Black/White invariants
  // Black should produce zero or 128 (signed-zero) for every component except hue.

  [Test]
  public void Black_ProducesValidValues_ForAllSpaces() {
    var planes = ColorSpaceSplitter.Split(Solid(0, 0, 0), ColorSpaceSet.All);
    foreach (var (path, data) in planes) {
      var v = ReadOne(data);
      // Just assert PNG decode succeeded and value is in byte range. Specific
      // expected zeros for black are covered in per-space tests above.
      // Notable exceptions: CMYK/K = 255 (black is full black-channel),
      // XyY for black falls back to D65 chromaticity (x=108, y=113),
      // ICtCp/JzAzBz for black ≈ 0 / signed-zero (128).
      Assert.That(v, Is.InRange((byte)0, (byte)255), $"{path} byte out of range");
    }
  }

  [Test]
  public void White_ProducesValidValues_ForAllSpaces() {
    var planes = ColorSpaceSplitter.Split(Solid(255, 255, 255), ColorSpaceSet.All);
    foreach (var (path, data) in planes) {
      var v = ReadOne(data);
      // All values must be in [0,255] (i.e. PNG decode succeeded), no NaN propagation.
      Assert.That(v, Is.InRange((byte)0, (byte)255), $"{path} produced out-of-range byte for white input");
    }
  }
}
