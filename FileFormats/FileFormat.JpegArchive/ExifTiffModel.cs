#pragma warning disable CS1591
namespace FileFormat.JpegArchive;

/// <summary>
/// TIFF-format tag types as defined by the TIFF 6.0 spec. Used by EXIF IFDs
/// which are just TIFF IFDs living inside a JPEG APP1 segment (after the
/// six-byte "Exif\0\0" marker) or a TIFF file.
/// </summary>
public enum TiffFieldType : ushort {
  /// <summary>
  /// Specifies the byte option.
  /// </summary>
Byte = 1,
  /// <summary>
  /// Specifies the ascii option.
  /// </summary>
Ascii = 2,
  /// <summary>
  /// Specifies the short option.
  /// </summary>
Short = 3,
  /// <summary>
  /// Specifies the long option.
  /// </summary>
Long = 4,
  /// <summary>
  /// Specifies the rational option.
  /// </summary>
Rational = 5,
  /// <summary>
  /// Specifies the s byte option.
  /// </summary>
SByte = 6,
  /// <summary>
  /// Specifies the undefined option.
  /// </summary>
Undefined = 7,
  /// <summary>
  /// Specifies the s short option.
  /// </summary>
SShort = 8,
  /// <summary>
  /// Specifies the s long option.
  /// </summary>
SLong = 9,
  /// <summary>
  /// Specifies the s rational option.
  /// </summary>
SRational = 10,
  /// <summary>
  /// Specifies the float option.
  /// </summary>
Float = 11,
  /// <summary>
  /// Specifies the double option.
  /// </summary>
Double = 12,
  /// <summary>
  /// Specifies the ifd option.
  /// </summary>
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
  /// <summary>
  /// Gets the value length.
  /// </summary>
public int ValueLength => this.ValueBytes.Length;
}

/// <summary>
/// One IFD (Image File Directory): an ordered list of <see cref="TiffEntry"/>
/// plus a pointer to the next IFD if there is one (used by IFD0→IFD1 for
/// the embedded thumbnail chain).
/// </summary>
public sealed class TiffIfd {
  /// <summary>
  /// Gets the entries.
  /// </summary>
public List<TiffEntry> Entries { get; } = new();
  /// <summary>
  /// Gets or sets the next.
  /// </summary>
public TiffIfd? Next { get; set; }

  /// <summary>
  /// Sub-IFDs referenced by entries in this IFD. Key = tag number of the
  /// pointer in <see cref="Entries"/>. Common tags: 0x8769 (EXIF SubIFD),
  /// 0x8825 (GPS IFD), 0xA005 (Interop IFD).
  /// </summary>
  public Dictionary<ushort, TiffIfd> SubIfds { get; } = new();

  /// <summary>
  /// Performs the find entry operation.
  /// </summary>
public TiffEntry? FindEntry(ushort tag) => this.Entries.FirstOrDefault(e => e.Tag == tag);

  /// <summary>Removes any existing entry with this tag and appends the new one.</summary>
  public void SetEntry(TiffEntry entry) {
    this.Entries.RemoveAll(e => e.Tag == entry.Tag);
    this.Entries.Add(entry);
    this.Entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
  }

  /// <summary>
  /// Performs the remove entry operation.
  /// </summary>
public bool RemoveEntry(ushort tag) => this.Entries.RemoveAll(e => e.Tag == tag) > 0;
}

/// <summary>
/// Root container for a parsed EXIF/TIFF area: byte order, magic number, IFD0
/// (which chains to IFD1 via <see cref="TiffIfd.Next"/> and hangs the EXIF
/// and GPS sub-IFDs off <see cref="TiffIfd.SubIfds"/>).
/// </summary>
public sealed class TiffImage {
  /// <summary>
  /// Gets a value indicating whether little endian.
  /// </summary>
public bool LittleEndian { get; init; }
  /// <summary>
  /// Gets or sets the ifd 0.
  /// </summary>
public TiffIfd Ifd0 { get; init; } = new();

  /// <summary>
  /// Optional embedded JPEG thumbnail bytes. When set together with an IFD1
  /// chain (<see cref="TiffIfd.Next"/>), <see cref="TiffWriter"/> writes the
  /// blob after the IFD1 entries and back-patches the IFD1
  /// <c>JpegInterchangeFormat</c> (0x0201) and <c>JpegInterchangeFormatLength</c>
  /// (0x0202) tags so EXIF readers find it. Reading thumbnail bytes back from
  /// an existing TIFF is not done here yet — that's a follow-up.
  /// </summary>
  public byte[]? ThumbnailJpegBytes { get; set; }
}

