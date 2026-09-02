#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Aomei;

/// <summary>
/// Twelve-byte tagged-record header that prefixes every INFO/INDEX record in
/// the AOMEI <c>.adi</c>/<c>.afi</c> payload. Recovered from access patterns
/// <c>pHead-&gt;Type</c>, <c>pHead-&gt;Size</c>, <c>Head.Crc32</c> in the
/// <c>ImgFile.dll</c> and <c>ammntdrv.sys</c>.
///
/// <code>
/// struct BR_STANDARD_HEADER {
///   uint32_t Size;   // total record bytes INCLUDING this header
///   uint16_t Type;   // INFO_TYPE_* / INDEX_TYPE_* tag
///   uint16_t Pad;    // observed-zero padding to 4-byte alignment
///   uint32_t Crc32;  // zlib CRC32 over the whole record with this field zeroed
/// };
/// </code>
///
/// <para>
/// The on-disk layout treats Type as a 16-bit value followed by 16 bits of
/// padding observed-zero in every recovered sample — the assert text in
/// <c>ImgFile.dll</c> compares <c>pHead-&gt;Type</c> against 16-bit constants
/// (0x105/0x106/0x107/0x10C) and never against 32-bit values, but the
/// <c>{Size, Type, Crc32}</c> layout in the spec section §3.1 shows the
/// header itself is 12 bytes (4+4+4). We treat that middle 4-byte slot as
/// <c>Type:u16</c> + <c>Reserved:u16</c> in code so the wire layout is
/// self-explanatory; the high u16 round-trips as zero unless a real sample
/// proves otherwise.
/// </para>
/// </summary>
public readonly struct BrStandardHeader {

  /// <summary>Total record size in bytes <b>including</b> this 12-byte
  /// header.</summary>
  public readonly uint Size;

  /// <summary>Record-type tag (e.g. <see cref="AomeiConstants.InfoTypeImageCompress"/>).</summary>
  public readonly ushort Type;

  /// <summary>Reserved/padding word at offsets 6..7. Observed zero in every
  /// known sample; surfaced so future RE work can keep it round-tripping.</summary>
  public readonly ushort Reserved;

  /// <summary>CRC32 over the whole record with this field zeroed during the
  /// computation, per <see cref="BrCrc32.ComputeWithZeroedCrc"/>.</summary>
  public readonly uint Crc32;

  /// <summary>
  /// Initializes a new instance of <see cref="BrStandardHeader"/>.
  /// </summary>
public BrStandardHeader(uint size, ushort type, uint crc32, ushort reserved = 0) {
    this.Size = size;
    this.Type = type;
    this.Reserved = reserved;
    this.Crc32 = crc32;
  }

  /// <summary>Reads a header from the first 12 bytes of
  /// <paramref name="span"/>.</summary>
  public static BrStandardHeader Read(ReadOnlySpan<byte> span) {
    if (span.Length < AomeiConstants.StandardHeaderSize)
      throw new ArgumentException(
        $"Buffer too small for BR_STANDARD_HEADER ({span.Length} < {AomeiConstants.StandardHeaderSize}).",
        nameof(span));
    var size = BinaryPrimitives.ReadUInt32LittleEndian(span);
    var type = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
    var crc = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
    return new BrStandardHeader(size, type, crc, reserved);
  }

  /// <summary>Writes the header into the first 12 bytes of
  /// <paramref name="dst"/>.</summary>
  public void Write(Span<byte> dst) {
    if (dst.Length < AomeiConstants.StandardHeaderSize)
      throw new ArgumentException(
        $"Buffer too small for BR_STANDARD_HEADER ({dst.Length} < {AomeiConstants.StandardHeaderSize}).",
        nameof(dst));
    BinaryPrimitives.WriteUInt32LittleEndian(dst, this.Size);
    BinaryPrimitives.WriteUInt16LittleEndian(dst[4..], this.Type);
    BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], this.Reserved);
    BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], this.Crc32);
  }

  /// <summary>Returns true if the record's recomputed CRC matches the stored
  /// value, per the AOMEI reader's verification protocol.</summary>
  public static bool VerifyCrc(ReadOnlySpan<byte> record) {
    if (record.Length < AomeiConstants.StandardHeaderSize)
      return false;
    var stored = BinaryPrimitives.ReadUInt32LittleEndian(record[AomeiConstants.Crc32FieldOffset..]);
    var computed = BrCrc32.ComputeWithZeroedCrc(record);
    return stored == computed;
  }

  /// <summary>Finalises a freshly-built record by zeroing the CRC field,
  /// recomputing it, and patching the result back into the buffer.
  /// Returns the new CRC value.</summary>
  public static uint SealCrc(Span<byte> record) {
    if (record.Length < AomeiConstants.StandardHeaderSize)
      throw new ArgumentException(
        $"Buffer too small for BR_STANDARD_HEADER ({record.Length} < {AomeiConstants.StandardHeaderSize}).",
        nameof(record));
    record[AomeiConstants.Crc32FieldOffset + 0] = 0;
    record[AomeiConstants.Crc32FieldOffset + 1] = 0;
    record[AomeiConstants.Crc32FieldOffset + 2] = 0;
    record[AomeiConstants.Crc32FieldOffset + 3] = 0;
    var crc = BrCrc32.Compute(record);
    BinaryPrimitives.WriteUInt32LittleEndian(record[AomeiConstants.Crc32FieldOffset..], crc);
    return crc;
  }
}
