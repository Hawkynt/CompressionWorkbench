#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Ntfs;

/// <summary>
/// The two FILE-record header layouts NTFS has shipped, and the bounds every
/// update-sequence fixup has to check before it touches a record.
/// </summary>
/// <remarks>
/// <para>
/// A FILE record begins with the multi-sector-transfer header
/// (<c>NTFS_RECORD</c>: magic, <c>usa_ofs</c> at 4, <c>usa_count</c> at 6) and
/// continues with the MFT record fields. Up to and including NTFS 3.0 those
/// fields end after <c>next_attr_instance</c> at offset 40, so the fixed header
/// is 42 bytes and the update-sequence array starts right there. NTFS 3.1
/// (Windows XP) appended a reserved <c>u16</c> at 42 and the record's own MFT
/// number as a <c>u32</c> at 44, which pushes the array to 48.
/// </para>
/// <para>
/// The two are told apart by nothing but <c>usa_ofs</c>: a record whose array
/// starts at 48 has the record-number field, one whose array starts at 42 does
/// not — and in that one, offset 44 is a saved sector trailer, so reading it as
/// a record number reads whatever two sectors happened to end with. Every reader
/// here therefore takes the offset from the record in hand rather than from the
/// version the volume claims.
/// </para>
/// </remarks>
internal static class NtfsRecordLayout {

  /// <summary>Fixed FILE header up to and including NTFS 3.0: the array starts at 42.</summary>
  internal const int LegacyFileHeaderSize = 42;

  /// <summary>Fixed FILE header from NTFS 3.1 on: the array starts at 48.</summary>
  internal const int ExtendedFileHeaderSize = 48;

  /// <summary>Offset of the <c>u32</c> MFT record number, present only in the NTFS 3.1+ header.</summary>
  internal const int RecordNumberOffset = 44;

  /// <summary>Fixed INDX header: <c>NTFS_RECORD</c> (24 bytes) plus <c>INDEX_HEADER</c> (16).</summary>
  internal const int IndexHeaderSize = 40;

  /// <summary>
  /// Attribute regions never begin before 56 regardless of geometry: that is where
  /// mkfs.ntfs and Windows both put the first attribute of a 1024-byte record, and
  /// holding the floor keeps the common case byte-identical across record sizes.
  /// </summary>
  internal const int MinimumAttributeStart = 56;

  /// <summary>Where the update-sequence array of a freshly built FILE record goes.</summary>
  internal static int UpdateSequenceOffsetFor(bool extendedHeader)
    => extendedHeader ? ExtendedFileHeaderSize : LegacyFileHeaderSize;

  /// <summary>One array slot for the record-wide USN plus one per protected sector.</summary>
  internal static int UpdateSequenceCount(int recordSize, int bytesPerSector)
    => 1 + recordSize / bytesPerSector;

  /// <summary>
  /// Whether a record already on disk carries the NTFS 3.1 record-number field —
  /// decided by where its update-sequence array starts, not by the volume version.
  /// </summary>
  internal static bool HasRecordNumberField(ReadOnlySpan<byte> record)
    => record.Length >= 6
       && BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) >= ExtendedFileHeaderSize;

  /// <summary>
  /// First attribute offset for a record whose array starts at <paramref name="usaOffset"/>:
  /// past the array, 8-byte aligned, never below <see cref="MinimumAttributeStart"/>.
  /// </summary>
  internal static int AttributeStart(int usaOffset, int recordSize, int bytesPerSector)
    => Math.Max(MinimumAttributeStart,
      (usaOffset + 2 * UpdateSequenceCount(recordSize, bytesPerSector) + 7) & ~7);

  /// <summary>
  /// The largest resident <c>$DATA</c> value that still fits in a record of this
  /// geometry beside the two mandatory attributes.
  /// </summary>
  /// <remarks>
  /// A flat byte threshold is not enough on its own, because three things compete for
  /// the same record: the update-sequence array (which moves where the attributes
  /// start), <c>$STANDARD_INFORMATION</c> (48 bytes up to NTFS 1.2, 72 from 3.0) and
  /// <c>$FILE_NAME</c>, which grows by two bytes per character of the name. A file
  /// whose data would not fit has to go non-resident; writing it resident anyway
  /// overruns the record.
  /// </remarks>
  internal static int MaxResidentDataLength(
    int recordSize, int usaOffset, int bytesPerSector, int standardInformationLength, int fileNameChars) {
    var attributeStart = AttributeStart(usaOffset, recordSize, bytesPerSector);
    var standardInformation = (24 + standardInformationLength + 7) & ~7;
    var fileName = (24 + 66 + 2 * fileNameChars + 7) & ~7;

    // Eight bytes for the end-of-attributes marker, then the $DATA header itself.
    var available = recordSize - attributeStart - standardInformation - fileName - 8 - 24;
    return Math.Max(0, available);
  }

  /// <summary>
  /// Reads a record's update-sequence array position and rejects it unless it lies
  /// wholly inside the record and clear of the record's own fixed header.
  /// </summary>
  /// <remarks>
  /// An array that starts inside the header would have the fixup overwrite the very
  /// fields — <c>usa_ofs</c>, the flags, the used size — that say how to read the
  /// record, and one that runs past the end would read or write out of bounds. Both
  /// are refusals rather than exceptions: callers walk records they did not write and
  /// have to survive a damaged one.
  /// </remarks>
  internal static bool TryReadUpdateSequence(ReadOnlySpan<byte> record, out int usaOffset, out int usaCount) {
    usaOffset = 0;
    usaCount = 0;
    if (record.Length < 8) return false;

    int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
    int count = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);

    // Fewer than two slots means no protected sector at all: nothing to fix up.
    if (count < 2) return false;

    // A FILE record's fields reach 42 even in the older layout; an INDX record's
    // reach 40. Anything below that overlaps the header it was read from.
    var minimum = record[..4].SequenceEqual("FILE"u8) ? LegacyFileHeaderSize : IndexHeaderSize;
    if (offset < minimum) return false;
    if (offset + 2 * count > record.Length) return false;

    usaOffset = offset;
    usaCount = count;
    return true;
  }
}
