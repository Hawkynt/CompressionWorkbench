#pragma warning disable CS1591
namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Set of colorspaces the splitter can emit. <see cref="All"/> = every
/// implemented projector (29 spaces), which is the user-preferred default.
/// </summary>
/// <remarks>
/// Munsell is intentionally absent: an authoritative renotation table is
/// required (the sibling repo ships a 4000-entry .dat file). Without that
/// table any "Munsell" output would be a fabrication, so the projector is
/// declined here and surfaced as a metadata.ini note in affected formats.
/// </remarks>
[Flags]
public enum ColorSpaceSet : long {
  None = 0,

  // Existing (Wave A)
  Rgb = 1L << 0,
  YCbCr = 1L << 1,
  Hsl = 1L << 2,
  Cmyk = 1L << 3,
  Lab = 1L << 4,
  Oklab = 1L << 5,

  // Cylindrical
  Hsi = 1L << 6,
  Hsv = 1L << 7,
  Hwb = 1L << 8,
  Lch = 1L << 9,
  LchUv = 1L << 10,

  // Perceptual
  Din99 = 1L << 11,
  HunterLab = 1L << 12,
  Luv = 1L << 13,
  Okhsl = 1L << 14,
  Okhsv = 1L << 15,
  Oklch = 1L << 16,

  // YUV-family (in addition to the existing YCbCr)
  YDbDr = 1L << 17,
  Yiq = 1L << 18,

  // Wide-gamut RGB primaries variants
  AcesCg = 1L << 19,
  AdobeRgb = 1L << 20,
  DisplayP3 = 1L << 21,
  ProPhotoRgb = 1L << 22,

  // HDR / canonical CIE
  Xyz = 1L << 23,
  XyY = 1L << 24,
  ICtCp = 1L << 25,
  JzAzBz = 1L << 26,
  JzCzhz = 1L << 27,

  All = Rgb | YCbCr | Hsl | Cmyk | Lab | Oklab |
        Hsi | Hsv | Hwb | Lch | LchUv |
        Din99 | HunterLab | Luv | Okhsl | Okhsv | Oklch |
        YDbDr | Yiq |
        AcesCg | AdobeRgb | DisplayP3 | ProPhotoRgb |
        Xyz | XyY | ICtCp | JzAzBz | JzCzhz,
}

/// <summary>
/// Optional knobs for image-archive helpers that emit one entry per frame
/// plus per-frame colorspace breakdowns.
/// </summary>
public sealed record ImageArchiveOptions(ColorSpaceSet Spaces = ColorSpaceSet.All);
