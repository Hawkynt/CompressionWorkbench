#pragma warning disable CS1591
using FileFormat.Core;
using FileFormat.Png;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Image;

[TestFixture]
public class ColorSpaceSplitterTests {

  // Helper: make a 4x4 Rgba32 image with the given color repeated, alpha 255.
  private static RawImage SolidRgba(byte r, byte g, byte b, byte a = 255, int w = 4, int h = 4) {
    var n = w * h;
    var px = new byte[n * 4];
    for (var i = 0; i < n; i++) {
      px[i * 4] = r;
      px[i * 4 + 1] = g;
      px[i * 4 + 2] = b;
      px[i * 4 + 3] = a;
    }
    return new RawImage { Width = w, Height = h, Format = PixelFormat.Rgba32, PixelData = px };
  }

  [Test]
  public void Split_All_EmitsAll29ColorspaceFolders() {
    var img = SolidRgba(255, 128, 64);
    var planes = ColorSpaceSplitter.Split(img, ColorSpaceSet.All);
    var paths = planes.Select(p => p.Path).ToList();

    // All 29 folders, with their canonical component file names.
    Assert.Multiple(() => {
      // Original Wave A (six)
      Assert.That(paths, Does.Contain("colorspace/RGB/R.png"));
      Assert.That(paths, Does.Contain("colorspace/RGB/G.png"));
      Assert.That(paths, Does.Contain("colorspace/RGB/B.png"));
      Assert.That(paths, Does.Contain("colorspace/YCbCr/Y.png"));
      Assert.That(paths, Does.Contain("colorspace/YCbCr/Cb.png"));
      Assert.That(paths, Does.Contain("colorspace/YCbCr/Cr.png"));
      Assert.That(paths, Does.Contain("colorspace/HSL/H.png"));
      Assert.That(paths, Does.Contain("colorspace/HSL/S.png"));
      Assert.That(paths, Does.Contain("colorspace/HSL/L.png"));
      Assert.That(paths, Does.Contain("colorspace/CMYK/C.png"));
      Assert.That(paths, Does.Contain("colorspace/CMYK/M.png"));
      Assert.That(paths, Does.Contain("colorspace/CMYK/Y.png"));
      Assert.That(paths, Does.Contain("colorspace/CMYK/K.png"));
      Assert.That(paths, Does.Contain("colorspace/Lab/L.png"));
      Assert.That(paths, Does.Contain("colorspace/Lab/a.png"));
      Assert.That(paths, Does.Contain("colorspace/Lab/b.png"));
      Assert.That(paths, Does.Contain("colorspace/Oklab/L.png"));
      Assert.That(paths, Does.Contain("colorspace/Oklab/a.png"));
      Assert.That(paths, Does.Contain("colorspace/Oklab/b.png"));
      // Cylindrical
      Assert.That(paths, Does.Contain("colorspace/HSI/H.png"));
      Assert.That(paths, Does.Contain("colorspace/HSV/V.png"));
      Assert.That(paths, Does.Contain("colorspace/HWB/W.png"));
      Assert.That(paths, Does.Contain("colorspace/LCH/h.png"));
      Assert.That(paths, Does.Contain("colorspace/LChUv/h.png"));
      // Perceptual
      Assert.That(paths, Does.Contain("colorspace/Din99/L.png"));
      Assert.That(paths, Does.Contain("colorspace/HunterLab/L.png"));
      Assert.That(paths, Does.Contain("colorspace/Luv/u.png"));
      Assert.That(paths, Does.Contain("colorspace/Okhsl/H.png"));
      Assert.That(paths, Does.Contain("colorspace/Okhsv/V.png"));
      Assert.That(paths, Does.Contain("colorspace/Oklch/C.png"));
      // YUV-family
      Assert.That(paths, Does.Contain("colorspace/YDbDr/Db.png"));
      Assert.That(paths, Does.Contain("colorspace/YIQ/I.png"));
      // Wide-gamut
      Assert.That(paths, Does.Contain("colorspace/AcesCg/R.png"));
      Assert.That(paths, Does.Contain("colorspace/AdobeRGB/R.png"));
      Assert.That(paths, Does.Contain("colorspace/DisplayP3/R.png"));
      Assert.That(paths, Does.Contain("colorspace/ProPhotoRGB/R.png"));
      // HDR
      Assert.That(paths, Does.Contain("colorspace/XYZ/X.png"));
      Assert.That(paths, Does.Contain("colorspace/XyY/Y.png"));
      Assert.That(paths, Does.Contain("colorspace/ICtCp/Ct.png"));
      Assert.That(paths, Does.Contain("colorspace/JzAzBz/Jz.png"));
      Assert.That(paths, Does.Contain("colorspace/JzCzhz/hz.png"));
      // Alpha is colorspace-agnostic — NOT emitted by Split(); ExtractAlpha handles it.
      Assert.That(paths, Does.Not.Contain("colorspace/RGB/A.png"));
    });

    // 29 spaces × 3 components, except CMYK has 4 -> 28*3 + 4 = 88. Wait —
    // count by family: Wave A = RGB(3)+YCbCr(3)+HSL(3)+CMYK(4)+Lab(3)+Oklab(3) = 19.
    // Cyl = HSI/HSV/HWB/LCH/LChUv = 5*3 = 15.
    // Perc = Din99/HunterLab/Luv/Okhsl/Okhsv/Oklch = 6*3 = 18.
    // YUV+ = YDbDr/YIQ = 2*3 = 6.
    // WideGamut = AcesCg/AdobeRGB/DisplayP3/ProPhotoRGB = 4*3 = 12.
    // HDR = XYZ/XyY/ICtCp/JzAzBz/JzCzhz = 5*3 = 15.
    // Total = 19 + 15 + 18 + 6 + 12 + 15 = 85.
    Assert.That(planes, Has.Count.EqualTo(85));
  }

