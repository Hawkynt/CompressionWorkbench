#pragma warning disable CS1591
using System.Text;

namespace FileFormat.JpegArchive;

/// <summary>
/// High-level editor for standalone TIFF files. A TIFF file's byte layout
/// IS a TIFF area, so the existing <see cref="TiffReader"/> + <see cref="TiffWriter"/>
/// can operate on it directly. XMP in TIFF lives in tag 0x02BC (XMLPacket)
/// on IFD0 as a BYTE/UNDEFINED array.
/// </summary>
public static class TiffMetadataEditor {
  public const ushort XmpPacketTag = 0x02BC;

  /// <summary>
  /// Replaces or inserts the XMP packet in a TIFF's IFD0 (tag 0x02BC).
  /// Image data is preserved because <see cref="TiffWriter"/> copies every
  /// unrelated tag byte-for-byte.
  /// </summary>
  public static byte[] ReplaceXmpPacket(ReadOnlySpan<byte> tiffBytes, byte[] xmpBytes) {
    ArgumentNullException.ThrowIfNull(xmpBytes);

    var image = TiffReader.Parse(tiffBytes);
    image.Ifd0.SetEntry(new TiffEntry(
      XmpPacketTag, TiffFieldType.Byte, (uint)xmpBytes.Length, xmpBytes
    ));

    return TiffWriter.Serialize(image);
  }

  /// <summary>
  /// Applies an <see cref="ExifPatch"/> plus an XMP payload to a TIFF file.
  /// </summary>
  public static byte[] ApplyPatch(ReadOnlySpan<byte> tiffBytes, ExifPatch exifPatch, byte[] xmpBytes) {
    ArgumentNullException.ThrowIfNull(exifPatch);
    ArgumentNullException.ThrowIfNull(xmpBytes);

    // Run the EXIF tag updates through the same JPEG helper by giving it
    // the bare TIFF bytes (JpegMetadataEditor does that for us internally
    // after stripping the "Exif\0\0" JPEG header). For a TIFF file there's
    // no header to strip, so we parse/patch/serialize directly.
    var image = TiffReader.Parse(tiffBytes);

    var littleEndian = image.LittleEndian;
    var gps = image.Ifd0.SubIfds.TryGetValue(TiffTags.GpsSubIfdPointer, out var existingGps)
      ? existingGps
      : new TiffIfd();
    image.Ifd0.SubIfds[TiffTags.GpsSubIfdPointer] = gps;

    if (exifPatch.Gps is { } coord) {
      gps.SetEntry(ExifValueEncoding.Ascii(TiffTags.GpsLatitudeRef, coord.Latitude >= 0 ? "N" : "S"));
      gps.SetEntry(ExifValueEncoding.GpsCoordinate(TiffTags.GpsLatitude, Math.Abs(coord.Latitude), littleEndian));
      gps.SetEntry(ExifValueEncoding.Ascii(TiffTags.GpsLongitudeRef, coord.Longitude >= 0 ? "E" : "W"));
      gps.SetEntry(ExifValueEncoding.GpsCoordinate(TiffTags.GpsLongitude, Math.Abs(coord.Longitude), littleEndian));
      if (coord.AltitudeMeters is { } alt) {
        gps.SetEntry(ExifValueEncoding.Byte(TiffTags.GpsAltitudeRef, alt >= 0 ? (byte)0 : (byte)1));
        gps.SetEntry(ExifValueEncoding.Rational(TiffTags.GpsAltitude, Math.Abs(alt), littleEndian, denominator: 100));
      }
    }

    if (exifPatch.ImageDirectionDegrees is { } direction) {
      gps.SetEntry(ExifValueEncoding.Ascii(
        TiffTags.GpsImgDirectionRef,
        exifPatch.ImageDirectionIsMagnetic ? "M" : "T"));
      gps.SetEntry(ExifValueEncoding.Rational(TiffTags.GpsImgDirection, direction, littleEndian));
    }

    if (exifPatch.ImageDescription is { } description)
      image.Ifd0.SetEntry(ExifValueEncoding.Ascii(TiffTags.ImageDescription, description));

    // GPS sub-IFD pointer entry needs to exist for the writer to emit it.
    if (image.Ifd0.FindEntry(TiffTags.GpsSubIfdPointer) == null)
      image.Ifd0.SetEntry(new TiffEntry(TiffTags.GpsSubIfdPointer, TiffFieldType.Long, 1, new byte[4]));

    image.Ifd0.SetEntry(new TiffEntry(XmpPacketTag, TiffFieldType.Byte, (uint)xmpBytes.Length, xmpBytes));

    return TiffWriter.Serialize(image);
  }

  public static byte[]? TryReadXmpPacket(ReadOnlySpan<byte> tiffBytes) {
    try {
      var image = TiffReader.Parse(tiffBytes);
      var entry = image.Ifd0.FindEntry(XmpPacketTag);
      return entry?.ValueBytes;
    } catch (InvalidDataException) {
      return null;
    }
  }
}
