#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.JpegArchive;

/// <summary>
/// Reads the EXIF IFD structure far enough to locate the IFD1 thumbnail JPEG offset
/// and length (tags 0x0201 JPEGInterchangeFormat and 0x0202 JPEGInterchangeFormatLength).
/// Byte-order is detected from the EXIF header's first two bytes ("II" = little-endian,
/// "MM" = big-endian); subsequent reads use that endianness.
/// </summary>
public static class ExifIfdParser {
  public sealed record Ifd1Thumbnail(int Offset, int Length);

  /// <summary>
  /// Parses an EXIF APP1 segment payload (excluding the leading "Exif\0\0" 6-byte
  /// header) and returns the IFD1 thumbnail locator, or null if absent.
  /// </summary>
  public static Ifd1Thumbnail? FindIfd1Thumbnail(ReadOnlySpan<byte> tiffArea) {
    if (tiffArea.Length < 8) return null;
    var littleEndian = tiffArea[0] == 'I' && tiffArea[1] == 'I';
    var bigEndian = tiffArea[0] == 'M' && tiffArea[1] == 'M';
    if (!littleEndian && !bigEndian) return null;

    var magic = ReadU16(tiffArea[2..], littleEndian);
    if (magic != 0x002A) return null;
    var ifd0Offset = (int)ReadU32(tiffArea[4..], littleEndian);

    var ifd1Offset = SkipIfd(tiffArea, ifd0Offset, littleEndian);
    if (ifd1Offset <= 0 || ifd1Offset >= tiffArea.Length) return null;

    return ReadThumbnailFromIfd(tiffArea, ifd1Offset, littleEndian);
  }

  /// <summary>
  /// Returns the absolute offset of IFD1 as stored at the end of IFD0's entry table.
  /// Returns 0 when there is no IFD1.
  /// </summary>
  private static int SkipIfd(ReadOnlySpan<byte> tiffArea, int ifdOffset, bool le) {
    if (ifdOffset + 2 > tiffArea.Length) return 0;
    var count = ReadU16(tiffArea[ifdOffset..], le);
    var nextOffsetPos = ifdOffset + 2 + count * 12;
    if (nextOffsetPos + 4 > tiffArea.Length) return 0;
    return (int)ReadU32(tiffArea[nextOffsetPos..], le);
  }

  private static Ifd1Thumbnail? ReadThumbnailFromIfd(ReadOnlySpan<byte> tiffArea, int ifdOffset, bool le) {
    if (ifdOffset + 2 > tiffArea.Length) return null;
    var count = ReadU16(tiffArea[ifdOffset..], le);
    var entryBase = ifdOffset + 2;

    int? thumbOffset = null;
    int? thumbLength = null;
    for (var i = 0; i < count; ++i) {
      var entryPos = entryBase + i * 12;
      if (entryPos + 12 > tiffArea.Length) break;
      var tag = ReadU16(tiffArea[entryPos..], le);
      // Tag value type is u16 (unused here); components is u32; value/offset is u32.
      var valueOrOffset = (int)ReadU32(tiffArea[(entryPos + 8)..], le);
      if (tag == 0x0201) thumbOffset = valueOrOffset;
      else if (tag == 0x0202) thumbLength = valueOrOffset;
    }

    if (thumbOffset.HasValue && thumbLength is > 0)
      return new Ifd1Thumbnail(thumbOffset.Value, thumbLength.Value);
    return null;
  }

  private static ushort ReadU16(ReadOnlySpan<byte> s, bool littleEndian)
    => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);

  private static uint ReadU32(ReadOnlySpan<byte> s, bool littleEndian)
    => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
}
