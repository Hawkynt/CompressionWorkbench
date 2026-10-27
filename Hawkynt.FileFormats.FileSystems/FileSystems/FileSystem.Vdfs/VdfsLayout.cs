using System.Text;

namespace FileSystem.Vdfs;

/// <summary>
/// On-disk constants of a ZenGin VDF volume, as <c>docs/VDFS-ON-DISK.md</c> describes them.
/// </summary>
/// <remarks>
/// Every value here is taken from GothicKit's ZenKit (MIT) and confirmed against the two
/// volumes in its test corpus: one written by GothicVDFS, one zipped by VDFSTool.
/// </remarks>
public static class VdfsLayout {

  /// <summary>Bytes of free-text comment at the start of the volume, padded with <c>0x1A</c>.</summary>
  public const int CommentSize = 256;

  /// <summary>Byte the comment is padded with, and where a reader stops reading it.</summary>
  public const byte CommentPad = 0x1A;

  /// <summary>Where the 16-byte signature starts.</summary>
  public const int SignatureOffset = CommentSize;

  /// <summary>Length of the signature.</summary>
  public const int SignatureSize = 16;

  /// <summary>Shared prefix of every known signature.</summary>
  public static ReadOnlySpan<byte> SignaturePrefix => "PSVDSC_V2.00"u8;

  /// <summary>Signature Gothic 1's tools write.</summary>
  public static ReadOnlySpan<byte> SignatureGothic1 => "PSVDSC_V2.00\r\n\r\n"u8;

  /// <summary>Signature Gothic 2's tools write.</summary>
  public static ReadOnlySpan<byte> SignatureGothic2 => "PSVDSC_V2.00\n\r\n\r"u8;

  /// <summary>Signature VDFSTool writes.</summary>
  public static ReadOnlySpan<byte> SignatureVdfsTool => "PSVDSC_V2.00\u001A\u001A\u001A\u001A"u8;

  /// <summary>Header field: number of catalog entries, directories included.</summary>
  public const int EntryCountOffset = 272;

  /// <summary>Header field: number of files.</summary>
  public const int FileCountOffset = 276;

  /// <summary>Header field: MS-DOS date and time the volume was written.</summary>
  public const int TimestampOffset = 280;

  /// <summary>Header field: the sum of every file's uncompressed size.</summary>
  public const int DataSizeOffset = 284;

  /// <summary>Header field: byte offset of the catalog.</summary>
  public const int CatalogOffsetOffset = 288;

  /// <summary>Header field: volume flags (<see cref="VolumeNormal" /> or <see cref="VolumeZipped" />).</summary>
  public const int VolumeFlagsOffset = 292;

  /// <summary>Bytes of header before the catalog, which is also where every tool puts it.</summary>
  public const int HeaderSize = 296;

  /// <summary>Bytes per catalog entry.</summary>
  public const int EntrySize = 80;

  /// <summary>Bytes of entry name, space-padded.</summary>
  public const int NameSize = 64;

  /// <summary>Entry field: payload offset of a file, or first-child index of a directory.</summary>
  public const int EntryOffsetField = 64;

  /// <summary>Entry field: uncompressed size of a file, 0 for a directory.</summary>
  public const int EntrySizeField = 68;

  /// <summary>Entry field: type flags.</summary>
  public const int EntryTypeField = 72;

  /// <summary>Entry field: Win32 file attributes.</summary>
  public const int EntryAttributesField = 76;

  /// <summary>Type flag: the entry is a directory.</summary>
  public const uint TypeDirectory = 0x80000000;

  /// <summary>Type flag: the entry is the last one of its directory.</summary>
  public const uint TypeLast = 0x40000000;

  /// <summary>Attributes the reference tools give a file: <c>FILE_ATTRIBUTE_ARCHIVE</c>.</summary>
  public const uint FileAttributes = 0x20;

  /// <summary>Volume flags of a volume whose payloads are stored as-is.</summary>
  public const uint VolumeNormal = 0x50;

  /// <summary>Volume flags of a Union volume whose payloads are ZippedStreams.</summary>
  public const uint VolumeZipped = 0xA0;

  /// <summary>Encoding of names and the comment: single-byte, every byte value round-trips.</summary>
  public static Encoding NameEncoding => Encoding.Latin1;

  /// <summary>
  /// Whether a file is stored raw even in a zipped volume. Union streams sound straight
  /// from the volume, so its tools leave <c>.wav</c> and <c>.ogg</c> uncompressed.
  /// </summary>
  public static bool IsStoredRawWhenZipped(string name)
    => name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
    || name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);

  /// <summary>Packs a time into the MS-DOS date/time word the header carries.</summary>
  public static uint ToDosTime(DateTime time) {
    if (time.Year < 1980) time = new DateTime(1980, 1, 1);
    if (time.Year > 2107) time = new DateTime(2107, 12, 31, 23, 59, 58);
    return (uint)((time.Year - 1980) << 25 | time.Month << 21 | time.Day << 16
                | time.Hour << 11 | time.Minute << 5 | time.Second / 2);
  }

  /// <summary>Unpacks the MS-DOS date/time word, or null when it names no valid date.</summary>
  public static DateTime? FromDosTime(uint dos) {
    var year = (int)(dos >> 25) + 1980;
    var month = (int)(dos >> 21 & 0x0F);
    var day = (int)(dos >> 16 & 0x1F);
    var hour = (int)(dos >> 11 & 0x1F);
    var minute = (int)(dos >> 5 & 0x3F);
    var second = (int)(dos & 0x1F) * 2;
    if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
        || hour > 23 || minute > 59 || second > 59)
      return null;
    return new DateTime(year, month, day, hour, minute, second);
  }
}
