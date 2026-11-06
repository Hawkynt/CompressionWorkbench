#pragma warning disable CS1591
using ImagesIco = FileFormat.Ico;

namespace CompressionWorkbench.FileFormat.Ico;

/// <summary>
/// Writes Windows ICO bundles. Inputs are individual image files (PNG or BMP). PNG
/// payloads are stored verbatim (Vista+ supports embedded PNG in ICO). BMP payloads
/// are converted back to icon-style DIBs: BITMAPFILEHEADER stripped, biHeight
/// doubled, and a zero AND-mask appended so legacy parsers stay happy.
/// </summary>
/// <remarks>
/// The conversion and the directory assembly are
/// <see cref="ImagesIco.IcoPayload.Encode"/> and
/// <see cref="ImagesIco.IcoWriter.Assemble"/> in Hawkynt.FileFormats.Images. What stays here is
/// the container's own rules: that an input is a whole image file, and how many of them a bundle
/// may hold.
/// </remarks>
public sealed class IcoWriter {

  /// <summary>Single image to embed.</summary>
  public sealed record Image(byte[] Data);

  /// <summary>
  /// Performs the build ico operation.
  /// </summary>
  public static byte[] BuildIco(IReadOnlyList<Image> images) => Build(images, isCursor: false);

  /// <summary>
  /// Performs the build cur operation.
  /// </summary>
  public static byte[] BuildCur(IReadOnlyList<Image> images) => Build(images, isCursor: true);

  /// <summary>
  /// Encodes one whole image file into the payload an entry carries, with the size and depth taken
  /// from it.
  /// </summary>
  /// <remarks>
  /// Exposed so the in-place modifier emits the same encoding a from-scratch bundle would, without
  /// reaching past this bridge. It used to hold a second copy of the conversion for that.
  /// </remarks>
  /// <exception cref="ArgumentException">The input is neither a PNG nor a BMP file.</exception>
  public static (byte[] Payload, int Width, int Height, int BitsPerPixel, bool IsPng) EncodePayload(byte[] imageFile) {
    ArgumentNullException.ThrowIfNull(imageFile);
    var (payload, width, height, bitsPerPixel, format) = _Encode(imageFile);
    return (payload, width, height, bitsPerPixel, format == ImagesIco.IcoImageFormat.Png);
  }

  private static byte[] Build(IReadOnlyList<Image> images, bool isCursor) {
    if (images.Count == 0) throw new ArgumentException("ICO: at least one image required", nameof(images));
    if (images.Count > ushort.MaxValue) throw new ArgumentException("ICO: too many images (>65535)", nameof(images));

    var entries = new ImagesIco.IconBundleEntry[images.Count];
    for (var i = 0; i < images.Count; i++) {
      var (payload, width, height, bitsPerPixel, format) = _Encode(images[i].Data);
      // Hotspots are (0, 0): a raw image says nothing about where a pointer should point, and the
      // caller can patch them afterwards.
      entries[i] = new ImagesIco.IconBundleEntry(i, width, height, bitsPerPixel, 0, 0, format, payload);
    }

    return ImagesIco.IcoWriter.Assemble(
      isCursor ? ImagesIco.IcoFileType.Cursor : ImagesIco.IcoFileType.Icon,
      entries);
  }

  /// <summary>Restates the codec's refusal in this container's words.</summary>
  private static (byte[] Payload, int Width, int Height, int BitsPerPixel, ImagesIco.IcoImageFormat Format) _Encode(byte[] source) {
    try {
      return ImagesIco.IcoPayload.Encode(source);
    } catch (ArgumentException) {
      throw new ArgumentException("ICO: input is neither a PNG nor a BMP file");
    }
  }
}
