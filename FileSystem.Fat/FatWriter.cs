#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fat;

/// <summary>
/// Builds FAT12 / FAT16 filesystem images from scratch.
/// Auto-selects FAT type based on cluster count. Default: FAT12 1.44MB floppy.
/// Short names only (8.3).
/// <para>
/// <b>FAT32 is not supported on write.</b> When the cluster count would land in
/// FAT32 range (≥ 65525) <see cref="Build"/> throws <see cref="NotSupportedException"/>
/// rather than silently producing a broken image (the FAT32 extended BPB,
/// FSInfo sector and cluster-2 EoC marker are not emitted). FAT32 support
/// is tracked in TODO.md §11f.
/// </para>
/// </summary>
public sealed class FatWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the image. Name should be 8.3 format (e.g. "TEST.TXT").</summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>
  /// Builds the FAT filesystem image.
  /// </summary>
  /// <param name="totalSectors">Total sectors (default 2880 = 1.44MB floppy).</param>
  /// <param name="bytesPerSector">Bytes per sector (default 512).</param>
  /// <returns>Complete disk image as byte array.</returns>
  public byte[] Build(int totalSectors = 2880, int bytesPerSector = 512) {
    const int fatCount = 2;
    const int reservedSectors = 1;

    // Start with FAT12 floppy defaults
    var sectorsPerCluster = 1;
    var rootEntryCount = 224;
    var fatSize = 9; // sectors per FAT for 1.44MB floppy

    // Determine FAT type
    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    var totalDataClusters = (totalSectors - firstDataSector) / sectorsPerCluster;
    var fatType = totalDataClusters < 4085 ? 12 : totalDataClusters < 65525 ? 16 : 32;

    // Adjust parameters for FAT16/32
    if (fatType == 16) {
      sectorsPerCluster = 4;
      rootEntryCount = 512;
      rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
      fatSize = (totalSectors * 2 / bytesPerSector) + 1;
      firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    } else if (fatType == 32) {
      // The FAT32 branch is incomplete — we do not emit BPB_RootClus (offset 44),
      // BPB_FSInfo (offset 48), BPB_BkBootSec (offset 50), the FSInfo sector, the
      // extended BPB (DriveNum/BootSig/VolumeID/VolumeLabel/FileSystemType), nor
      // the EoC marker for cluster 2 (root directory). Producing a silently broken
      // image is worse than refusing — per user mandate, fail loudly. FAT32 support
      // is tracked in TODO.md §11f.
      throw new NotSupportedException(
        "FAT32 writer is incomplete: missing BPB_RootClus / BPB_FSInfo / FSInfo sector " +
        "and cluster-2 EoC marker. Use FAT16 for images < 2 GB (totalSectors such that " +
        "cluster count stays below 65525). FAT32 support is tracked in TODO.md §11f.");
    }

    var disk = new byte[totalSectors * bytesPerSector];

    // Boot sector
    disk[0] = 0xEB; disk[1] = 0x3C; disk[2] = 0x90;
    Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(disk, 3);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(11), (ushort)bytesPerSector);
    disk[13] = (byte)sectorsPerCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(14), (ushort)reservedSectors);
    disk[16] = (byte)fatCount;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(17), (ushort)rootEntryCount);
    if (totalSectors < 65536)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(19), (ushort)totalSectors);
    else
      BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(32), totalSectors);
    disk[21] = 0xF8; // media type (hard disk)
    if (fatType != 32)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(22), (ushort)fatSize);
    else
      BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(36), fatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(24), 63); // sectors per track
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(26), 255); // heads
    disk[510] = 0x55; disk[511] = 0xAA;

    // FAT initialization — media byte + end-of-chain markers for clusters 0 and 1
    var fatOffset = reservedSectors * bytesPerSector;
    if (fatType == 12) {
      disk[fatOffset] = 0xF8; disk[fatOffset + 1] = 0xFF; disk[fatOffset + 2] = 0xFF;
    } else if (fatType == 16) {
      disk[fatOffset] = 0xF8; disk[fatOffset + 1] = 0xFF;
      disk[fatOffset + 2] = 0xFF; disk[fatOffset + 3] = 0xFF;
    } else {
      BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(fatOffset), unchecked((int)0x0FFFFFF8));
      BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(fatOffset + 4), unchecked((int)0x0FFFFFFF));
    }

    // Root directory and file data
    var rootDirOffset = (reservedSectors + fatCount * fatSize) * bytesPerSector;
    var dataAreaOffset = firstDataSector * bytesPerSector;
    var nextCluster = 2;
    var dirEntryPos = rootDirOffset;
    var clusterSize = sectorsPerCluster * bytesPerSector;

    foreach (var (name, data) in _files) {
      // Generate 8.3 short name
      var dotIdx = name.LastIndexOf('.');
      var baseName = dotIdx >= 0 ? name[..dotIdx] : name;
      var ext = dotIdx >= 0 ? name[(dotIdx + 1)..] : "";
      var shortBase = baseName.ToUpperInvariant().PadRight(8)[..8];
      var shortExt = ext.ToUpperInvariant().PadRight(3)[..3];

      // Write directory entry
      Encoding.ASCII.GetBytes(shortBase).CopyTo(disk, dirEntryPos);
      Encoding.ASCII.GetBytes(shortExt).CopyTo(disk, dirEntryPos + 8);
      disk[dirEntryPos + 11] = 0x20; // Archive attribute
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirEntryPos + 26), (ushort)nextCluster);
      BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(dirEntryPos + 28), data.Length);
      dirEntryPos += 32;

      // Write file data to clusters
      var clustersNeeded = Math.Max(1, (data.Length + clusterSize - 1) / clusterSize);
      var clusterOffset = dataAreaOffset + (nextCluster - 2) * clusterSize;
      if (clusterOffset + data.Length <= disk.Length)
        data.CopyTo(disk, clusterOffset);

      // Write FAT chain
      for (var c = 0; c < clustersNeeded; c++) {
        var cluster = nextCluster + c;
        var nextVal = (c + 1 < clustersNeeded)
          ? cluster + 1
          : (fatType == 12 ? 0xFFF : fatType == 16 ? 0xFFFF : 0x0FFFFFFF);
        WriteFatEntry(disk, fatOffset, cluster, nextVal, fatType);
      }

      nextCluster += clustersNeeded;
    }

    // Copy FAT1 to FAT2
    Array.Copy(disk, fatOffset, disk, fatOffset + fatSize * bytesPerSector, fatSize * bytesPerSector);

    return disk;
  }

  /// <summary>
  /// Convenience: builds a FAT image from a list of files, auto-sizing to fit.
  /// Used by virtual disk writers (QCOW2, VHD, VMDK, VDI) to embed a filesystem
  /// inside a disk container so that Create() produces a usable volume.
  /// </summary>
  public static byte[] BuildFromFiles(IEnumerable<(string name, byte[] data)> files) {
    var w = new FatWriter();
    var totalData = 0L;
    foreach (var (name, data) in files) {
      w.AddFile(ToShortName(name), data);
      totalData += data.Length;
    }
    // Auto-size: data + ~50% overhead for FAT/root-dir/alignment, minimum 1.44 MB.
    var neededBytes = Math.Max(totalData * 3 / 2 + 32768, 1440 * 1024);
    var totalSectors = Math.Max(2880, (int)((neededBytes + 511) / 512));
    return w.Build(totalSectors);
  }

  private static string ToShortName(string name) {
    var leaf = Path.GetFileName(name);
    var dotIdx = leaf.LastIndexOf('.');
    var basePart = (dotIdx >= 0 ? leaf[..dotIdx] : leaf).ToUpperInvariant();
    var extPart = (dotIdx >= 0 ? leaf[(dotIdx + 1)..] : "").ToUpperInvariant();
    // Keep only A-Z 0-9 _ chars
    basePart = new string(basePart.Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_').ToArray());
    extPart = new string(extPart.Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_').ToArray());
    if (basePart.Length == 0) basePart = "FILE";
    if (basePart.Length > 8) basePart = basePart[..8];
    if (extPart.Length > 3) extPart = extPart[..3];
    return extPart.Length > 0 ? $"{basePart}.{extPart}" : basePart;
  }

  private static void WriteFatEntry(byte[] disk, int fatOffset, int cluster, int value, int fatType) {
    if (fatType == 12) {
      var bytePos = fatOffset + cluster * 3 / 2;
      if (bytePos + 1 >= disk.Length) return;
      if ((cluster & 1) == 0) {
        disk[bytePos] = (byte)(value & 0xFF);
        disk[bytePos + 1] = (byte)((disk[bytePos + 1] & 0xF0) | ((value >> 8) & 0x0F));
      } else {
        disk[bytePos] = (byte)((disk[bytePos] & 0x0F) | ((value << 4) & 0xF0));
        disk[bytePos + 1] = (byte)((value >> 4) & 0xFF);
      }
    } else if (fatType == 16) {
      var pos = fatOffset + cluster * 2;
      if (pos + 2 <= disk.Length)
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(pos), (ushort)value);
    } else {
      var pos = fatOffset + cluster * 4;
      if (pos + 4 <= disk.Length)
        BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(pos), value & 0x0FFFFFFF);
    }
  }
}
