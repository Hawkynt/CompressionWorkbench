#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.JpegArchive;

/// <summary>
/// Reads the complete TIFF structure out of an APP1 EXIF payload: byte order,
/// IFD0, IFD1 (if present), and any sub-IFDs referenced by EXIF / GPS /
/// Interoperability pointers. Unknown tags are kept as raw byte values so a
/// subsequent writer round-trip doesn't lose data.
/// </summary>
public static class TiffReader {
  /// <summary>
  /// Parses a TIFF area — the bytes AFTER the <c>Exif\0\0</c> header when
  /// the area came from a JPEG APP1 segment, or the whole file for a raw TIFF.
  /// </summary>
  public static TiffImage Parse(ReadOnlySpan<byte> tiffArea) {
    if (tiffArea.Length < 8)
      throw new InvalidDataException("TIFF area too small (needs at least 8 bytes for header).");

    bool littleEndian;
    if (tiffArea[0] == 'I' && tiffArea[1] == 'I')
      littleEndian = true;
    else if (tiffArea[0] == 'M' && tiffArea[1] == 'M')
      littleEndian = false;
    else
      throw new InvalidDataException("TIFF byte-order marker is not II or MM.");

    var magic = ReadU16(tiffArea[2..], littleEndian);
    if (magic != 0x002A)
      throw new InvalidDataException($"Unexpected TIFF magic number 0x{magic:X4}; expected 0x002A.");

    var ifd0Offset = (int)ReadU32(tiffArea[4..], littleEndian);
    var ifd0 = ReadIfd(tiffArea, ifd0Offset, littleEndian, includeSubIfds: true);

    return new TiffImage {
      LittleEndian = littleEndian,
      Ifd0 = ifd0
    };
  }

  private static TiffIfd ReadIfd(ReadOnlySpan<byte> tiffArea, int offset, bool littleEndian, bool includeSubIfds) {
    var ifd = new TiffIfd();

    if (offset + 2 > tiffArea.Length)
      return ifd;

    var count = ReadU16(tiffArea[offset..], littleEndian);
    var entryBase = offset + 2;
    if (entryBase + count * 12 + 4 > tiffArea.Length)
      throw new InvalidDataException("IFD extends past TIFF area.");

    for (var i = 0; i < count; i++) {
      var entryPos = entryBase + i * 12;
      var tag = ReadU16(tiffArea[entryPos..], littleEndian);
      var type = (TiffFieldType)ReadU16(tiffArea[(entryPos + 2)..], littleEndian);
      var components = ReadU32(tiffArea[(entryPos + 4)..], littleEndian);
      var inlineBytes = tiffArea.Slice(entryPos + 8, 4);

      var valueBytes = ReadValueBytes(tiffArea, type, components, inlineBytes, littleEndian);
      ifd.Entries.Add(new TiffEntry(tag, type, components, valueBytes));
    }

    var nextIfdOffset = (int)ReadU32(tiffArea[(entryBase + count * 12)..], littleEndian);
    if (nextIfdOffset > 0 && nextIfdOffset < tiffArea.Length)
      ifd.Next = ReadIfd(tiffArea, nextIfdOffset, littleEndian, includeSubIfds: false);

    if (includeSubIfds) {
      // Follow the sub-IFD pointer tags we know about (EXIF, GPS, Interop).
      foreach (var pointerTag in new ushort[] { TiffTags.ExifSubIfdPointer, TiffTags.GpsSubIfdPointer, 0xA005 }) {
        var entry = ifd.FindEntry(pointerTag);
        if (entry == null) continue;
        var subOffset = (int)ReadUnsignedLong(entry.ValueBytes, littleEndian);
        if (subOffset <= 0 || subOffset >= tiffArea.Length) continue;

        var subIfd = ReadIfd(tiffArea, subOffset, littleEndian, includeSubIfds: false);
        ifd.SubIfds[pointerTag] = subIfd;
      }
    }

    return ifd;
  }

  private static byte[] ReadValueBytes(ReadOnlySpan<byte> tiffArea, TiffFieldType type, uint components, ReadOnlySpan<byte> inline, bool littleEndian) {
    var bytesPerComponent = BytesPerComponent(type);
    var totalBytes = (int)(components * bytesPerComponent);

    if (totalBytes <= 4) {
      // Value fits in the 4-byte inline slot. Copy only the meaningful bytes,
      // not all 4, so the value round-trips cleanly.
      return inline[..totalBytes].ToArray();
    }

    var offset = (int)ReadU32(inline, littleEndian);
    if (offset < 0 || offset + totalBytes > tiffArea.Length)
      throw new InvalidDataException($"Out-of-line TIFF value at offset {offset} with length {totalBytes} exceeds area size {tiffArea.Length}.");

    return tiffArea.Slice(offset, totalBytes).ToArray();
  }

  public static uint BytesPerComponent(TiffFieldType type) => type switch {
    TiffFieldType.Byte or TiffFieldType.Ascii or TiffFieldType.SByte or TiffFieldType.Undefined => 1,
    TiffFieldType.Short or TiffFieldType.SShort => 2,
    TiffFieldType.Long or TiffFieldType.SLong or TiffFieldType.Float or TiffFieldType.Ifd => 4,
    TiffFieldType.Rational or TiffFieldType.SRational or TiffFieldType.Double => 8,
    _ => 1
  };

  /// <summary>
  /// Reads the raw bytes of a Long-typed value. Callers that already know
  /// the endianness use this to dereference sub-IFD pointer entries.
  /// </summary>
  public static uint ReadUnsignedLong(byte[] valueBytes, bool littleEndian) {
    if (valueBytes.Length < 4)
      throw new InvalidDataException("Value bytes too short for a Long.");
    return littleEndian
      ? BinaryPrimitives.ReadUInt32LittleEndian(valueBytes)
      : BinaryPrimitives.ReadUInt32BigEndian(valueBytes);
  }

  private static ushort ReadU16(ReadOnlySpan<byte> s, bool le)
    => le ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);

  private static uint ReadU32(ReadOnlySpan<byte> s, bool le)
    => le ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
}
