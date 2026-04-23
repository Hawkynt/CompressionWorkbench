#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.JpegArchive;

/// <summary>
/// Helpers that encode common EXIF value types — ASCII strings, RATIONAL
/// triples for GPS coordinates, altitude — into the byte layouts a
/// <see cref="TiffEntry"/> expects. All emissions respect the endianness
/// of the TIFF area they'll be stitched into.
/// </summary>
public static class ExifValueEncoding {
  /// <summary>
  /// Encodes an ASCII string (trailing NUL required by the EXIF spec).
  /// </summary>
  public static TiffEntry Ascii(ushort tag, string value) {
    var bytes = Encoding.ASCII.GetBytes(value + "\0");
    return new TiffEntry(tag, TiffFieldType.Ascii, (uint)bytes.Length, bytes);
  }

  /// <summary>
  /// Encodes a single unsigned short (e.g. Orientation).
  /// </summary>
  public static TiffEntry Short(ushort tag, ushort value, bool littleEndian) {
    var bytes = new byte[2];
    if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    else BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
    return new TiffEntry(tag, TiffFieldType.Short, 1, bytes);
  }

  /// <summary>
  /// Encodes a single-byte value (e.g. GPSAltitudeRef: 0=above sea level, 1=below).
  /// </summary>
  public static TiffEntry Byte(ushort tag, byte value)
    => new(tag, TiffFieldType.Byte, 1, new[] { value });

  /// <summary>
  /// Encodes a single RATIONAL (numerator/denominator). Used for altitude
  /// and image-direction degrees.
  /// </summary>
  public static TiffEntry Rational(ushort tag, double value, bool littleEndian, uint denominator = 1_000_000) {
    var bytes = new byte[8];
    WriteRational(bytes, 0, value, littleEndian, denominator);
    return new TiffEntry(tag, TiffFieldType.Rational, 1, bytes);
  }

  /// <summary>
  /// Encodes a GPS latitude/longitude as three RATIONAL values (degrees,
  /// minutes, seconds). The caller passes the absolute value; the ref tag
  /// (N/S/E/W) is written as a separate ASCII entry via <see cref="Ascii"/>.
  /// </summary>
  public static TiffEntry GpsCoordinate(ushort tag, double absoluteDegrees, bool littleEndian) {
    var degrees = Math.Floor(absoluteDegrees);
    var minutesDecimal = (absoluteDegrees - degrees) * 60.0;
    var minutes = Math.Floor(minutesDecimal);
    var seconds = (minutesDecimal - minutes) * 60.0;

    var bytes = new byte[24];
    WriteRational(bytes, 0, degrees, littleEndian, 1);
    WriteRational(bytes, 8, minutes, littleEndian, 1);
    WriteRational(bytes, 16, seconds, littleEndian, 10_000);

    return new TiffEntry(tag, TiffFieldType.Rational, 3, bytes);
  }

  /// <summary>
  /// Parses a GPS coordinate tuple (three RATIONAL values) back into a
  /// decimal-degree absolute value.
  /// </summary>
  public static double? ParseGpsCoordinate(TiffEntry entry, bool littleEndian) {
    if (entry.Type != TiffFieldType.Rational || entry.Count != 3 || entry.ValueBytes.Length < 24)
      return null;

    var degrees = ReadRational(entry.ValueBytes, 0, littleEndian);
    var minutes = ReadRational(entry.ValueBytes, 8, littleEndian);
    var seconds = ReadRational(entry.ValueBytes, 16, littleEndian);
    if (degrees is null || minutes is null || seconds is null)
      return null;

    return degrees.Value + minutes.Value / 60.0 + seconds.Value / 3600.0;
  }

  public static double? ParseRational(TiffEntry entry, bool littleEndian) {
    if (entry.Type != TiffFieldType.Rational || entry.Count < 1 || entry.ValueBytes.Length < 8)
      return null;
    return ReadRational(entry.ValueBytes, 0, littleEndian);
  }

  public static string? ParseAscii(TiffEntry entry) {
    if (entry.Type != TiffFieldType.Ascii)
      return null;
    // Strip trailing NUL(s).
    var text = Encoding.ASCII.GetString(entry.ValueBytes);
    return text.TrimEnd('\0');
  }

  private static void WriteRational(byte[] buffer, int offset, double value, bool littleEndian, uint denominator) {
    var numerator = (uint)Math.Round(Math.Abs(value) * denominator, MidpointRounding.AwayFromZero);
    if (littleEndian) {
      BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), numerator);
      BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), denominator);
    } else {
      BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), numerator);
      BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 4), denominator);
    }
  }

  private static double? ReadRational(byte[] buffer, int offset, bool littleEndian) {
    if (buffer.Length < offset + 8)
      return null;
    uint num, den;
    if (littleEndian) {
      num = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
      den = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4));
    } else {
      num = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset));
      den = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset + 4));
    }
    return den == 0 ? null : (double)num / den;
  }
}
