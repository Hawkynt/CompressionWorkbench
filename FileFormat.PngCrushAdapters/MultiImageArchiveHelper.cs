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
internal static class MultiImageArchiveHelper {
  /// <summary>
  /// Reads <paramref name="stream"/> via <paramref name="readImages"/> and exposes
  /// each <see cref="RawImage"/> as an entry. Names follow the pattern
  /// <c>{prefix}_NNN[_WxH][_BBPP].png</c>.
  /// </summary>
  public static List<ArchiveEntryInfo> List(Stream stream, string prefix, Func<Stream, IReadOnlyList<RawImage>> readImages) {
    var images = readImages(stream);
    var result = new List<ArchiveEntryInfo>(images.Count);
    for (var i = 0; i < images.Count; i++) {
      var name = BuildEntryName(prefix, i, images[i]);
      // Size reported is the in-memory pixel-data size, not the eventual PNG. The
      // CLI's ratio column degenerates to "100%" but listings are still informative.
      var size = (long)images[i].PixelData.Length;
      result.Add(new ArchiveEntryInfo(i, name, size, size, "Stored", false, false, null));
    }
    return result;
  }

  public static void Extract(Stream stream, string outputDir, string[]? files,
                             string prefix, Func<Stream, IReadOnlyList<RawImage>> readImages) {
    var images = readImages(stream);
    for (var i = 0; i < images.Count; i++) {
      var name = BuildEntryName(prefix, i, images[i]);
      if (files != null && !MatchesFilter(name, files)) continue;
      // Many readers return Bgra32 / Rgb24 / paletted formats that PngFile.FromRawImage
      // doesn't natively accept; convert to Rgba32 (a PNG-supported format) first.
      var compatible = SupportsPngEncode(images[i].Format) ? images[i] : PixelConverter.Convert(images[i], PixelFormat.Rgba32);
      var png = PngWriter.ToBytes(PngFile.FromRawImage(compatible));
      WriteFile(outputDir, name, png);
    }
  }

  private static bool SupportsPngEncode(PixelFormat f) => f is
    PixelFormat.Gray8 or PixelFormat.Gray16 or PixelFormat.GrayAlpha16 or
    PixelFormat.Rgb24 or PixelFormat.Rgb48 or PixelFormat.Rgba32 or PixelFormat.Rgba64 or
    PixelFormat.Indexed8 or PixelFormat.Indexed4 or PixelFormat.Indexed1;

  private static string BuildEntryName(string prefix, int index, RawImage img) {
    var bpp = RawImage.BitsPerPixel(img.Format);
    return $"{prefix}_{index:D3}_{img.Width}x{img.Height}_{bpp}bpp.png";
  }

  /// <summary>
  /// Static-abstract dispatch wrapper: C# only lets you call <c>T.ToRawImages(file)</c>
  /// when T is a type parameter constrained to <see cref="IMultiImageFileFormat{T}"/>.
  /// Each per-format adapter calls this with its concrete type.
  /// </summary>
  public static IReadOnlyList<RawImage> ToRawImages<T>(T file) where T : IMultiImageFileFormat<T>
    => T.ToRawImages(file);
}
