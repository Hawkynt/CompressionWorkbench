#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fat;

/// <summary>
/// Builds FAT12 / FAT16 / FAT32 filesystem images from scratch per the Microsoft
/// FAT specification (FATGEN103). Auto-selects FAT type based on cluster count.
/// Short names only (8.3).
/// </summary>
/// <remarks>
/// FAT32 layout: 32 reserved sectors (boot @0, FSInfo @1, backup boot @6), two
/// FAT copies, root directory at cluster 2 with FAT entry = end-of-chain.
/// </remarks>
public sealed class FatWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the image. Name should be 8.3 format (e.g. "TEST.TXT").</summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>
  /// Builds the FAT filesystem image.
  /// </summary>
  /// <param name="totalSectors">Total sectors (default 2880 = 1.44 MB floppy).</param>
  /// <param name="bytesPerSector">Bytes per sector (default 512).</param>
  /// <returns>Complete disk image as byte array.</returns>
  public byte[] Build(int totalSectors = 2880, int bytesPerSector = 512) {
    const int fatCount = 2;

    // Start with FAT12 floppy defaults
    var reservedSectors = 1;
    var sectorsPerCluster = 1;
    var rootEntryCount = 224;
    var fatSize = 9; // sectors per FAT for 1.44MB floppy

    // Determine FAT type
    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    var totalDataClusters = (totalSectors - firstDataSector) / sectorsPerCluster;
    var fatType = totalDataClusters < 4085 ? 12 : totalDataClusters < 65525 ? 16 : 32;

    // Adjust parameters for FAT16/32.
    if (fatType == 16) {
      sectorsPerCluster = 4;
      rootEntryCount = 512;
      rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
      fatSize = (totalSectors * 2 / bytesPerSector) + 1;
      firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    } else if (fatType == 32) {
      reservedSectors = 32; // FAT32 requires >=1 but convention is 32 (leaves room for FSInfo+BackupBoot)
      rootEntryCount = 0;   // FAT32 root is in the cluster chain, not a fixed area
      rootDirSectors = 0;
      // Sectors-per-cluster heuristic from FATGEN103 table.
      sectorsPerCluster = totalSectors < 66600 ? 1
        : totalSectors < 532480 ? 1      // up to 260 MB, 512-byte clusters ⇒ 1 spc
        : totalSectors < 16777216 ? 8    // up to 8 GB ⇒ 4 KB clusters
        : totalSectors < 33554432 ? 16
        : totalSectors < 67108864 ? 32
        : 64;
      // Estimate FAT size: (data sectors / spc) entries × 4 bytes each, rounded up.
      var dataSectorsEstimate = totalSectors - reservedSectors;
      var dataClustersEstimate = dataSectorsEstimate / sectorsPerCluster;
      fatSize = (dataClustersEstimate * 4 + bytesPerSector - 1) / bytesPerSector;
      firstDataSector = reservedSectors + fatCount * fatSize;
    }

    var disk = new byte[(long)totalSectors * bytesPerSector];

    // ── Boot sector (shared base) ──────────────────────────────────────────
    if (fatType == 32) { disk[0] = 0xEB; disk[1] = 0x58; disk[2] = 0x90; }
    else { disk[0] = 0xEB; disk[1] = 0x3C; disk[2] = 0x90; }
    Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(disk, 3);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(11), (ushort)bytesPerSector);
    disk[13] = (byte)sectorsPerCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(14), (ushort)reservedSectors);
    disk[16] = (byte)fatCount;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(17), (ushort)rootEntryCount);
    if (fatType != 32 && totalSectors < 65536)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(19), (ushort)totalSectors);
    else
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(32), (uint)totalSectors);
    disk[21] = 0xF8; // media: fixed / hard disk
    if (fatType != 32)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(22), (ushort)fatSize);
    // (FAT32 writes fat_size_32 at offset 36 below.)
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(24), 63); // sectors per track
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(26), 255); // heads
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(28), 0u);  // hidden sectors

    if (fatType == 32) {
      // ── FAT32 extended BPB ───────────────────────────────────────────────
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(36), (uint)fatSize);   // BPB_FATSz32
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(40), 0);               // BPB_ExtFlags: mirror
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(42), 0);               // BPB_FSVer: 0.0
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(44), 2u);              // BPB_RootClus: root at cluster 2
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(48), 1);               // BPB_FSInfo: sector 1
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(50), 6);               // BPB_BkBootSec: backup at sector 6
      // 52-63 reserved (already zero)
      disk[64] = 0x80;                                                             // BS_DrvNum
      disk[66] = 0x29;                                                             // BS_BootSig: extended BPB present
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(67), 0x12345678u);     // BS_VolID
      Encoding.ASCII.GetBytes("NO NAME    ").CopyTo(disk, 71);                     // BS_VolLab (11 bytes)
      Encoding.ASCII.GetBytes("FAT32   ").CopyTo(disk, 82);                        // BS_FilSysType (8 bytes)
    } else {
      // Short extended BPB (FAT12/16)
      disk[36] = 0x80;
      disk[38] = 0x29;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(39), 0x12345678u);
      Encoding.ASCII.GetBytes("NO NAME    ").CopyTo(disk, 43);
      Encoding.ASCII.GetBytes(fatType == 12 ? "FAT12   " : "FAT16   ").CopyTo(disk, 54);
    }

    disk[510] = 0x55; disk[511] = 0xAA;

    // ── FAT32 FSInfo sector (sector 1) ───────────────────────────────────
    if (fatType == 32) {
      var fsInfo = 1 * bytesPerSector;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo), 0x41615252u);           // FSI_LeadSig
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 484), 0x61417272u);     // FSI_StrucSig
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 488), 0xFFFFFFFFu);     // FSI_Free_Count = unknown
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 492), 0xFFFFFFFFu);     // FSI_Nxt_Free = unknown
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 508), 0xAA550000u);     // FSI_TrailSig

      // ── Backup boot sector (sector 6) ──────────────────────────────────
      var bkOff = 6 * bytesPerSector;
      Array.Copy(disk, 0, disk, bkOff, bytesPerSector);
      // Backup FSInfo (sector 7)
      var bkFsInfo = 7 * bytesPerSector;
      Array.Copy(disk, fsInfo, disk, bkFsInfo, bytesPerSector);
    }

    // ── FAT initialisation: media byte + EoC markers for clusters 0 and 1 ─
    var fatOffset = reservedSectors * bytesPerSector;
    if (fatType == 12) {
      disk[fatOffset] = 0xF8; disk[fatOffset + 1] = 0xFF; disk[fatOffset + 2] = 0xFF;
    } else if (fatType == 16) {
      disk[fatOffset] = 0xF8; disk[fatOffset + 1] = 0xFF;
      disk[fatOffset + 2] = 0xFF; disk[fatOffset + 3] = 0xFF;
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset), 0x0FFFFFF8u);
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 4), 0x0FFFFFFFu);
    }

    // ── Root directory and file data ──────────────────────────────────────
    var clusterSize = sectorsPerCluster * bytesPerSector;

    int dirEntryPos;
    int nextCluster;
    if (fatType == 32) {
      // Root lives in the cluster chain at cluster 2 — allocate it up-front
      // and mark its FAT entry as end-of-chain. Place directory entries
      // inside the cluster-2 region.
      var rootStart = firstDataSector * bytesPerSector;
      dirEntryPos = rootStart;
      WriteFatEntry(disk, fatOffset, 2, 0x0FFFFFFF, fatType);
      nextCluster = 3;
    } else {
      dirEntryPos = (reservedSectors + fatCount * fatSize) * bytesPerSector;
      nextCluster = 2;
    }
    var dataAreaOffset = firstDataSector * bytesPerSector;
    if (fatType == 32) {
      // For FAT32 the first cluster (2) is the root directory itself, so file
      // clusters start at cluster 2 + 1 round(rootDir / cluster). We used a
      // single cluster for the root above, so data files start at cluster 3.
      // dataAreaOffset already points at cluster 2; cluster 3 follows it.
    }

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
      // For FAT32 first-cluster-high lives at offset 20, low at 26.
      if (fatType == 32) {
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirEntryPos + 20), (ushort)((nextCluster >> 16) & 0xFFFF));
      }
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirEntryPos + 26), (ushort)(nextCluster & 0xFFFF));
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirEntryPos + 28), (uint)data.Length);
      dirEntryPos += 32;

      // Write file data to clusters
      var clustersNeeded = Math.Max(1, (data.Length + clusterSize - 1) / clusterSize);
      var clusterOffset = dataAreaOffset + (long)(nextCluster - 2) * clusterSize;
      if (clusterOffset + data.Length <= disk.Length && data.Length > 0)
        Buffer.BlockCopy(data, 0, disk, (int)clusterOffset, data.Length);

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
    Buffer.BlockCopy(disk, fatOffset, disk, fatOffset + fatSize * bytesPerSector, fatSize * bytesPerSector);

    return disk;
  }

  /// <summary>
  /// Convenience: builds a FAT image from a list of files, auto-sizing to fit.
  /// Used by virtual-disk writers (QCOW2, VHD, VMDK, VDI) to embed a filesystem
  /// inside a disk container so that Create() produces a usable volume.
  /// </summary>
  public static byte[] BuildFromFiles(IEnumerable<(string name, byte[] data)> files) {
    var w = new FatWriter();
    var totalData = 0L;
    foreach (var (name, data) in files) {
      w.AddFile(ToShortName(name), data);
      totalData += data.Length;
    }
    // Auto-size: data + ~50% overhead, minimum 1.44 MB.
    var neededBytes = Math.Max(totalData * 3 / 2 + 32768, 1440 * 1024);
    var totalSectors = Math.Max(2880, (int)((neededBytes + 511) / 512));
    return w.Build(totalSectors);
  }

  private static string ToShortName(string name) {
    var leaf = Path.GetFileName(name);
    var dotIdx = leaf.LastIndexOf('.');
    var basePart = (dotIdx >= 0 ? leaf[..dotIdx] : leaf).ToUpperInvariant();
    var extPart = (dotIdx >= 0 ? leaf[(dotIdx + 1)..] : "").ToUpperInvariant();
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
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(pos), (uint)value & 0x0FFFFFFFu);
    }
  }
}
