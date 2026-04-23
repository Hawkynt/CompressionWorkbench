#pragma warning disable CS1591
using System.Text;

namespace FileFormat.JpegArchive;

/// <summary>
/// High-level JPEG metadata editor. Reads the APP1 EXIF segment (if any),
/// applies a patch to the TIFF tag tree, and writes the updated EXIF back
/// plus optional XMP in a single pass. Image pixel data is byte-for-byte
/// preserved — only APP1 segments are touched.
/// </summary>
public static class JpegMetadataEditor {
  private static readonly byte[] ExifHeader = Encoding.ASCII.GetBytes("Exif\0\0");

  /// <summary>
  /// Applies <paramref name="patch"/> to the EXIF IFD structure inside
  /// <paramref name="jpegBytes"/> and returns the modified JPEG. A missing
  /// EXIF segment is created; existing unknown tags are preserved.
  /// </summary>
  public static byte[] ApplyExifPatch(ReadOnlySpan<byte> jpegBytes, ExifPatch patch) {
    ArgumentNullException.ThrowIfNull(patch);

    var tiff = ExtractTiffOrCreate(jpegBytes);
    var gpsIfd = GetOrCreateSubIfd(tiff.Ifd0, TiffTags.GpsSubIfdPointer);
    var littleEndian = tiff.LittleEndian;

    if (patch.Gps is { } gps) {
      gpsIfd.SetEntry(ExifValueEncoding.Ascii(TiffTags.GpsLatitudeRef, gps.Latitude >= 0 ? "N" : "S"));
      gpsIfd.SetEntry(ExifValueEncoding.GpsCoordinate(TiffTags.GpsLatitude, Math.Abs(gps.Latitude), littleEndian));
      gpsIfd.SetEntry(ExifValueEncoding.Ascii(TiffTags.GpsLongitudeRef, gps.Longitude >= 0 ? "E" : "W"));
      gpsIfd.SetEntry(ExifValueEncoding.GpsCoordinate(TiffTags.GpsLongitude, Math.Abs(gps.Longitude), littleEndian));
      if (gps.AltitudeMeters is { } alt) {
        gpsIfd.SetEntry(ExifValueEncoding.Byte(TiffTags.GpsAltitudeRef, alt >= 0 ? (byte)0 : (byte)1));
        gpsIfd.SetEntry(ExifValueEncoding.Rational(TiffTags.GpsAltitude, Math.Abs(alt), littleEndian, denominator: 100));
      }
    }

    if (patch.ImageDirectionDegrees is { } direction) {
      gpsIfd.SetEntry(ExifValueEncoding.Ascii(
        TiffTags.GpsImgDirectionRef,
        patch.ImageDirectionIsMagnetic ? "M" : "T"));
      gpsIfd.SetEntry(ExifValueEncoding.Rational(TiffTags.GpsImgDirection, direction, littleEndian));
    }

    if (patch.TargetGps is { } target) {
      gpsIfd.SetEntry(ExifValueEncoding.Ascii(TiffTags.GpsDestLatitudeRef, target.Latitude >= 0 ? "N" : "S"));
      gpsIfd.SetEntry(ExifValueEncoding.GpsCoordinate(TiffTags.GpsDestLatitude, Math.Abs(target.Latitude), littleEndian));
      gpsIfd.SetEntry(ExifValueEncoding.Ascii(TiffTags.GpsDestLongitudeRef, target.Longitude >= 0 ? "E" : "W"));
      gpsIfd.SetEntry(ExifValueEncoding.GpsCoordinate(TiffTags.GpsDestLongitude, Math.Abs(target.Longitude), littleEndian));
    }

    if (patch.ImageDescription is { } description)
      tiff.Ifd0.SetEntry(ExifValueEncoding.Ascii(TiffTags.ImageDescription, description));

    // Ensure the sub-IFD pointer entry exists so TiffWriter updates its offset
    // correctly. TiffWriter rewrites the value slot from SubIfds placement.
    if (tiff.Ifd0.FindEntry(TiffTags.GpsSubIfdPointer) == null)
      tiff.Ifd0.SetEntry(new TiffEntry(TiffTags.GpsSubIfdPointer, TiffFieldType.Long, 1, new byte[4]));

    var newTiffBytes = TiffWriter.Serialize(tiff);
    var newApp1Payload = ConcatenateExifPayload(newTiffBytes);
    return JpegSegmentSurgery.ReplaceExifSegment(jpegBytes, newApp1Payload);
  }

  /// <summary>
  /// Reads the APP1 EXIF segment (if any) and parses its TIFF area, or
  /// returns a fresh little-endian empty TIFF image if the JPEG doesn't
  /// have EXIF yet.
  /// </summary>
  public static TiffImage ExtractTiffOrCreate(ReadOnlySpan<byte> jpegBytes) {
    var exifBytes = JpegSegmentSurgery.TryReadExifSegment(jpegBytes);
    if (exifBytes != null) {
      try {
        return TiffReader.Parse(exifBytes);
      } catch (InvalidDataException) {
        // Malformed EXIF — start fresh rather than refuse to write.
      }
    }

    return new TiffImage { LittleEndian = true, Ifd0 = new TiffIfd() };
  }

  private static TiffIfd GetOrCreateSubIfd(TiffIfd parent, ushort pointerTag) {
    if (!parent.SubIfds.TryGetValue(pointerTag, out var sub)) {
      sub = new TiffIfd();
      parent.SubIfds[pointerTag] = sub;
    }
    return sub;
  }

  private static byte[] ConcatenateExifPayload(byte[] tiffBytes) {
    var payload = new byte[ExifHeader.Length + tiffBytes.Length];
    Array.Copy(ExifHeader, payload, ExifHeader.Length);
    Array.Copy(tiffBytes, 0, payload, ExifHeader.Length, tiffBytes.Length);
    return payload;
  }
}

/// <summary>
/// Set of metadata fields to write into a JPEG's APP1 EXIF segment. Null
/// fields are left unchanged; setting a field overwrites whatever was there.
/// </summary>
public sealed class ExifPatch {
  public GpsPoint? Gps { get; init; }
  public GpsPoint? TargetGps { get; init; }
  public double? ImageDirectionDegrees { get; init; }
  public bool ImageDirectionIsMagnetic { get; init; }
  public string? ImageDescription { get; init; }
}

/// <summary>
/// Plain point struct used for EXIF patching to avoid a cross-cutting
/// dependency on PhotoManager.Core types inside this library.
/// </summary>
public readonly record struct GpsPoint(double Latitude, double Longitude, double? AltitudeMeters = null);
