#pragma warning disable CS1591
namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Single source of truth for the colorspace entry layout. Both the
/// <c>MultiImageArchiveHelper.List</c> overloads (lazy enumeration with
/// mathematical size estimation) and <see cref="ColorSpaceSplitter.SplitOne"/>
/// (on-demand computation during Extract) consult this table so the names
/// always agree.
/// </summary>
/// <remarks>
/// The catalog is keyed by the <see cref="ColorSpaceSet"/> single-flag value;
/// the helper iterates every set bit at <see cref="ColorSpaceSet.All"/> to enumerate
/// the full ~85-entry name set without doing any pixel work.
/// </remarks>
public static class ColorSpaceCatalog {

  /// <summary>One colorspace (folder + ordered component file names).</summary>
  public readonly record struct Entry(ColorSpaceSet Flag, string Folder, IReadOnlyList<string> Components);

  /// <summary>The full ordered table. Order mirrors <see cref="ColorSpaceSplitter.Split"/>.</summary>
  public static readonly IReadOnlyList<Entry> All = [
    // Wave A
    new(ColorSpaceSet.Rgb,         "RGB",         ["R", "G", "B"]),
    new(ColorSpaceSet.YCbCr,       "YCbCr",       ["Y", "Cb", "Cr"]),
    new(ColorSpaceSet.Hsl,         "HSL",         ["H", "S", "L"]),
    new(ColorSpaceSet.Cmyk,        "CMYK",        ["C", "M", "Y", "K"]),
    new(ColorSpaceSet.Lab,         "Lab",         ["L", "a", "b"]),
    new(ColorSpaceSet.Oklab,       "Oklab",       ["L", "a", "b"]),
    // Cylindrical
    new(ColorSpaceSet.Hsi,         "HSI",         ["H", "S", "I"]),
    new(ColorSpaceSet.Hsv,         "HSV",         ["H", "S", "V"]),
    new(ColorSpaceSet.Hwb,         "HWB",         ["H", "W", "B"]),
    new(ColorSpaceSet.Lch,         "LCH",         ["L", "C", "h"]),
    new(ColorSpaceSet.LchUv,       "LChUv",       ["L", "C", "h"]),
    // Perceptual
    new(ColorSpaceSet.Din99,       "Din99",       ["L", "a", "b"]),
    new(ColorSpaceSet.HunterLab,   "HunterLab",   ["L", "a", "b"]),
    new(ColorSpaceSet.Luv,         "Luv",         ["L", "u", "v"]),
    new(ColorSpaceSet.Okhsl,       "Okhsl",       ["H", "S", "L"]),
    new(ColorSpaceSet.Okhsv,       "Okhsv",       ["H", "S", "V"]),
    new(ColorSpaceSet.Oklch,       "Oklch",       ["L", "C", "h"]),
    // YUV-family
    new(ColorSpaceSet.YDbDr,       "YDbDr",       ["Y", "Db", "Dr"]),
    new(ColorSpaceSet.Yiq,         "YIQ",         ["Y", "I", "Q"]),
    // Wide-gamut
    new(ColorSpaceSet.AcesCg,      "AcesCg",      ["R", "G", "B"]),
    new(ColorSpaceSet.AdobeRgb,    "AdobeRGB",    ["R", "G", "B"]),
    new(ColorSpaceSet.DisplayP3,   "DisplayP3",   ["R", "G", "B"]),
    new(ColorSpaceSet.ProPhotoRgb, "ProPhotoRGB", ["R", "G", "B"]),
    // HDR
    new(ColorSpaceSet.Xyz,         "XYZ",         ["X", "Y", "Z"]),
    new(ColorSpaceSet.XyY,         "XyY",         ["x", "y", "Y"]),
    new(ColorSpaceSet.ICtCp,       "ICtCp",       ["I", "Ct", "Cp"]),
    new(ColorSpaceSet.JzAzBz,      "JzAzBz",      ["Jz", "az", "bz"]),
    new(ColorSpaceSet.JzCzhz,      "JzCzhz",      ["Jz", "Cz", "hz"]),
  ];

  /// <summary>Yields the catalog entries enabled by <paramref name="set"/>, in canonical order.</summary>
  public static IEnumerable<Entry> Enumerate(ColorSpaceSet set) {
    if (set == ColorSpaceSet.None) yield break;
    foreach (var e in All)
      if ((set & e.Flag) != 0)
        yield return e;
  }

  /// <summary>
  /// Estimates the on-disk size of an 8-bit grayscale W×H plane as PNG bytes.
  /// </summary>
  /// <remarks>
  /// PNG signature (8) + IHDR (25) + IDAT chunk overhead (12) + the raw deflate
  /// payload bound for non-compressed Gray8 (≈ <c>W*H + H</c> filter bytes plus
  /// a small zlib/deflate framing margin) + IEND (12). We add a generous 1024-byte
  /// fudge to cover the chunk lengths, CRCs, deflate stored-block headers and any
  /// minor encoder overhead. The UI tolerates a small overestimate.
  /// </remarks>
  public static long EstimatePngBytes(int width, int height)
    => (long)width * height + 1024L;
}
