namespace Compression.Core.DiskImage;

/// <summary>
/// Wraps a raw filesystem image in a minimal MBR + single primary partition so that
/// Windows' disk-attach path, <c>diskpart</c>, and typical VM managers recognise
/// it as a partitioned disk. Without this wrapping, the only layout Windows mounts
/// from a raw FS image is FAT "superfloppy" — NTFS and exFAT require a partition
/// table.
/// </summary>
/// <remarks>
/// Use when producing a VHD/VMDK/VDI/Qcow2 whose caller wants the guest OS (or
/// Windows' disk-management tools) to see a mountable partition, not a bare FS.
/// Callers that already built their own partition table should skip this helper.
/// </remarks>
public static class MbrWrapper {
  /// <summary>MBR partition-type bytes for common filesystems.</summary>
  public static class PartitionType {
#pragma warning disable CS1591
    public const byte Fat12 = 0x01;
    public const byte Fat16Small = 0x04;
    public const byte Fat16 = 0x06;
    public const byte NtfsExfat = 0x07;
    public const byte Fat32Chs = 0x0B;
    public const byte Fat32Lba = 0x0C;
    public const byte Fat16Lba = 0x0E;
    public const byte Linux = 0x83;
#pragma warning restore CS1591
  }

  /// <summary>
  /// Returns a new image containing: an MBR sector at offset&#160;0 (with a single
  /// primary partition entry covering the whole payload, starting at LBA&#160;2048),
  /// 2047 zero-filled sectors of alignment padding, then the supplied <paramref
  /// name="rawFilesystemImage"/>.
  /// </summary>
  /// <param name="rawFilesystemImage">A filesystem image (FAT, NTFS, exFAT, ext, …).</param>
  /// <param name="partitionType">MBR partition-type byte; use constants in <see cref="PartitionType"/>.</param>
  /// <param name="active">If true the entry gets the 0x80 active/bootable marker.</param>
  public static byte[] Wrap(byte[] rawFilesystemImage, byte partitionType, bool active = false) {
    ArgumentNullException.ThrowIfNull(rawFilesystemImage);
    const int sectorSize = 512;
    // 1 MiB (2048 sectors) of lead-in is the conventional alignment used by Windows' partitioner.
    const int partitionStartLba = 2048;
    var partitionStartBytes = partitionStartLba * sectorSize;

    var partitionSectors = (rawFilesystemImage.Length + sectorSize - 1) / sectorSize;
    var totalSize = partitionStartBytes + partitionSectors * sectorSize;
    var result = new byte[totalSize];

    // --- MBR at offset 0 ---
    // Bytes 0..445 are reserved for boot code; leaving them zero produces a non-bootable disk,
    // which Windows' disk-management still mounts when the partition table is valid.

    // Partition entry 1 at offset 446 (16 bytes)
    var p = result.AsSpan(446);
    p[0] = active ? (byte)0x80 : (byte)0x00;
    // CHS fields: 0xFE 0xFF 0xFF is the conventional "use LBA" sentinel per EDD spec.
    p[1] = 0xFE; p[2] = 0xFF; p[3] = 0xFF;
    p[4] = partitionType;
    p[5] = 0xFE; p[6] = 0xFF; p[7] = 0xFF;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p[8..], partitionStartLba);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p[12..], (uint)partitionSectors);

    // Entries 2-4 (offsets 462/478/494, 16 bytes each) remain zeroed.

    // Boot signature at 510/511.
    result[510] = 0x55;
    result[511] = 0xAA;

    // --- Payload at LBA 2048 ---
    rawFilesystemImage.CopyTo(result, partitionStartBytes);
    return result;
  }
}
