extern alias images;

using images::FileFormat.Core;
using images::Hawkynt.FileFormats.Images;
using NativeImageDecoder = Hawkynt.NativeForms.Drawing.ImageDecoder;

namespace Compression.NativeUI.Controls;

/// <summary>A decoded picture: one frame per entry, all the same size, with per-frame delays.</summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Frames">Straight-ARGB pixels, one buffer per frame.</param>
/// <param name="DelaysMilliseconds">Per-frame delay; all zero for a still.</param>
/// <param name="FormatName">What the bytes were recognised as, for the window title.</param>
internal sealed record DecodedPicture(
  int Width,
  int Height,
  IReadOnlyList<int[]> Frames,
  IReadOnlyList<int> DelaysMilliseconds,
  string FormatName
) {
  public bool IsAnimated => this.Frames.Count > 1 && this.DelaysMilliseconds.Any(d => d > 0);
}

/// <summary>
/// Decodes preview bytes as a picture, in two tiers.
/// <para>
/// NativeForms goes first for the handful of formats it reads, because it is the only one of the
/// two that returns every frame of an animation along with its timing. Everything else falls to
/// <c>Hawkynt.FileFormats.Images</c>, which reads several hundred formats but one frame at a time —
/// so a TIFF, a WebP, a TGA, a DDS or a PCX still previews, as a still.
/// </para>
/// </summary>
internal static class PreviewImageDecoder {
  /// <summary>
  /// Decodes <paramref name="data"/>, or returns false when nothing recognises it. Never throws:
  /// a corrupt file is a fall-through to the text or hex view, not an error.
  /// </summary>
  /// <param name="data">The bytes to decode.</param>
  /// <param name="entryName">
  /// The entry's name, when one is known. Many of the older image formats carry no magic bytes at
  /// all — an Atari or BBC Micro screen dump is raw pixels from byte zero — so the extension is the
  /// only thing that identifies them.
  /// </param>
  /// <param name="picture">The decoded frames.</param>
  public static bool TryDecode(byte[] data, string? entryName, out DecodedPicture picture) {
    picture = null!;
    if (data.Length < 4) return false;

    return TryDecodeAnimated(data, out picture)
        || TryDecodeStill(data, entryName, out picture);
  }

  /// <summary>
  /// The NativeForms tier. Its decoder throws on anything it does not know, so the signature is
  /// checked first rather than used as a probe.
  /// </summary>
  private static bool TryDecodeAnimated(byte[] data, out DecodedPicture picture) {
    picture = null!;
    if (NativeSignature(data) is not { } formatName) return false;

    try {
      var decoded = NativeImageDecoder.Decode(data);
      if (decoded.Frames.Count == 0) return false;

      picture = new(
        decoded.Width,
        decoded.Height,
        [.. decoded.Frames.Select(f => f.Argb)],
        [.. decoded.Frames.Select(f => f.DelayMilliseconds)],
        formatName);
      return true;
    } catch {
      // A truncated or malformed file of a known type still gets the second tier's opinion.
      return false;
    }
  }

  /// <summary>The broad tier: several hundred readers, one frame each.</summary>
  private static bool TryDecodeStill(byte[] data, string? entryName, out DecodedPicture picture) {
    picture = null!;

    if (DetectFromBytes(data) is { } byBytes && TryRead(() => FormatRegistry.Read(data), byBytes, out picture))
      return true;

    // Nothing in the header identified it. Many of the older formats have no header at all, so the
    // extension is the only thing that names them — and the reader that takes the extension into
    // account is the one that reads a file, so the bytes go through a temporary one.
    return TryDecodeByExtension(data, entryName, out picture);
  }

  private static bool TryDecodeByExtension(byte[] data, string? entryName, out DecodedPicture picture) {
    picture = null!;
    if (string.IsNullOrEmpty(entryName)) return false;

    var extension = Path.GetExtension(entryName);
    if (string.IsNullOrEmpty(extension)) return false;

    ImageFormat format;
    try {
      format = FormatRegistry.DetectFromExtension(extension);
    } catch {
      return false;
    }
    if (format == ImageFormat.Unknown) return false;

    var path = Path.Combine(Path.GetTempPath(), $"cwb_preview_{Guid.NewGuid():N}{extension}");
    try {
      File.WriteAllBytes(path, data);
      return TryRead(() => FormatRegistry.Read(new FileInfo(path)), format, out picture);
    } catch {
      return false;
    } finally {
      try {
        File.Delete(path);
      } catch {
        // A leftover in the temp directory is not worth failing a preview over.
      }
    }
  }

  private static ImageFormat? DetectFromBytes(byte[] data) {
    try {
      var format = FormatRegistry.DetectFromBytes(data);
      return format == ImageFormat.Unknown ? null : format;
    } catch {
      // A detector that throws on short or odd input is treated as no answer.
      return null;
    }
  }

  private static bool TryRead(Func<RawImage?> read, ImageFormat format, out DecodedPicture picture) {
    picture = null!;

    try {
      if (read() is not { } raw || raw.Width <= 0 || raw.Height <= 0) return false;
      picture = new(raw.Width, raw.Height, [ToArgb(raw)], [0], format.ToString());
      return true;
    } catch {
      return false;
    }
  }

  /// <summary>
  /// BGRA byte order and straight-ARGB <see cref="int"/> are the same four bytes on a
  /// little-endian machine, so the normalised buffer copies straight across.
  /// </summary>
  private static int[] ToArgb(RawImage raw) {
    var bgra = raw.ToBgra32();
    var pixels = new int[raw.Width * raw.Height];

    for (var i = 0; i < pixels.Length; ++i) {
      var o = i * 4;
      pixels[i] = unchecked((int)(
        ((uint)bgra[o + 3] << 24) |
        ((uint)bgra[o + 2] << 16) |
        ((uint)bgra[o + 1] << 8) |
        bgra[o]));
    }

    return pixels;
  }

  /// <summary>Names the format when NativeForms is the one that should read it, else null.</summary>
  private static string? NativeSignature(ReadOnlySpan<byte> data) {
    ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    if (data.Length >= 8 && data[..8].SequenceEqual(png)) return "PNG";
    if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "JPEG";
    if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38) return "GIF";
    if (data[0] == 0x42 && data[1] == 0x4D) return "BMP";

    // ICO and CUR share a header that differs only in the type word.
    if (data[0] == 0x00 && data[1] == 0x00 && data[3] == 0x00)
      return data[2] switch { 0x01 => "ICO", 0x02 => "CUR", _ => null };

    return null;
  }
}
