#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Parsed Microsoft ReFS volume boot record. Layout matches the reverse-engineered
/// on-disk header used by Andrea Allievi and the libfsrefs research, which mirrors
/// the NTFS BPB at offsets 0x00..0x0B then deviates: at offset 0x03 the OEM-id
/// reads "ReFS\0\0\0\0" (the 8-byte FS signature) and at 0x46 the FSRS structure
/// is followed by the version word, sector size, cluster size, and cluster counts.
/// </summary>
internal sealed class RefsVolumeHeader {
  // The OEM-ID "ReFS\0\0\0\0" lives at offset 3 — the same slot NTFS uses for "NTFS    ".
  public static readonly byte[] FsSignature = "ReFS\0\0\0\0"u8.ToArray();
  // FSRS structure marker further into the boot sector (offset 0x46 in most builds).
  public static readonly byte[] FsrsSignature = "FSRS"u8.ToArray();

  public bool Valid { get; init; }
  public string OemId { get; init; } = "";
  public bool FsrsFound { get; init; }
  public int FsrsOffset { get; init; }
  public uint FsrsLength { get; init; }
  public ushort FsrsCheckSum { get; init; }
  public ushort SectorSize { get; init; }
  public uint BytesPerCluster { get; init; }
  public ulong TotalSectors { get; init; }
  public uint MajorVersion { get; init; }
  public uint MinorVersion { get; init; }
  public byte[] RawBytes { get; init; } = [];

  public static RefsVolumeHeader TryParse(ReadOnlySpan<byte> image) {
    if (image.Length < 512) return new RefsVolumeHeader();

    // Check OEM-id at offset 3.
    if (!image.Slice(3, 8).SequenceEqual(FsSignature)) return new RefsVolumeHeader();

    var oem = Encoding.ASCII.GetString(image.Slice(3, 8)).TrimEnd('\0');
    var raw = image.Slice(0, Math.Min(512, image.Length)).ToArray();
    if (raw.Length < 512) {
      var padded = new byte[512];
      raw.CopyTo(padded, 0);
      raw = padded;
    }

    // BPB-style fields shared with NTFS:
    //   0x0B = bytes_per_sector (u16)
    //   0x0D = sectors_per_cluster (u8)  — for ReFS this is often 0 with the cluster size
    //                                       living in a dedicated u32 further in
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(0x0B, 2));

    // ReFS-specific block: scan the first 512 bytes for "FSRS"; bail out gracefully if not present.
    int fsrsOffset = -1;
    for (var i = 0; i + 4 <= 512 && i + 4 <= image.Length; i++) {
      if (image[i] == 0x46 && image[i + 1] == 0x53 && image[i + 2] == 0x52 && image[i + 3] == 0x53) {
        fsrsOffset = i;
        break;
      }
    }

    uint fsrsLen = 0;
    ushort fsrsChk = 0;
    uint major = 0, minor = 0;
    uint bytesPerCluster = 0;
    ulong totalSectors = 0;

    if (fsrsOffset >= 0 && fsrsOffset + 0x20 <= image.Length) {
      // After "FSRS": u32 length, u16 checksum, u16 reserved, u64 total_sectors,
      //   u32 bytes_per_sector, u32 bytes_per_cluster, u8 major, u8 minor (per Allievi)
      fsrsLen = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(fsrsOffset + 4, 4));
      fsrsChk = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(fsrsOffset + 8, 2));
      totalSectors = BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(fsrsOffset + 12, 8));
      bytesPerCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(fsrsOffset + 24, 4));
      major = image[fsrsOffset + 28];
      minor = image[fsrsOffset + 29];
    }

    return new RefsVolumeHeader {
      Valid = true,
      OemId = oem,
      FsrsFound = fsrsOffset >= 0,
      FsrsOffset = fsrsOffset,
      FsrsLength = fsrsLen,
      FsrsCheckSum = fsrsChk,
      SectorSize = bytesPerSector,
      BytesPerCluster = bytesPerCluster,
      TotalSectors = totalSectors,
      MajorVersion = major,
      MinorVersion = minor,
      RawBytes = raw,
    };
  }
}
