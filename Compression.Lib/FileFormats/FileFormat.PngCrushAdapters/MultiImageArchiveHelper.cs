#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Png;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Shared helpers for adapter descriptors that wrap any PngCrushCS multi-image
/// format (any T : <c>IMultiImageFileFormat&lt;T&gt;</c>) as a CompressionWorkbench
/// archive. Each adapter passes a delegate that knows how to read its specific
/// format from a stream and enumerate the contained <see cref="RawImage"/>s.
/// </summary>
/// <remarks>
/// Layout per frame folder <c>{prefix}_NNN_WxH_BBpp/</c>:
/// <list type="bullet">
///   <item><c>{prefix}_NNN.png</c> — the composite frame (PNG, RGBA-compatible).</item>
///   <item><c>Alpha.png</c> — colorspace-agnostic alpha plane, present only when the
///   source pixel format carries an alpha channel.</item>
///   <item><c>colorspace/&lt;Space&gt;/...</c> — per-component 8-bit grayscale
///   PNGs across 29 colorspaces (RGB, YCbCr, HSL, HSI, HSV, HWB, LCH, LChUv,
///   CMYK, Lab, Oklab, Luv, Din99, HunterLab, Okhsl, Okhsv, Oklch, YDbDr,
///   YIQ, AcesCg, AdobeRGB, DisplayP3, ProPhotoRGB, XYZ, XyY, ICtCp, JzAzBz,
///   JzCzhz). Alpha is intentionally NOT in this tree — it lives alongside
///   the composite frame because alpha is independent of colorspace.</item>
/// </list>
/// <para>
/// <b>Two source contracts</b>:
/// <list type="number">
///   <item>The legacy <c>Func&lt;Stream, IReadOnlyList&lt;RawImage&gt;&gt;</c>
///   path eagerly decodes all frames up front and is still used by APNG, TIFF,
///   MPO, GIF, MNG, FLI, DCX, ICNS — formats whose underlying readers don't
///   expose cheap header-only metadata, but whose decode is fast enough that
///   <c>List()</c> remains snappy.</item>
///   <item>The lazy <see cref="IFrameSource"/> path (used by JPEG) reads only
///   the SOF marker for <c>List()</c> and defers the libjpeg decode until
///   <c>Extract()</c> actually needs pixels. Required for JPEG because
///   libjpeg's full decode of a multi-megabyte JPEG runs for seconds and
///   was freezing the UI.</item>
/// </list>
/// </para>
/// </remarks>
public static class MultiImageArchiveHelper {

  // -------- Lazy IFrameSource path (JPEG and any future format whose decoder is too expensive to call from List). --------

  /// <summary>
  /// Lazy <c>List()</c>: pulls only header metadata via <see cref="IFrameSource.GetMetadata"/>.
  /// No pixel decode is triggered. This is the path JPEG must use.
  /// </summary>
  public static List<ArchiveEntryInfo> List(
    Stream stream, string prefix,
    Func<Stream, IFrameSource> openSource,
    ImageArchiveOptions? options = null
  ) {
    var opts = options ?? new ImageArchiveOptions();
    IFrameSource source;
    try { source = openSource(stream); }
    catch { return []; }

    var spaceComponentCount = 0;
    foreach (var entry in ColorSpaceCatalog.Enumerate(opts.Spaces))
      spaceComponentCount += entry.Components.Count;

    var frameCount = source.FrameCount;
    var result = new List<ArchiveEntryInfo>(frameCount * (1 + 1 + spaceComponentCount));
    var index = 0;
    for (var i = 0; i < frameCount; i++) {
      FrameMetadata meta;
      try { meta = source.GetMetadata(i); }
      catch { meta = new FrameMetadata(0, 0, 24, false); }
      var folder = BuildFrameFolder(prefix, i, meta.Width, meta.Height, meta.BitsPerPixel);
      var estimatedSize = ColorSpaceCatalog.EstimatePngBytes(meta.Width, meta.Height);

      var compositeName = $"{folder}/{prefix}_{i:D3}.png";
      result.Add(new ArchiveEntryInfo(index++, compositeName, estimatedSize, estimatedSize, "Stored", false, false, null));

      if (meta.HasAlpha) {
        var alphaName = $"{folder}/Alpha.png";
        result.Add(new ArchiveEntryInfo(index++, alphaName, estimatedSize, estimatedSize, "Stored", false, false, null));
      }

      foreach (var entry in ColorSpaceCatalog.Enumerate(opts.Spaces)) {
        foreach (var component in entry.Components) {
          var name = $"{folder}/colorspace/{entry.Folder}/{component}.png";
          result.Add(new ArchiveEntryInfo(index++, name, estimatedSize, estimatedSize, "Stored", false, false, null));
        }
      }
    }
    return result;
  }

