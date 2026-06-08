#pragma warning disable CS1591
using Compression.Core.Checksums;

namespace FileFormat.Aomei;

/// <summary>
/// AOMEI <c>BRCrc32</c> — the CRC-32 used to integrity-protect the file head,
/// file tail and every <c>BR_STANDARD_HEADER</c>-prefixed INFO/INDEX record.
///
/// <para>
/// Recovered via binary reverse engineering of <c>Encrypt.dll!BRCrc32</c> at offset
/// <c>0x1800015c0</c> and the identical kernel-side reimplementation at
/// <c>ammntdrv.sys!FUN_0002053c</c>. The verbatim decompilation reads:
/// <code>
/// uint BRCrc32(byte *p, uint n) {
///   uint c = 0;
///   while (n--) c = (c &gt;&gt; 8) ^ TABLE[(*p++ ^ (byte)c) &amp; 0xFF];
///   return ~c;
/// }
/// </code>
/// This is <b>standard zlib CRC-32</b>: reflected polynomial
/// <c>0xEDB88320</c>, init <c>0x00000000</c>, final XOR <c>0xFFFFFFFF</c>.
/// </para>
/// </summary>
public static class BrCrc32 {

  /// <summary>
  /// Computes the AOMEI <c>BRCrc32</c> over <paramref name="data"/>.
  /// </summary>
  /// <param name="data">The bytes to checksum.</param>
  /// <returns>The 32-bit CRC value as it would appear in the on-disk
  /// <c>Crc32</c> field.</returns>
  public static uint Compute(ReadOnlySpan<byte> data) => Crc32.Compute(data);

  /// <summary>
  /// Computes the CRC over <paramref name="record"/> with the 4-byte
  /// <see cref="AomeiConstants.Crc32FieldOffset"/> field treated as zero —
  /// matching the AOMEI reader's verification protocol:
  /// <c>saved = Head.Crc32; Head.Crc32 = 0; ASSERT(BRCrc32(...) == saved);</c>.
  /// </summary>
  /// <remarks>
  /// The record must be at least <see cref="AomeiConstants.StandardHeaderSize"/>
  /// bytes long (the 12-byte BR_STANDARD_HEADER). Throws
  /// <see cref="ArgumentException"/> if shorter.
  /// </remarks>
  public static uint ComputeWithZeroedCrc(ReadOnlySpan<byte> record) {
    if (record.Length < AomeiConstants.StandardHeaderSize)
      throw new ArgumentException(
        $"Record too small for BR_STANDARD_HEADER ({record.Length} < {AomeiConstants.StandardHeaderSize}).",
        nameof(record));
    var buf = record.ToArray();
    buf[AomeiConstants.Crc32FieldOffset + 0] = 0;
    buf[AomeiConstants.Crc32FieldOffset + 1] = 0;
    buf[AomeiConstants.Crc32FieldOffset + 2] = 0;
    buf[AomeiConstants.Crc32FieldOffset + 3] = 0;
    return Crc32.Compute(buf);
  }
}
