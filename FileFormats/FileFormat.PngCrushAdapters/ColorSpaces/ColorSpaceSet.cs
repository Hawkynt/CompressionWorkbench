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
  /// <summary>
  /// Specifies that no option is selected.
  /// </summary>
  None = 0,

  // Existing (Wave A)
  /// <summary>
  /// Specifies the rgb option.
  /// </summary>
  Rgb = 1L << 0,
  /// <summary>
  /// Specifies the y cb cr option.
  /// </summary>
  YCbCr = 1L << 1,
  /// <summary>
  /// Specifies the hsl option.
  /// </summary>
  Hsl = 1L << 2,
  /// <summary>
  /// Specifies the cmyk option.
  /// </summary>
  Cmyk = 1L << 3,
  /// <summary>
  /// Specifies the lab option.
  /// </summary>
  Lab = 1L << 4,
  /// <summary>
  /// Specifies the oklab option.
  /// </summary>
  Oklab = 1L << 5,

  // Cylindrical
  /// <summary>
  /// Specifies the hsi option.
  /// </summary>
  Hsi = 1L << 6,
  /// <summary>
  /// Specifies the hsv option.
  /// </summary>
  Hsv = 1L << 7,
  /// <summary>
  /// Specifies the hwb option.
  /// </summary>
  Hwb = 1L << 8,
  /// <summary>
  /// Specifies the lch option.
  /// </summary>
  Lch = 1L << 9,
  /// <summary>
  /// Specifies the lch uv option.
  /// </summary>
  LchUv = 1L << 10,

  // Perceptual
  /// <summary>
  /// Specifies the din 99 option.
  /// </summary>
  Din99 = 1L << 11,
  /// <summary>
  /// Specifies the hunter lab option.
  /// </summary>
  HunterLab = 1L << 12,
  /// <summary>
  /// Specifies the luv option.
  /// </summary>
  Luv = 1L << 13,
  /// <summary>
  /// Specifies the okhsl option.
  /// </summary>
  Okhsl = 1L << 14,
  /// <summary>
  /// Specifies the okhsv option.
  /// </summary>
  Okhsv = 1L << 15,
  /// <summary>
  /// Specifies the oklch option.
  /// </summary>
  Oklch = 1L << 16,

  // YUV-family (in addition to the existing YCbCr)
  /// <summary>
  /// Specifies the y db dr option.
  /// </summary>
  YDbDr = 1L << 17,
  /// <summary>
  /// Specifies the yiq option.
  /// </summary>
  Yiq = 1L << 18,

  // Wide-gamut RGB primaries variants
  /// <summary>
  /// Specifies the aces cg option.
  /// </summary>
  AcesCg = 1L << 19,
  /// <summary>
  /// Specifies the adobe rgb option.
  /// </summary>
  AdobeRgb = 1L << 20,
  /// <summary>
  /// Specifies the display p 3 option.
  /// </summary>
  DisplayP3 = 1L << 21,
  /// <summary>
  /// Specifies the pro photo rgb option.
  /// </summary>
  ProPhotoRgb = 1L << 22,

  // HDR / canonical CIE
  /// <summary>
  /// Specifies the xyz option.
  /// </summary>
  Xyz = 1L << 23,
  /// <summary>
  /// Specifies the xy y option.
  /// </summary>
  XyY = 1L << 24,
  /// <summary>
  /// Specifies the i ct cp option.
  /// </summary>
  ICtCp = 1L << 25,
  /// <summary>
  /// Specifies the jz az bz option.
  /// </summary>
  JzAzBz = 1L << 26,
  /// <summary>
  /// Specifies the jz czhz option.
  /// </summary>
  JzCzhz = 1L << 27,

  /// <summary>
  /// Specifies the all option.
  /// </summary>
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