  /// <summary>
  /// Lazy <c>Extract()</c>: decodes a frame at most once even if multiple
  /// components within that frame are requested. With <paramref name="files"/>
  /// non-null, frames not referenced by any filter entry are never decoded.
  /// </summary>
  public static void Extract(
    Stream stream, string outputDir, string[]? files,
    string prefix, Func<Stream, IFrameSource> openSource,
    ImageArchiveOptions? options = null
  ) {
    var opts = options ?? new ImageArchiveOptions();
    IFrameSource source;
    try { source = openSource(stream); }
    catch { return; }

    var frameCount = source.FrameCount;
    for (var i = 0; i < frameCount; i++) {
      FrameMetadata meta;
      try { meta = source.GetMetadata(i); }
      catch { meta = new FrameMetadata(0, 0, 24, false); }
      var folder = BuildFrameFolder(prefix, i, meta.Width, meta.Height, meta.BitsPerPixel);
      var compositeName = $"{folder}/{prefix}_{i:D3}.png";
      var alphaName = $"{folder}/Alpha.png";

      // Pre-scan: does ANY requested entry need pixels for this frame?
      var compositeRequested = files == null || MatchesFilter(compositeName, files);
      var alphaRequested = meta.HasAlpha && (files == null || MatchesFilter(alphaName, files));
      var requestedSpaces = ColorSpaceSet.None;
      foreach (var entry in ColorSpaceCatalog.Enumerate(opts.Spaces)) {
        var spaceFolder = $"{folder}/colorspace/{entry.Folder}/";
        var anyRequested = files == null;
        if (files != null) {
          foreach (var component in entry.Components) {
            if (MatchesFilter(spaceFolder + component + ".png", files)) {
              anyRequested = true;
              break;
            }
          }
        }
        if (anyRequested) requestedSpaces |= entry.Flag;
      }
      if (!compositeRequested && !alphaRequested && requestedSpaces == ColorSpaceSet.None)
        continue;

      // We're here only when at least one entry needs pixels — pay the decode cost once.
      RawImage img;
      try { img = source.GetFrame(i); }
      catch { continue; }

      if (compositeRequested) {
        var compatible = SupportsPngEncode(img.Format) ? img : PixelConverter.Convert(img, PixelFormat.Rgba32);
        try {
          var png = PngWriter.ToBytes(PngFile.FromRawImage(compatible));
          WriteFile(outputDir, compositeName, png);
        } catch { /* best-effort */ }
      }

      if (alphaRequested) {
        try {
          var alpha = ColorSpaceSplitter.ExtractAlpha(img);
          if (alpha is { } al) WriteFile(outputDir, alphaName, al.Data);
        } catch { /* best-effort */ }
      }

      foreach (var entry in ColorSpaceCatalog.Enumerate(opts.Spaces)) {
        if ((requestedSpaces & entry.Flag) == 0) continue;
        IReadOnlyList<(string Path, byte[] Data)> planes;
        try { planes = ColorSpaceSplitter.SplitOne(img, entry.Flag); }
        catch { continue; }
        foreach (var (path, data) in planes) {
          var name = $"{folder}/{path}";
          if (files != null && !MatchesFilter(name, files)) continue;
          WriteFile(outputDir, name, data);
        }
      }
    }
  }

  // -------- Eager Func<Stream, IReadOnlyList<RawImage>> path (legacy; kept for the other adapters). --------

  /// <summary>
  /// Eager <c>List()</c>: decodes all frames up front. Retained for adapter formats
  /// (APNG/TIFF/MPO/GIF/MNG/FLI/DCX/ICNS) whose decoders are fast enough to make
  /// the lazy contract not worth the per-format plumbing. The freeze fix only
  /// mattered for JPEG.
  /// </summary>
  public static List<ArchiveEntryInfo> List(
    Stream stream, string prefix,
    Func<Stream, IReadOnlyList<RawImage>> readImages,
    ImageArchiveOptions? options = null
  ) => List(stream, prefix, s => new EagerFrameSource(readImages(s)), options);

  /// <summary>Eager <c>Extract()</c>; counterpart to the eager <c>List()</c> above.</summary>
  public static void Extract(
    Stream stream, string outputDir, string[]? files,
    string prefix, Func<Stream, IReadOnlyList<RawImage>> readImages,
    ImageArchiveOptions? options = null
  ) => Extract(stream, outputDir, files, prefix, s => new EagerFrameSource(readImages(s)), options);

  /// <summary>
  /// Adapter that fronts an already-decoded <c>IReadOnlyList&lt;RawImage&gt;</c>
  /// as an <see cref="IFrameSource"/> so the eager call sites can route through
  /// the same body of <c>List</c>/<c>Extract</c> code without duplication.
  /// </summary>
  private sealed class EagerFrameSource(IReadOnlyList<RawImage> images) : IFrameSource {
    public int FrameCount => images.Count;
    public FrameMetadata GetMetadata(int frameIndex) {
      var img = images[frameIndex];
      var bpp = RawImage.BitsPerPixel(img.Format);
      return new FrameMetadata(img.Width, img.Height, bpp, ColorSpaceSplitter.HasAlphaChannel(img.Format));
    }
    public RawImage GetFrame(int frameIndex) => images[frameIndex];
  }

  private static bool SupportsPngEncode(PixelFormat f) => f is
    PixelFormat.Gray8 or PixelFormat.Gray16 or PixelFormat.GrayAlpha16 or
    PixelFormat.Rgb24 or PixelFormat.Rgb48 or PixelFormat.Rgba32 or PixelFormat.Rgba64 or
    PixelFormat.Indexed8 or PixelFormat.Indexed4 or PixelFormat.Indexed1;

  private static string BuildFrameFolder(string prefix, int index, int width, int height, int bpp)
    => $"{prefix}_{index:D3}_{width}x{height}_{bpp}bpp";

  /// <summary>
  /// Static-abstract dispatch wrapper: C# only lets you call <c>T.ToRawImages(file)</c>
  /// when T is a type parameter constrained to <see cref="IMultiImageFileFormat{T}"/>.
  /// Each per-format adapter calls this with its concrete type.
  /// </summary>
  public static IReadOnlyList<RawImage> ToRawImages<T>(T file) where T : IMultiImageFileFormat<T>
    => T.ToRawImages(file);
}
