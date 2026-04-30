#pragma warning disable CS1591
using FileFormat.Core;
using FileFormat.Png;
using FileFormat.PngCrushAdapters.ColorSpaces;
using static FileFormat.PngCrushAdapters.ColorSpaces.Quant;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Splits a decoded <see cref="RawImage"/> into per-component grayscale PNGs
/// across the requested colorspaces. The returned tuples carry sub-paths like
/// <c>colorspace/RGB/R.png</c>, ready to be appended under a frame folder.
/// </summary>
/// <remarks>
/// Per-space projector math lives in <c>ColorSpaces/*.cs</c> (clean-room,
/// no GDI / no <c>System.Drawing.Bitmap</c>). Components are quantised to
/// 8-bit grayscale using component-specific min/max ranges documented at
/// each call site. PNG encoding goes through PngCrushCS' <see cref="PngWriter"/>
/// we already reference, so we don't ship a second PNG encoder.
/// <para>
/// The sibling <c>System.Drawing.Extensions/ColorProcessing/Spaces</c> at
/// <c>..\..\C--FrameworkExtensions</c> has equivalent projectors but its
/// build graph (Hawkynt.ColorProcessing.{Codecs,Constants,Internal,Working},
/// FixedPointMath, T4 sources, embedded resources) makes a ProjectReference
/// impractical from .NET 10. The clean-room math here cites authoritative
/// public references (Lindbloom, ITU-R BTs, Ottosson, Safdar et al.) per file.
/// </para>
/// </remarks>
public static class ColorSpaceSplitter {

  /// <summary>
  /// Computes only the planes belonging to a single colorspace flag — the lazy-extract
  /// path used by <c>MultiImageArchiveHelper.Extract</c> when the user filtered
  /// the entry list down to one or a few components. Skipping the other 28 spaces
  /// avoids ~28× the per-pixel work.
  /// </summary>
  /// <param name="image">Decoded source frame.</param>
  /// <param name="flag">A single-bit <see cref="ColorSpaceSet"/> value (e.g. <see cref="ColorSpaceSet.YCbCr"/>).</param>
  public static IReadOnlyList<(string Path, byte[] Data)> SplitOne(RawImage image, ColorSpaceSet flag) {
    ArgumentNullException.ThrowIfNull(image);
    if (flag == ColorSpaceSet.None) return Array.Empty<(string, byte[])>();
    return Split(image, flag);
  }

