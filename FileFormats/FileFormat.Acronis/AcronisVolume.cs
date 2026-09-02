#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Acronis;

/// <summary>
/// Volume-header parser for Acronis True Image classic .tib backups.
/// </summary>
/// <remarks>
/// <para>
/// Layout per upstream RE (https://github.com/dennisss/acronis-tib, src/volume.ts +
/// src/win/volume.ts + src/mac/volume.ts):
/// </para>
/// <list type="bullet">
///   <item><description>Bytes 0..3 = magic <c>CE 24 B9 A2</c> (little-endian 0xA2B924CE).</description></item>
///   <item><description>Bytes 4..5 = header length (uint16 LE). Windows = 0x20 (32 bytes), Mac = 0x24 (36 bytes).</description></item>
///   <item><description>Bytes 6..7 = volume version (0 = Windows, 1 = Mac).</description></item>
///   <item><description>Bytes 8..19 = three 4-byte random identifiers (archive key, slice key, volume key).</description></item>
///   <item><description>Bytes 20..23 = sequence number (uint32 LE). Mac volumes always have sequence = 1.</description></item>
///   <item><description>Bytes 24..27 = Adler-32 checksum.</description></item>
///   <item><description>Bytes 28..31 = block size (uint32 LE). Windows = 32, Mac = 4096.</description></item>
/// </list>
/// <para>
/// Encrypted volumes use the same outer header — encryption affects the record stream payload.
/// </para>
/// </remarks>
public enum AcronisVolumeVersion {
  /// <summary>
  /// Specifies the windows option.
  /// </summary>
  Windows = 0,
  /// <summary>
  /// Specifies the mac option.
  /// </summary>
  Mac = 1,
}

/// <summary>
/// Represents an acronis volume header.
/// </summary>
public sealed record AcronisVolumeHeader(
  ushort HeaderLength,
  AcronisVolumeVersion Version,
  uint ArchiveKey,
  uint SliceKey,
  uint VolumeKey,
  uint Sequence,
  uint Adler32,
  uint BlockSize
) {

  /// <summary>
  /// Defines the magic constant value.
  /// </summary>
  public const uint Magic = 0xA2B924CE;

  /// <summary>Reads the volume header from the start of <paramref name="stream"/>.</summary>
  public static AcronisVolumeHeader Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    Span<byte> hdr = stackalloc byte[36];
    stream.Position = 0;
    var read = stream.Read(hdr);
    if (read < 32) throw new InvalidDataException("Acronis: file too small for volume header.");

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
    if (magic != Magic) throw new InvalidDataException($"Acronis: bad magic 0x{magic:X8} (expected 0xA2B924CE).");

    var length = BinaryPrimitives.ReadUInt16LittleEndian(hdr[4..]);
    var version = (AcronisVolumeVersion)BinaryPrimitives.ReadUInt16LittleEndian(hdr[6..]);
    var archiveKey = BinaryPrimitives.ReadUInt32LittleEndian(hdr[8..]);
    var sliceKey = BinaryPrimitives.ReadUInt32LittleEndian(hdr[12..]);
    var volumeKey = BinaryPrimitives.ReadUInt32LittleEndian(hdr[16..]);
    var sequence = BinaryPrimitives.ReadUInt32LittleEndian(hdr[20..]);
    var adler = BinaryPrimitives.ReadUInt32LittleEndian(hdr[24..]);
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[28..]);

    return new AcronisVolumeHeader(length, version, archiveKey, sliceKey, volumeKey, sequence, adler, blockSize);
  }
}