  [Test]
  public void Split_RgbOnly_OnRgbaSource_EmitsThreePlanes_NoAlpha() {
    var img = SolidRgba(10, 20, 30);
    var planes = ColorSpaceSplitter.Split(img, ColorSpaceSet.Rgb);
    Assert.That(planes, Has.Count.EqualTo(3));
    Assert.That(planes.Select(p => p.Path),
                Is.EquivalentTo(new[] {
                  "colorspace/RGB/R.png",
                  "colorspace/RGB/G.png",
                  "colorspace/RGB/B.png",
                }));
  }

  [Test]
  public void ExtractAlpha_OnRgbaSource_ReturnsTopLevelAlphaPng() {
    var img = SolidRgba(50, 60, 70, a: 200);
    var alpha = ColorSpaceSplitter.ExtractAlpha(img);
    Assert.That(alpha, Is.Not.Null);
    Assert.That(alpha!.Value.Path, Is.EqualTo("Alpha.png"),
      "alpha must be colorspace-agnostic — bare file name, not under colorspace/");
    var pixels = DecodeGray(alpha.Value.Data);
    Assert.That(pixels, Is.All.EqualTo((byte)200), "alpha plane must round-trip exact byte values");
  }

  [Test]
  public void ExtractAlpha_OnNonAlphaFormat_ReturnsNull() {
    // Build a Gray8 image (no alpha channel) and verify no alpha entry is produced.
    var img = new RawImage {
      Width = 4, Height = 4, Format = PixelFormat.Gray8,
      PixelData = new byte[16],
    };
    Assert.That(ColorSpaceSplitter.ExtractAlpha(img), Is.Null);
  }

  [Test]
  public void Split_None_EmitsNothing() {
    var img = SolidRgba(0, 0, 0);
    var planes = ColorSpaceSplitter.Split(img, ColorSpaceSet.None);
    Assert.That(planes, Is.Empty);
  }