  /// <summary>Splits <paramref name="image"/> into one PNG per component for every requested colorspace.</summary>
  /// <remarks>
  /// Alpha is NOT included in the RGB folder — alpha is colorspace-independent and
  /// is exposed via <see cref="ExtractAlpha"/>. Callers are responsible for placing
  /// the alpha plane alongside the composite frame at the host-image level.
  /// </remarks>
  public static IReadOnlyList<(string Path, byte[] Data)> Split(RawImage image, ColorSpaceSet spaces) {
    ArgumentNullException.ThrowIfNull(image);
    if (spaces == ColorSpaceSet.None) return Array.Empty<(string, byte[])>();

    var rgba = image.Format == PixelFormat.Rgba32 ? image : PixelConverter.Convert(image, PixelFormat.Rgba32);
    var pixels = rgba.PixelData;
    var w = rgba.Width;
    var h = rgba.Height;
    var n = w * h;

    var result = new List<(string Path, byte[] Data)>();

    if ((spaces & ColorSpaceSet.Rgb) != 0) AddRgb(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.YCbCr) != 0) AddYCbCr(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Hsl) != 0) AddHsl(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Cmyk) != 0) AddCmyk(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Lab) != 0) AddLab(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Oklab) != 0) AddOklab(result, pixels, w, h, n);

    if ((spaces & ColorSpaceSet.Hsi) != 0) AddHsi(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Hsv) != 0) AddHsv(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Hwb) != 0) AddHwb(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Lch) != 0) AddLch(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.LchUv) != 0) AddLchUv(result, pixels, w, h, n);

    if ((spaces & ColorSpaceSet.Din99) != 0) AddDin99(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.HunterLab) != 0) AddHunterLab(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Luv) != 0) AddLuv(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Okhsl) != 0) AddOkhsl(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Okhsv) != 0) AddOkhsv(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Oklch) != 0) AddOklch(result, pixels, w, h, n);

    if ((spaces & ColorSpaceSet.YDbDr) != 0) AddYDbDr(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.Yiq) != 0) AddYiq(result, pixels, w, h, n);

    if ((spaces & ColorSpaceSet.AcesCg) != 0) AddAcesCg(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.AdobeRgb) != 0) AddAdobeRgb(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.DisplayP3) != 0) AddDisplayP3(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.ProPhotoRgb) != 0) AddProPhotoRgb(result, pixels, w, h, n);

    if ((spaces & ColorSpaceSet.Xyz) != 0) AddXyz(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.XyY) != 0) AddXyY(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.ICtCp) != 0) AddICtCp(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.JzAzBz) != 0) AddJzAzBz(result, pixels, w, h, n);
    if ((spaces & ColorSpaceSet.JzCzhz) != 0) AddJzCzhz(result, pixels, w, h, n);

    return result;
  }

  /// <summary>
  /// Extracts the alpha plane as a colorspace-agnostic single-channel image. Returns
  /// <c>null</c> when <paramref name="image"/>'s format carries no alpha channel.
  /// </summary>
  public static (string Path, byte[] Data)? ExtractAlpha(RawImage image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!HasAlphaChannel(image.Format)) return null;
    var rgba = image.Format == PixelFormat.Rgba32 ? image : PixelConverter.Convert(image, PixelFormat.Rgba32);
    var n = rgba.Width * rgba.Height;
    var alpha = new byte[n];
    var pixels = rgba.PixelData;
    for (var i = 0; i < n; i++) alpha[i] = pixels[i * 4 + 3];
    return ("Alpha.png", EncodeGray(alpha, rgba.Width, rgba.Height));
  }

  // ============================================================ existing six

  private static void AddRgb(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var r = new byte[n]; var g = new byte[n]; var b = new byte[n];
    for (var i = 0; i < n; i++) { var p = i * 4; r[i] = rgba[p]; g[i] = rgba[p + 1]; b[i] = rgba[p + 2]; }
    Emit(sink, "RGB", ("R", r), ("G", g), ("B", b), w, h);
  }

  private static void AddYCbCr(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var y = new byte[n]; var cb = new byte[n]; var cr = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (yf, cbf, crf) = Yuv.YCbCr(rgba[p], rgba[p + 1], rgba[p + 2]);
      y[i] = Unit(yf);
      cb[i] = Signed(cbf, 0.5f);
      cr[i] = Signed(crf, 0.5f);
    }
    Emit(sink, "YCbCr", ("Y", y), ("Cb", cb), ("Cr", cr), w, h);
  }

  private static void AddHsl(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var hb = new byte[n]; var sb = new byte[n]; var lb = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (H, S, L) = Cylindrical.Hsl(rgba[p], rgba[p + 1], rgba[p + 2]);
      hb[i] = Hue(H); sb[i] = Unit(S); lb[i] = Unit(L);
    }
    Emit(sink, "HSL", ("H", hb), ("S", sb), ("L", lb), w, h);
  }

  private static void AddCmyk(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var c = new byte[n]; var m = new byte[n]; var yy = new byte[n]; var k = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var rf = rgba[p] / 255f;
      var gf = rgba[p + 1] / 255f;
      var bf = rgba[p + 2] / 255f;
      var kf = 1f - MathF.Max(rf, MathF.Max(gf, bf));
      float cf, mf, ycf;
      if (kf >= 1f) { cf = 0f; mf = 0f; ycf = 0f; } else {
        var inv = 1f - kf;
        cf = (1f - rf - kf) / inv;
        mf = (1f - gf - kf) / inv;
        ycf = (1f - bf - kf) / inv;
      }
      c[i] = Unit(cf); m[i] = Unit(mf); yy[i] = Unit(ycf); k[i] = Unit(kf);
    }
    Emit4(sink, "CMYK", ("C", c), ("M", m), ("Y", yy), ("K", k), w, h);
  }

  private static void AddLab(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var lp = new byte[n]; var ap = new byte[n]; var bp = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (L, a, b) = ColorSpaces.Lab.Project(rgba[p], rgba[p + 1], rgba[p + 2]);
      lp[i] = Range(L, 0f, 100f);
      ap[i] = Signed(a, 128f);
      bp[i] = Signed(b, 128f);
    }
    Emit(sink, "Lab", ("L", lp), ("a", ap), ("b", bp), w, h);
  }

  private static void AddOklab(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var lp = new byte[n]; var ap = new byte[n]; var bp = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (L, a, b) = Oklab.Project(rgba[p], rgba[p + 1], rgba[p + 2]);
      lp[i] = Unit(L);
      ap[i] = Signed(a, 0.4f);
      bp[i] = Signed(b, 0.4f);
    }
    Emit(sink, "Oklab", ("L", lp), ("a", ap), ("b", bp), w, h);
  }

  // ============================================================ Cylindrical

  private static void AddHsi(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var hb = new byte[n]; var sb = new byte[n]; var ib = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (H, S, I) = Cylindrical.Hsi(rgba[p], rgba[p + 1], rgba[p + 2]);
      hb[i] = Hue(H); sb[i] = Unit(S); ib[i] = Unit(I);
    }
    Emit(sink, "HSI", ("H", hb), ("S", sb), ("I", ib), w, h);
  }

  private static void AddHsv(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var hb = new byte[n]; var sb = new byte[n]; var vb = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (H, S, V) = Cylindrical.Hsv(rgba[p], rgba[p + 1], rgba[p + 2]);
      hb[i] = Hue(H); sb[i] = Unit(S); vb[i] = Unit(V);
    }
    Emit(sink, "HSV", ("H", hb), ("S", sb), ("V", vb), w, h);
  }

  private static void AddHwb(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var hb = new byte[n]; var wp = new byte[n]; var bp = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (H, W, B) = Cylindrical.Hwb(rgba[p], rgba[p + 1], rgba[p + 2]);
      hb[i] = Hue(H); wp[i] = Unit(W); bp[i] = Unit(B);
    }
    Emit(sink, "HWB", ("H", hb), ("W", wp), ("B", bp), w, h);
  }

  private static void AddLch(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var lp = new byte[n]; var cp = new byte[n]; var hp = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (L, C, hh) = Cylindrical.Lch(rgba[p], rgba[p + 1], rgba[p + 2]);
      lp[i] = Range(L, 0f, 100f);
      cp[i] = Range(C, 0f, 150f);
      hp[i] = Hue(hh);
    }
    Emit(sink, "LCH", ("L", lp), ("C", cp), ("h", hp), w, h);
  }

  private static void AddLchUv(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var lp = new byte[n]; var cp = new byte[n]; var hp = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (L, C, hh) = Cylindrical.LchUv(rgba[p], rgba[p + 1], rgba[p + 2]);
      lp[i] = Range(L, 0f, 100f);
      cp[i] = Range(C, 0f, 180f);
      hp[i] = Hue(hh);
    }
    Emit(sink, "LChUv", ("L", lp), ("C", cp), ("h", hp), w, h);
  }

  // ============================================================ Perceptual

  private static void AddLuv(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var lp = new byte[n]; var up = new byte[n]; var vp = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (L, u, v) = Perceptual.Luv(rgba[p], rgba[p + 1], rgba[p + 2]);
      lp[i] = Range(L, 0f, 100f);
      up[i] = Range(u, -83f, 175f);
      vp[i] = Range(v, -134f, 107f);
    }
    Emit(sink, "Luv", ("L", lp), ("u", up), ("v", vp), w, h);
  }

  private static void AddDin99(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var lp = new byte[n]; var ap = new byte[n]; var bp = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (L99, a99, b99) = Perceptual.Din99(rgba[p], rgba[p + 1], rgba[p + 2]);
      lp[i] = Range(L99, 0f, 100f);
      ap[i] = Signed(a99, 50f);
      bp[i] = Signed(b99, 50f);
    }
    Emit(sink, "Din99", ("L", lp), ("a", ap), ("b", bp), w, h);
  }

  private static void AddHunterLab(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var lp = new byte[n]; var ap = new byte[n]; var bp = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (L, a, b) = Perceptual.HunterLab(rgba[p], rgba[p + 1], rgba[p + 2]);
      lp[i] = Range(L, 0f, 100f);
      ap[i] = Signed(a, 128f);
      bp[i] = Signed(b, 128f);
    }
    Emit(sink, "HunterLab", ("L", lp), ("a", ap), ("b", bp), w, h);
  }

  private static void AddOkhsl(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var hb = new byte[n]; var sb = new byte[n]; var lb = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (H, S, L) = Perceptual.Okhsl(rgba[p], rgba[p + 1], rgba[p + 2]);
      hb[i] = Hue(H); sb[i] = Unit(S); lb[i] = Unit(L);
    }
    Emit(sink, "Okhsl", ("H", hb), ("S", sb), ("L", lb), w, h);
  }

  private static void AddOkhsv(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var hb = new byte[n]; var sb = new byte[n]; var vb = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (H, S, V) = Perceptual.Okhsv(rgba[p], rgba[p + 1], rgba[p + 2]);
      hb[i] = Hue(H); sb[i] = Unit(S); vb[i] = Unit(V);
    }
    Emit(sink, "Okhsv", ("H", hb), ("S", sb), ("V", vb), w, h);
  }

  private static void AddOklch(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var lp = new byte[n]; var cp = new byte[n]; var hp = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (L, C, hh) = Perceptual.Oklch(rgba[p], rgba[p + 1], rgba[p + 2]);
      lp[i] = Unit(L);
      cp[i] = Range(C, 0f, 0.4f);
      hp[i] = Hue(hh);
    }
    Emit(sink, "Oklch", ("L", lp), ("C", cp), ("h", hp), w, h);
  }

  // ============================================================ Yuv-family

  private static void AddYDbDr(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var y = new byte[n]; var db = new byte[n]; var dr = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (yf, dbf, drf) = Yuv.YDbDr(rgba[p], rgba[p + 1], rgba[p + 2]);
      y[i] = Unit(yf);
      db[i] = Signed(dbf, 1.333f);
      dr[i] = Signed(drf, 1.333f);
    }
    Emit(sink, "YDbDr", ("Y", y), ("Db", db), ("Dr", dr), w, h);
  }

  private static void AddYiq(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var y = new byte[n]; var ip = new byte[n]; var qp = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (yf, iif, qf) = Yuv.Yiq(rgba[p], rgba[p + 1], rgba[p + 2]);
      y[i] = Unit(yf);
      ip[i] = Signed(iif, 0.5957f);
      qp[i] = Signed(qf, 0.5226f);
    }
    Emit(sink, "YIQ", ("Y", y), ("I", ip), ("Q", qp), w, h);
  }

  // ============================================================ Wide-gamut

  private static void AddAcesCg(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n)
    => AddWideGamut(sink, rgba, w, h, n, "AcesCg", WideGamut.AcesCg);

  private static void AddAdobeRgb(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n)
    => AddWideGamut(sink, rgba, w, h, n, "AdobeRGB", WideGamut.AdobeRgb);

  private static void AddDisplayP3(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n)
    => AddWideGamut(sink, rgba, w, h, n, "DisplayP3", WideGamut.DisplayP3);

  private static void AddProPhotoRgb(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n)
    => AddWideGamut(sink, rgba, w, h, n, "ProPhotoRGB", WideGamut.ProPhotoRgb);

  private static void AddWideGamut(
    List<(string, byte[])> sink, byte[] rgba, int w, int h, int n, string folder,
    Func<byte, byte, byte, (float R, float G, float B)> proj
  ) {
    var rp = new byte[n]; var gp = new byte[n]; var bp = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (R, G, B) = proj(rgba[p], rgba[p + 1], rgba[p + 2]);
      rp[i] = Range(R, -0.25f, 1.25f);
      gp[i] = Range(G, -0.25f, 1.25f);
      bp[i] = Range(B, -0.25f, 1.25f);
    }
    Emit(sink, folder, ("R", rp), ("G", gp), ("B", bp), w, h);
  }

  // ============================================================ HDR

  private static void AddXyz(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var Xb = new byte[n]; var Yb = new byte[n]; var Zb = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (X, Y, Z) = Hdr.Xyz(rgba[p], rgba[p + 1], rgba[p + 2]);
      Xb[i] = Range(X, 0f, ColorSpaces.Lab.Xn);
      Yb[i] = Range(Y, 0f, ColorSpaces.Lab.Yn);
      Zb[i] = Range(Z, 0f, ColorSpaces.Lab.Zn);
    }
    Emit(sink, "XYZ", ("X", Xb), ("Y", Yb), ("Z", Zb), w, h);
  }

  private static void AddXyY(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var xb = new byte[n]; var yb = new byte[n]; var Yb = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (x, y, Y) = Hdr.XyY(rgba[p], rgba[p + 1], rgba[p + 2]);
      xb[i] = Range(x, 0f, 0.74f);
      yb[i] = Range(y, 0f, 0.74f);
      Yb[i] = Unit(Y);
    }
    Emit(sink, "XyY", ("x", xb), ("y", yb), ("Y", Yb), w, h);
  }

  private static void AddICtCp(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var Ib = new byte[n]; var Ctb = new byte[n]; var Cpb = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (I, Ct, Cp) = Hdr.ICtCp(rgba[p], rgba[p + 1], rgba[p + 2]);
      Ib[i] = Unit(I);
      Ctb[i] = Signed(Ct, 0.5f);
      Cpb[i] = Signed(Cp, 0.5f);
    }
    Emit(sink, "ICtCp", ("I", Ib), ("Ct", Ctb), ("Cp", Cpb), w, h);
  }

  private static void AddJzAzBz(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var Jb = new byte[n]; var ab = new byte[n]; var bb = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (Jz, az, bz) = Hdr.JzAzBz(rgba[p], rgba[p + 1], rgba[p + 2]);
      Jb[i] = Range(Jz, 0f, 0.17f);
      ab[i] = Signed(az, 0.05f);
      bb[i] = Signed(bz, 0.05f);
    }
    Emit(sink, "JzAzBz", ("Jz", Jb), ("az", ab), ("bz", bb), w, h);
  }

  private static void AddJzCzhz(List<(string, byte[])> sink, byte[] rgba, int w, int h, int n) {
    var Jb = new byte[n]; var Cb = new byte[n]; var hb = new byte[n];
    for (var i = 0; i < n; i++) {
      var p = i * 4;
      var (Jz, Cz, hz) = Hdr.JzCzhz(rgba[p], rgba[p + 1], rgba[p + 2]);
      Jb[i] = Range(Jz, 0f, 0.17f);
      Cb[i] = Range(Cz, 0f, 0.07f);
      hb[i] = Hue(hz);
    }
    Emit(sink, "JzCzhz", ("Jz", Jb), ("Cz", Cb), ("hz", hb), w, h);
  }

  // ============================================================ helpers

  /// <summary>Returns <c>true</c> if <paramref name="f"/> carries an alpha channel that <see cref="ExtractAlpha"/> can surface.</summary>
  public static bool HasAlphaChannel(PixelFormat f) => f is
    PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Argb32 or
    PixelFormat.Rgba64 or PixelFormat.GrayAlpha16;

  private static void Emit(List<(string, byte[])> sink, string folder,
    (string Name, byte[] Data) c1, (string Name, byte[] Data) c2, (string Name, byte[] Data) c3,
    int w, int h) {
    sink.Add(($"colorspace/{folder}/{c1.Name}.png", EncodeGray(c1.Data, w, h)));
    sink.Add(($"colorspace/{folder}/{c2.Name}.png", EncodeGray(c2.Data, w, h)));
    sink.Add(($"colorspace/{folder}/{c3.Name}.png", EncodeGray(c3.Data, w, h)));
  }

  private static void Emit4(List<(string, byte[])> sink, string folder,
    (string Name, byte[] Data) c1, (string Name, byte[] Data) c2,
    (string Name, byte[] Data) c3, (string Name, byte[] Data) c4,
    int w, int h) {
    sink.Add(($"colorspace/{folder}/{c1.Name}.png", EncodeGray(c1.Data, w, h)));
    sink.Add(($"colorspace/{folder}/{c2.Name}.png", EncodeGray(c2.Data, w, h)));
    sink.Add(($"colorspace/{folder}/{c3.Name}.png", EncodeGray(c3.Data, w, h)));
    sink.Add(($"colorspace/{folder}/{c4.Name}.png", EncodeGray(c4.Data, w, h)));
  }

  /// <summary>Encodes an 8-bit grayscale plane as a PNG via the PngCrushCS encoder.</summary>
  private static byte[] EncodeGray(byte[] plane, int width, int height) {
    var img = new RawImage {
      Width = width,
      Height = height,
      Format = PixelFormat.Gray8,
      PixelData = plane,
    };
    return PngWriter.ToBytes(PngFile.FromRawImage(img));
  }
}
