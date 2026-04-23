#pragma warning disable CS1591
namespace FileFormat.JpegArchive;

/// <summary>
/// TIFF-format tag types as defined by the TIFF 6.0 spec. Used by EXIF IFDs
/// which are just TIFF IFDs living inside a JPEG APP1 segment (after the
/// six-byte "Exif\0\0" marker) or a TIFF file.
/// </summary>
public enum TiffFieldType : ushort {
  Byte = 1,
  Ascii = 2,
  Short = 3,
  Long = 4,
  Rational = 5,
  SByte = 6,
  Undefined = 7,
  SShort = 8,
  SLong = 9,
  SRational = 10,
  Float = 11,
  Double = 12,
  Ifd = 13  // Used for sub-IFD pointers; same wire format as Long.
}

/// <summary>
/// One entry from a TIFF IFD: the 2-byte tag number, the type, the element
/// count, and the raw bytes of the value (already pulled out-of-line if the
/// stored value was too large to fit in the entry's 4-byte value slot).
/// We deliberately keep the value as opaque bytes so unknown tags round-trip
/// unchanged — the whole point of a write path that doesn't corrupt the
/// EXIF structure is that we preserve what we don't understand.
/// </summary>
public sealed record TiffEntry(ushort Tag, TiffFieldType Type, uint Count, byte[] ValueBytes) {
  public int ValueLength => this.ValueBytes.Length;
}

/// <summary>
/// One IFD (Image File Directory): an ordered list of <see cref="TiffEntry"/>
/// plus a pointer to the next IFD if there is one (used by IFD0→IFD1 for
/// the embedded thumbnail chain).
/// </summary>
public sealed class TiffIfd {
  public List<TiffEntry> Entries { get; } = new();
  public TiffIfd? Next { get; set; }

  /// <summary>
  /// Sub-IFDs referenced by entries in this IFD. Key = tag number of the
  /// pointer in <see cref="Entries"/>. Common tags: 0x8769 (EXIF SubIFD),
  /// 0x8825 (GPS IFD), 0xA005 (Interop IFD).
  /// </summary>
  public Dictionary<ushort, TiffIfd> SubIfds { get; } = new();

  public TiffEntry? FindEntry(ushort tag) => this.Entries.FirstOrDefault(e => e.Tag == tag);

  /// <summary>Removes any existing entry with this tag and appends the new one.</summary>
  public void SetEntry(TiffEntry entry) {
    this.Entries.RemoveAll(e => e.Tag == entry.Tag);
    this.Entries.Add(entry);
    this.Entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
  }

  public bool RemoveEntry(ushort tag) => this.Entries.RemoveAll(e => e.Tag == tag) > 0;
}

/// <summary>
/// Root container for a parsed EXIF/TIFF area: byte order, magic number, IFD0
/// (which chains to IFD1 via <see cref="TiffIfd.Next"/> and hangs the EXIF
/// and GPS sub-IFDs off <see cref="TiffIfd.SubIfds"/>).
/// </summary>
public sealed class TiffImage {
  public bool LittleEndian { get; init; }
  public TiffIfd Ifd0 { get; init; } = new();
}

/// <summary>
/// Well-known TIFF / EXIF tag numbers we care about for the PhotoManager
/// write path. Grouped by IFD: main (IFD0), EXIF sub-IFD, GPS sub-IFD.
/// </summary>
public static class TiffTags {
  // IFD0 (main image).
  public const ushort ImageDescription = 0x010E;
  public const ushort Make             = 0x010F;
  public const ushort Model            = 0x0110;
  public const ushort Orientation      = 0x0112;
  public const ushort DateTime         = 0x0132;
  public const ushort Software         = 0x0131;
  public const ushort Artist           = 0x013B;
  public const ushort Copyright        = 0x8298;
  public const ushort ExifSubIfdPointer = 0x8769;
  public const ushort GpsSubIfdPointer  = 0x8825;

  // EXIF sub-IFD.
  public const ushort ExposureTime    = 0x829A;
  public const ushort FNumber         = 0x829D;
  public const ushort DateTimeOriginal  = 0x9003;
  public const ushort DateTimeDigitized = 0x9004;
  public const ushort UserComment     = 0x9286;

  // GPS sub-IFD.
  public const ushort GpsLatitudeRef  = 0x0001;
  public const ushort GpsLatitude     = 0x0002;
  public const ushort GpsLongitudeRef = 0x0003;
  public const ushort GpsLongitude    = 0x0004;
  public const ushort GpsAltitudeRef  = 0x0005;
  public const ushort GpsAltitude     = 0x0006;
  public const ushort GpsImgDirectionRef = 0x0010;
  public const ushort GpsImgDirection    = 0x0011;
  public const ushort GpsMapDatum        = 0x0012;
  public const ushort GpsDestLatitudeRef = 0x0013;
  public const ushort GpsDestLatitude    = 0x0014;
  public const ushort GpsDestLongitudeRef = 0x0015;
  public const ushort GpsDestLongitude    = 0x0016;
}
