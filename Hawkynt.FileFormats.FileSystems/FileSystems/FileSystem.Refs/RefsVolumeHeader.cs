#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Parsed Microsoft ReFS volume boot record (VBR).
///
/// ReFS 3.x keeps the geometry in the fixed FSRS record at offset 0x10:
/// total sectors at 0x18, bytes/sector at 0x20, sectors/cluster at 0x24,
/// version at 0x28, checksum selector at 0x2A and the 64-MiB container size
/// at 0x40.  These fields are needed before any metadata-page or container
/// address can be interpreted.
/// </summary>
internal sealed class RefsVolumeHeader {
  public static readonly byte[] FsSignature = "ReFS\0\0\0\0"u8.ToArray();
  public static readonly byte[] FsrsSignature = "FSRS"u8.ToArray();

  public bool Valid { get; init; }
  public string OemId { get; init; } = "";
  public bool FsrsFound { get; init; }
  public int FsrsOffset { get; init; }
  public uint FsrsLength { get; init; }
  public ushort FsrsCheckSum { get; init; }
  public bool FsrsChecksumValid { get; init; }
  public ushort SectorSize { get; init; }
  public uint SectorsPerCluster { get; init; }
  public uint BytesPerCluster { get; init; }
  public ulong TotalSectors { get; init; }
  public uint MajorVersion { get; init; }
  public uint MinorVersion { get; init; }
  public ushort ChecksumAlgorithm { get; init; }
  public uint VolumeFlags { get; init; }
  public ulong VolumeSerialNumber { get; init; }
  public ulong BytesPerContainer { get; init; }
  public byte[] ExtendedGuid { get; init; } = [];
  public byte[] RawBytes { get; init; } = [];

  public ulong TotalClusters => this.SectorsPerCluster == 0 ? 0 : this.TotalSectors / this.SectorsPerCluster;

  public static RefsVolumeHeader TryParse(ReadOnlySpan<byte> image) {
    if (image.Length < 512 || !image.Slice(3, 8).SequenceEqual(FsSignature))
      return new RefsVolumeHeader();

    var raw = image[..512].ToArray();
    var oem = Encoding.ASCII.GetString(image.Slice(3, 8)).TrimEnd('\0');

    // ReFS 3.x has one fixed FSRS VBR descriptor at 0x10.  Do not scan for a
    // coincidental string inside the boot sector: doing so used to interpret
    // arbitrary bytes as geometry on damaged images.
    const int fsrsOffset = 0x10;
    var fsrsFound = image.Slice(fsrsOffset, 4).SequenceEqual(FsrsSignature);
    if (!fsrsFound) {
      return new RefsVolumeHeader {
        OemId = oem,
        RawBytes = raw,
      };
    }

    var vbrSize = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(0x14, 2));
    var storedChecksum = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(0x16, 2));
    var totalSectors = BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(0x18, 8));
    var bytesPerSector32 = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(0x20, 4));
    var sectorsPerCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(0x24, 4));
    var major = image[0x28];
    var minor = image[0x29];
    var checksumAlgorithm = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(0x2A, 2));
    var volumeFlags = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(0x2C, 4));
    var serial = BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(0x38, 8));
    var bytesPerContainer = BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(0x40, 8));
    var guid = image.Slice(0x48, 16).ToArray();

    var geometryValid = vbrSize == 0x200
      && bytesPerSector32 is > 0 and <= ushort.MaxValue
      && sectorsPerCluster > 0
      && bytesPerContainer > 0
      && totalSectors > 0;

    ulong clusterSize64 = (ulong)bytesPerSector32 * sectorsPerCluster;
    if (clusterSize64 > uint.MaxValue)
      geometryValid = false;

    return new RefsVolumeHeader {
      Valid = geometryValid,
      OemId = oem,
      FsrsFound = true,
      FsrsOffset = fsrsOffset,
      FsrsLength = vbrSize,
      FsrsCheckSum = storedChecksum,
      FsrsChecksumValid = ComputeVbrChecksum(image[..512]) == storedChecksum,
      SectorSize = (ushort)Math.Min(bytesPerSector32, ushort.MaxValue),
      SectorsPerCluster = sectorsPerCluster,
      BytesPerCluster = geometryValid ? (uint)clusterSize64 : 0,
      TotalSectors = totalSectors,
      MajorVersion = major,
      MinorVersion = minor,
      ChecksumAlgorithm = checksumAlgorithm,
      VolumeFlags = volumeFlags,
      VolumeSerialNumber = serial,
      BytesPerContainer = bytesPerContainer,
      ExtendedGuid = guid,
      RawBytes = raw,
    };
  }

  /// <summary>
  /// ReFS boot-sector checksum: rotate the 16-bit accumulator right by one and
  /// add each byte from offsets 3..511, omitting the checksum field itself.
  /// </summary>
  internal static ushort ComputeVbrChecksum(ReadOnlySpan<byte> vbr) {
    if (vbr.Length < 512) return 0;
    ushort sum = 0;
    for (var i = 3; i < 512; ++i) {
      if (i is 0x16 or 0x17) continue;
      sum = (ushort)((sum >> 1) | (sum << 15));
      sum = unchecked((ushort)(sum + vbr[i]));
    }
    return sum;
  }
}