  [Test]
  public void PureRed_RgbChannels_HaveExpectedValues() {
    var img = SolidRgba(255, 0, 0);
    var planes = ColorSpaceSplitter.Split(img, ColorSpaceSet.Rgb);

    var r = DecodeGray(planes.Single(p => p.Path == "colorspace/RGB/R.png").Data);
    var g = DecodeGray(planes.Single(p => p.Path == "colorspace/RGB/G.png").Data);
    var b = DecodeGray(planes.Single(p => p.Path == "colorspace/RGB/B.png").Data);
    Assert.That(r, Is.All.EqualTo((byte)255));
    Assert.That(g, Is.All.EqualTo((byte)0));
    Assert.That(b, Is.All.EqualTo((byte)0));
  }

  [Test]
  public void PureRed_YCbCr_LuminanceIsBT601_Approx76() {
    var img = SolidRgba(255, 0, 0);
    var planes = ColorSpaceSplitter.Split(img, ColorSpaceSet.YCbCr);
    var y = DecodeGray(planes.Single(p => p.Path == "colorspace/YCbCr/Y.png").Data);
    // BT.601: Y = 0.299 * 255 = 76.245 -> 76 after quantisation.
    Assert.That(y[0], Is.EqualTo((byte)76).Within(1));
  }

  [Test]
  public void PureRed_YCbCr_CbAndCrInRangeAroundZero() {
    var img = SolidRgba(255, 0, 0);
    var planes = ColorSpaceSplitter.Split(img, ColorSpaceSet.YCbCr);
    var cb = DecodeGray(planes.Single(p => p.Path == "colorspace/YCbCr/Cb.png").Data);
    var cr = DecodeGray(planes.Single(p => p.Path == "colorspace/YCbCr/Cr.png").Data);
    // Cb for pure red: -0.168736 -> mapped to ~85; Cr: +0.5 -> mapped to 255.
    Assert.That(cb[0], Is.EqualTo((byte)(((-0.168736f / 0.5f) + 1f) * 0.5f * 255f + 0.5f)).Within(2));
    Assert.That(cr[0], Is.EqualTo((byte)255));
  }

  [Test]
  public void TransparentPixel_AlphaPlaneRoundTripsViaExtractAlpha() {
    // Half-transparent RGBA: alpha=128 must round-trip through Alpha.png.
    var img = SolidRgba(50, 60, 70, a: 128);
    // Split() must NOT emit A.png inside the RGB folder anymore.
    var planes = ColorSpaceSplitter.Split(img, ColorSpaceSet.Rgb);
    Assert.That(planes.Select(p => p.Path), Does.Not.Contain("colorspace/RGB/A.png"));

    // Alpha lives at the top level via ExtractAlpha().
    var alpha = ColorSpaceSplitter.ExtractAlpha(img);
    Assert.That(alpha, Is.Not.Null);
    Assert.That(alpha!.Value.Path, Is.EqualTo("Alpha.png"));
    var bytes = DecodeGray(alpha.Value.Data);
    Assert.That(bytes, Is.All.EqualTo((byte)128));
  }

  [Test]
  public void EachEmittedPng_RoundTripsThroughPngReader() {
    var img = SolidRgba(123, 200, 33);
    var planes = ColorSpaceSplitter.Split(img, ColorSpaceSet.All);
    foreach (var (path, data) in planes) {
      var raw = PngFile.ToRawImage(PngReader.FromSpan(data));
      Assert.That(raw.Width, Is.EqualTo(4), $"width mismatch for {path}");
      Assert.That(raw.Height, Is.EqualTo(4), $"height mismatch for {path}");
      Assert.That(raw.Format, Is.EqualTo(PixelFormat.Gray8), $"format mismatch for {path}");
      Assert.That(raw.PixelData, Has.Length.EqualTo(16), $"data length mismatch for {path}");
    }
  }

  // Decode an 8-bit grayscale PNG plane to its raw byte buffer.
  private static byte[] DecodeGray(byte[] pngBytes) {
    var raw = PngFile.ToRawImage(PngReader.FromSpan(pngBytes));
    Assert.That(raw.Format, Is.EqualTo(PixelFormat.Gray8), "splitter must emit 8-bit grayscale planes");
    return raw.PixelData;
  }
}
