#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Vhdx;

/// <summary>
/// Reader for Hyper-V VHDX virtual hard-disk images (MS-VHDX v1). Splits the
/// file into the 64KB File Type Identifier region, the two 64KB header copies,
/// and the two 64KB region tables. Full block-level decompression is out of
/// scope; this first pass surfaces enough structural state for identification
/// and comparison against reference parsers like qemu-img.
/// </summary>
/// <remarks>
/// Per [MS-VHDX], the first 1 MiB of the file is reserved for metadata and laid
/// out at fixed 64 KiB boundaries:
/// <code>
///   0x000000  File Type Identifier  (64 KiB)
///   0x010000  Header 1              (64 KiB)
///   0x020000  Header 2              (64 KiB)
///   0x030000  Region Table 1        (64 KiB)
///   0x040000  Region Table 2        (64 KiB)
///   0x050000  (reserved)            (10 × 64 KiB)
///   0x100000  Log + BAT + metadata + raw blocks
/// </code>
/// The File Type Identifier begins with the 8-byte ASCII signature "vhdxfile"
/// followed by a 512-entry UTF-16LE Creator field. The Header structure begins
/// with the 4-byte ASCII signature "head".
/// </remarks>
public sealed class VhdxReader {

  public const int FileTypeIdentifierOffset = 0x00000;
  public const int Header1Offset = 0x10000;
  public const int Header2Offset = 0x20000;
  public const int RegionTable1Offset = 0x30000;
  public const int RegionTable2Offset = 0x40000;
  public const int RegionSize = 0x10000;  // 64 KiB

  public static readonly byte[] FileSignature = "vhdxfile"u8.ToArray();
  public static readonly byte[] HeaderSignature = "head"u8.ToArray();
  public static readonly byte[] RegionTableSignature = "regi"u8.ToArray();

  public sealed record VhdxImage(
    string Creator,
    byte[] FileTypeIdentifier,   // first 64 KiB
    byte[] HeaderPrimary,        // 64 KiB at 0x10000
    byte[] HeaderBackup,         // 64 KiB at 0x20000
    byte[] RegionTablePrimary,   // 64 KiB at 0x30000
    byte[] RegionTableBackup,    // 64 KiB at 0x40000
    HeaderInfo? PrimaryHeaderInfo,
    HeaderInfo? BackupHeaderInfo,
    long TotalFileSize);

  public sealed record HeaderInfo(
    uint Checksum,
    ulong SequenceNumber,
    Guid FileWriteGuid,
    Guid DataWriteGuid,
    Guid LogGuid,
    ushort LogVersion,
    ushort Version,
    uint LogLength,
    ulong LogOffset);

  public static VhdxImage Read(ReadOnlySpan<byte> data) {
    if (data.Length < Header1Offset + RegionSize)
      throw new InvalidDataException("VHDX: file is shorter than the mandatory 128 KiB File Type Identifier + Header 1.");

    if (!data[..8].SequenceEqual(FileSignature))
      throw new InvalidDataException("VHDX: invalid file signature (expected 'vhdxfile' at offset 0).");

    // Creator: 512 UTF-16LE code units at offset 8 — 1024 bytes total.
    var creatorBytes = data.Slice(8, 1024);
    var creator = Encoding.Unicode.GetString(creatorBytes).TrimEnd('\0');

    var fileTypeId = data.Slice(FileTypeIdentifierOffset, RegionSize).ToArray();
    var header1 = SafeSlice(data, Header1Offset, RegionSize);
    var header2 = SafeSlice(data, Header2Offset, RegionSize);
    var regionTable1 = SafeSlice(data, RegionTable1Offset, RegionSize);
    var regionTable2 = SafeSlice(data, RegionTable2Offset, RegionSize);

    var primary = ParseHeader(header1);
    var backup = ParseHeader(header2);

    return new VhdxImage(
      Creator: creator,
      FileTypeIdentifier: fileTypeId,
      HeaderPrimary: header1,
      HeaderBackup: header2,
      RegionTablePrimary: regionTable1,
      RegionTableBackup: regionTable2,
      PrimaryHeaderInfo: primary,
      BackupHeaderInfo: backup,
      TotalFileSize: data.Length);
  }

  private static byte[] SafeSlice(ReadOnlySpan<byte> data, int offset, int size) {
    if (offset >= data.Length) return [];
    var avail = Math.Min(size, data.Length - offset);
    var buf = new byte[size];
    data.Slice(offset, avail).CopyTo(buf);
    return buf;
  }

  private static HeaderInfo? ParseHeader(ReadOnlySpan<byte> region) {
    if (region.Length < 76) return null;
    if (!region[..4].SequenceEqual(HeaderSignature)) return null;

    var checksum = BinaryPrimitives.ReadUInt32LittleEndian(region[4..]);
    var sequence = BinaryPrimitives.ReadUInt64LittleEndian(region[8..]);
    var fileWriteGuid = new Guid(region.Slice(16, 16));
    var dataWriteGuid = new Guid(region.Slice(32, 16));
    var logGuid = new Guid(region.Slice(48, 16));
    var logVersion = BinaryPrimitives.ReadUInt16LittleEndian(region[64..]);
    var version = BinaryPrimitives.ReadUInt16LittleEndian(region[66..]);
    var logLength = BinaryPrimitives.ReadUInt32LittleEndian(region[68..]);
    var logOffset = BinaryPrimitives.ReadUInt64LittleEndian(region[72..]);

    return new HeaderInfo(
      Checksum: checksum,
      SequenceNumber: sequence,
      FileWriteGuid: fileWriteGuid,
      DataWriteGuid: dataWriteGuid,
      LogGuid: logGuid,
      LogVersion: logVersion,
      Version: version,
      LogLength: logLength,
      LogOffset: logOffset);
  }
}