/// <summary>
/// Well-known TIFF / EXIF tag numbers we care about for the PhotoManager
/// write path. Grouped by IFD: main (IFD0), EXIF sub-IFD, GPS sub-IFD.
/// </summary>
public static class TiffTags {
  // IFD1 (thumbnail) — same well-known tags as IFD0, plus the JPEG offset/length pair.
  /// <summary>
  /// Defines the compression constant value.
  /// </summary>
public const ushort Compression = 0x0103;        // 6 = JPEG-compressed thumbnail
  /// <summary>
  /// Defines the jpeg interchange format constant value.
  /// </summary>
public const ushort JpegInterchangeFormat       = 0x0201; // offset to thumbnail JPEG bytes
  /// <summary>
  /// Defines the jpeg interchange format length constant value.
  /// </summary>
public const ushort JpegInterchangeFormatLength = 0x0202; // thumbnail JPEG length

  // IFD0 (main image).
  /// <summary>
  /// Defines the image description constant value.
  /// </summary>
public const ushort ImageDescription = 0x010E;
  /// <summary>
  /// Defines the make constant value.
  /// </summary>
public const ushort Make             = 0x010F;
  /// <summary>
  /// Defines the model constant value.
  /// </summary>
public const ushort Model            = 0x0110;
  /// <summary>
  /// Defines the orientation constant value.
  /// </summary>
public const ushort Orientation      = 0x0112;
  /// <summary>
  /// Defines the date time constant value.
  /// </summary>
public const ushort DateTime         = 0x0132;
  /// <summary>
  /// Defines the software constant value.
  /// </summary>
public const ushort Software         = 0x0131;
  /// <summary>
  /// Defines the artist constant value.
  /// </summary>
public const ushort Artist           = 0x013B;
  /// <summary>
  /// Defines the copyright constant value.
  /// </summary>
public const ushort Copyright        = 0x8298;
  /// <summary>
  /// Defines the exif sub ifd pointer constant value.
  /// </summary>
public const ushort ExifSubIfdPointer = 0x8769;
  /// <summary>
  /// Defines the gps sub ifd pointer constant value.
  /// </summary>
public const ushort GpsSubIfdPointer  = 0x8825;

  // EXIF sub-IFD.
  /// <summary>
  /// Defines the exposure time constant value.
  /// </summary>
public const ushort ExposureTime    = 0x829A;
  /// <summary>
  /// Defines the f number constant value.
  /// </summary>
public const ushort FNumber         = 0x829D;
  /// <summary>
  /// Defines the date time original constant value.
  /// </summary>
public const ushort DateTimeOriginal  = 0x9003;
  /// <summary>
  /// Defines the date time digitized constant value.
  /// </summary>
public const ushort DateTimeDigitized = 0x9004;
  /// <summary>
  /// Defines the user comment constant value.
  /// </summary>
public const ushort UserComment     = 0x9286;

  // GPS sub-IFD.
  /// <summary>
  /// Defines the gps latitude ref constant value.
  /// </summary>
public const ushort GpsLatitudeRef  = 0x0001;
  /// <summary>
  /// Defines the gps latitude constant value.
  /// </summary>
public const ushort GpsLatitude     = 0x0002;
  /// <summary>
  /// Defines the gps longitude ref constant value.
  /// </summary>
public const ushort GpsLongitudeRef = 0x0003;
  /// <summary>
  /// Defines the gps longitude constant value.
  /// </summary>
public const ushort GpsLongitude    = 0x0004;
  /// <summary>
  /// Defines the gps altitude ref constant value.
  /// </summary>
public const ushort GpsAltitudeRef  = 0x0005;
  /// <summary>
  /// Defines the gps altitude constant value.
  /// </summary>
public const ushort GpsAltitude     = 0x0006;
  /// <summary>
  /// Defines the gps img direction ref constant value.
  /// </summary>
public const ushort GpsImgDirectionRef = 0x0010;
  /// <summary>
  /// Defines the gps img direction constant value.
  /// </summary>
public const ushort GpsImgDirection    = 0x0011;
  /// <summary>
  /// Defines the gps map datum constant value.
  /// </summary>
public const ushort GpsMapDatum        = 0x0012;
  /// <summary>
  /// Defines the gps dest latitude ref constant value.
  /// </summary>
public const ushort GpsDestLatitudeRef = 0x0013;
  /// <summary>
  /// Defines the gps dest latitude constant value.
  /// </summary>
public const ushort GpsDestLatitude    = 0x0014;
  /// <summary>
  /// Defines the gps dest longitude ref constant value.
  /// </summary>
public const ushort GpsDestLongitudeRef = 0x0015;
  /// <summary>
  /// Defines the gps dest longitude constant value.
  /// </summary>
public const ushort GpsDestLongitude    = 0x0016;
}
