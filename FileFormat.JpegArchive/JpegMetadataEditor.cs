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

    // Date/time tags — DateTime in IFD0, the "Original" + "Digitized" pair
    // in EXIF SubIFD. Format is JEITA's "yyyy:MM:dd HH:mm:ss". Patching any
    // of the SubIFD dates makes us materialise the SubIFD pointer below.
    if (patch.DateTimeModified is { } modified)
      tiff.Ifd0.SetEntry(ExifValueEncoding.Ascii(TiffTags.DateTime, FormatExifDate(modified)));
    if (patch.DateTimeOriginal is { } original) {
      var subIfd = GetOrCreateSubIfd(tiff.Ifd0, TiffTags.ExifSubIfdPointer);
      subIfd.SetEntry(ExifValueEncoding.Ascii(TiffTags.DateTimeOriginal, FormatExifDate(original)));
    }
    if (patch.DateTimeDigitized is { } digitized) {
      var subIfd = GetOrCreateSubIfd(tiff.Ifd0, TiffTags.ExifSubIfdPointer);
      subIfd.SetEntry(ExifValueEncoding.Ascii(TiffTags.DateTimeDigitized, FormatExifDate(digitized)));
    }

    // Ensure the sub-IFD pointer entry exists so TiffWriter updates its offset
    // correctly. TiffWriter rewrites the value slot from SubIfds placement.
    if (tiff.Ifd0.FindEntry(TiffTags.GpsSubIfdPointer) == null)
      tiff.Ifd0.SetEntry(new TiffEntry(TiffTags.GpsSubIfdPointer, TiffFieldType.Long, 1, new byte[4]));
    if (tiff.Ifd0.SubIfds.ContainsKey(TiffTags.ExifSubIfdPointer)
        && tiff.Ifd0.FindEntry(TiffTags.ExifSubIfdPointer) == null)
      tiff.Ifd0.SetEntry(new TiffEntry(TiffTags.ExifSubIfdPointer, TiffFieldType.Long, 1, new byte[4]));

    var newTiffBytes = TiffWriter.Serialize(tiff);
    var newApp1Payload = ConcatenateExifPayload(newTiffBytes);
    return JpegSegmentSurgery.ReplaceExifSegment(jpegBytes, newApp1Payload);
  }

  /// <summary>
  /// Builds the APP1 EXIF payload (incl. the 6-byte <c>Exif\0\0</c> prefix)
  /// for embedding <paramref name="thumbnailJpegBytes"/> as the IFD1
  /// thumbnail of <paramref name="jpegBytes"/>. Existing IFD0 entries are
  /// preserved. Use <see cref="JpegSegmentSurgery.MaxApp1PayloadBytes"/>
  /// to check against the single-APP1 cap before calling
  /// <see cref="SetEmbeddedThumbnail"/>; this lets callers iterate on
  /// dimensions / quality without writing the file repeatedly.
  /// </summary>
  public static byte[] BuildEmbeddedThumbnailPayload(ReadOnlySpan<byte> jpegBytes, byte[] thumbnailJpegBytes, int thumbnailWidth, int thumbnailHeight) {
    ArgumentNullException.ThrowIfNull(thumbnailJpegBytes);
    if (thumbnailJpegBytes.Length == 0)
      throw new ArgumentException("Thumbnail bytes must be non-empty.", nameof(thumbnailJpegBytes));

    var tiff = ExtractTiffOrCreate(jpegBytes);
    var le = tiff.LittleEndian;

    var ifd1 = new TiffIfd();
    ifd1.SetEntry(ExifValueEncoding.Short(0x0100, (ushort)thumbnailWidth, le));   // ImageWidth
    ifd1.SetEntry(ExifValueEncoding.Short(0x0101, (ushort)thumbnailHeight, le));  // ImageLength
    ifd1.SetEntry(ExifValueEncoding.Short(TiffTags.Compression, 6, le));          // 6 = JPEG
    // Offset and length are back-patched by TiffWriter once the bytes are placed.
    ifd1.SetEntry(new TiffEntry(TiffTags.JpegInterchangeFormat, TiffFieldType.Long, 1, new byte[4]));
    ifd1.SetEntry(new TiffEntry(TiffTags.JpegInterchangeFormatLength, TiffFieldType.Long, 1, new byte[4]));

    tiff.Ifd0.Next = ifd1;
    tiff.ThumbnailJpegBytes = thumbnailJpegBytes;

    var newTiff = TiffWriter.Serialize(tiff);
    return ConcatenateExifPayload(newTiff);
  }

  /// <summary>
  /// Replaces (or creates) the IFD1 embedded JPEG thumbnail. Throws
  /// <see cref="InvalidOperationException"/> when the resulting EXIF
  /// segment exceeds the JPEG APP1 cap — callers wanting graceful
  /// downgrade should preflight via <see cref="BuildEmbeddedThumbnailPayload"/>.
  /// </summary>
  public static byte[] SetEmbeddedThumbnail(ReadOnlySpan<byte> jpegBytes, byte[] thumbnailJpegBytes, int thumbnailWidth, int thumbnailHeight) {
    var payload = BuildEmbeddedThumbnailPayload(jpegBytes, thumbnailJpegBytes, thumbnailWidth, thumbnailHeight);
    return JpegSegmentSurgery.ReplaceExifSegment(jpegBytes, payload);
  }

  /// <summary>
  /// Reads the APP1 EXIF segment (if any) and parses its TIFF area, or
  /// returns a fresh little-endian empty TIFF image if the JPEG doesn't
  /// have EXIF yet.
  /// </summary>
  public static TiffImage ExtractTiffOrCreate(ReadOnlySpan<byte> jpegBytes) {
    var exifBytes = JpegSegmentSurgery.TryReadExifSegment(jpegBytes);
    if (exifBytes != null && exifBytes.Length > ExifHeader.Length) {
      try {
        // TryReadExifSegment returns the payload INCLUDING the 6-byte
        // "Exif\0\0" prefix; TiffReader.Parse expects the bare TIFF area
        // (II/MM at byte 0), so strip the prefix here.
        var prefixed = exifBytes.AsSpan(0, ExifHeader.Length).SequenceEqual(ExifHeader);
        var tiffArea = prefixed ? exifBytes.AsSpan(ExifHeader.Length) : exifBytes.AsSpan();
        return TiffReader.Parse(tiffArea);
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

  /// <summary>JEITA EXIF date/time format: <c>yyyy:MM:dd HH:mm:ss</c>.</summary>
  private static string FormatExifDate(DateTime when)
    => when.ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

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
  /// <summary>EXIF SubIFD <c>DateTimeOriginal</c> (0x9003) — when the shutter closed.</summary>
  public DateTime? DateTimeOriginal { get; init; }
  /// <summary>EXIF SubIFD <c>DateTimeDigitized</c> (0x9004) — when the file was created.</summary>
  public DateTime? DateTimeDigitized { get; init; }
  /// <summary>IFD0 <c>DateTime</c> (0x0132) — last-modified.</summary>
  public DateTime? DateTimeModified { get; init; }
}

/// <summary>
/// Plain point struct used for EXIF patching to avoid a cross-cutting
/// dependency on PhotoManager.Core types inside this library.
/// </summary>
public readonly record struct GpsPoint(double Latitude, double Longitude, double? AltitudeMeters = null);
