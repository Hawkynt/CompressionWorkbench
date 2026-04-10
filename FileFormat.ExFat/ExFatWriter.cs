#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.ExFat;

/// <summary>
/// Builds minimal exFAT filesystem images.
/// Default: 8MB image, 512 bytes/sector, 8 sectors/cluster (4KB clusters).
/// VBR at sector 0, backup VBR at sector 12, FAT at sector 24,
/// cluster heap after FAT, root directory at cluster 2,
/// allocation bitmap at cluster 3, up-case table at cluster 4.
/// </summary>
public sealed class ExFatWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the image.</summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>Builds the exFAT filesystem image.</summary>
  /// <param name="totalSizeMB">Total image size in MB (default 8).</param>
  /// <returns>Complete disk image as byte array.</returns>
  public byte[] Build(int totalSizeMB = 8) {
    const int bytesPerSector = 512;
    const int sectorsPerClusterShift = 3; // 2^3 = 8 sectors per cluster
    const int sectorsPerCluster = 1 << sectorsPerClusterShift;
    const int clusterSize = bytesPerSector * sectorsPerCluster; // 4096
    const int bytesPerSectorShift = 9; // 2^9 = 512
    const int fatOffsetSectors = 24;
    const int fatCount = 1;

    var totalBytes = totalSizeMB * 1024 * 1024;
    var totalSectors = totalBytes / bytesPerSector;

    // FAT: 4 bytes per cluster entry
    var fatLengthSectors = 1; // start with 1, expand if needed
    var clusterHeapOffsetSectors = fatOffsetSectors + fatLengthSectors;
    var clusterCount = (totalSectors - clusterHeapOffsetSectors) / sectorsPerCluster;

    // Recalculate FAT size to fit all clusters
    var fatBytesNeeded = (clusterCount + 2) * 4; // clusters 0,1 are reserved
    fatLengthSectors = ((int)fatBytesNeeded + bytesPerSector - 1) / bytesPerSector;
    clusterHeapOffsetSectors = fatOffsetSectors + fatLengthSectors;
    clusterCount = (totalSectors - clusterHeapOffsetSectors) / sectorsPerCluster;

    var disk = new byte[totalBytes];

    // === VBR (sector 0) ===
    WriteVbr(disk, 0, bytesPerSector, bytesPerSectorShift, sectorsPerClusterShift,
      fatOffsetSectors, fatLengthSectors, clusterHeapOffsetSectors,
      (uint)clusterCount, totalSectors, fatCount);

    // === Backup VBR (sector 12) ===
    WriteVbr(disk, 12 * bytesPerSector, bytesPerSector, bytesPerSectorShift, sectorsPerClusterShift,
      fatOffsetSectors, fatLengthSectors, clusterHeapOffsetSectors,
      (uint)clusterCount, totalSectors, fatCount);

    // === FAT ===
    var fatOffset = fatOffsetSectors * bytesPerSector;
    // Cluster 0: media type
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset), 0xFFFFFFF8);
    // Cluster 1: end of chain marker
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 4), 0xFFFFFFFF);

    // Reserve clusters: 2=root dir, 3=alloc bitmap, 4=upcase table
    var nextCluster = 5u;
    // Mark cluster 2 (root dir) as end of chain initially
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 2 * 4), 0xFFFFFFF8);
    // Mark cluster 3 (alloc bitmap) as end of chain
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 3 * 4), 0xFFFFFFF8);
    // Mark cluster 4 (upcase table) as end of chain
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 4 * 4), 0xFFFFFFF8);

    var clusterHeapOffset = clusterHeapOffsetSectors * bytesPerSector;

    // === Allocation Bitmap (cluster 3) ===
    var bitmapCluster = 3u;
    var bitmapSize = ((int)clusterCount + 7) / 8;
    // We'll fill the bitmap at the end

    // === Up-Case Table (cluster 4) ===
    var upcaseCluster = 4u;
    // Minimal identity mapping for ASCII (128 entries, 2 bytes each = 256 bytes)
    var upcaseSize = 128 * 2;
    var upcaseOffset = clusterHeapOffset + (int)(upcaseCluster - 2) * clusterSize;
    for (var i = 0; i < 128; i++) {
      var ch = (ushort)(i >= 'a' && i <= 'z' ? i - 32 : i);
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(upcaseOffset + i * 2), ch);
    }

    // === Root Directory (cluster 2) ===
    var rootOffset = clusterHeapOffset + (int)(2 - 2) * clusterSize;
    var dirPos = rootOffset;

    // Volume Label entry (0x83) — optional but standard
    disk[dirPos] = 0x83; // entry type
    disk[dirPos + 1] = 0; // character count (no label)
    dirPos += 32;

    // Allocation Bitmap entry (0x81)
    disk[dirPos] = 0x81;
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 20), bitmapCluster);
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(dirPos + 24), bitmapSize);
    dirPos += 32;

    // Up-Case Table entry (0x82)
    disk[dirPos] = 0x82;
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 20), upcaseCluster);
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(dirPos + 24), upcaseSize);
    dirPos += 32;

    // === Write file entries ===
    foreach (var (name, data) in _files) {
      // Allocate clusters for file data
      var clustersNeeded = Math.Max(1, (data.Length + clusterSize - 1) / clusterSize);
      var fileFirstCluster = nextCluster;

      // Write data to clusters
      var dataOffset = clusterHeapOffset + (int)(fileFirstCluster - 2) * clusterSize;
      if (dataOffset + data.Length <= disk.Length)
        data.CopyTo(disk, dataOffset);

      // Write FAT chain
      for (var c = 0; c < clustersNeeded; c++) {
        var cluster = fileFirstCluster + (uint)c;
        var nextVal = (c + 1 < clustersNeeded) ? cluster + 1 : 0xFFFFFFF8;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + (int)cluster * 4), nextVal);
      }

      nextCluster += (uint)clustersNeeded;

      // Write directory entry set: File (0x85) + Stream Extension (0xC0) + File Name (0xC1)
      var nameChars = name.ToCharArray();
      var nameEntries = (nameChars.Length + 14) / 15;
      var secondaryCount = 1 + nameEntries; // stream ext + name entries

      // File entry (0x85)
      disk[dirPos] = 0x85;
      disk[dirPos + 1] = (byte)secondaryCount;
      // Attributes: archive (0x20)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirPos + 4), 0x20);
      dirPos += 32;

      // Stream Extension (0xC0)
      disk[dirPos] = 0xC0;
      disk[dirPos + 1] = 0; // general secondary flags
      disk[dirPos + 3] = (byte)nameChars.Length; // name length
      // Name hash
      var nameHash = ComputeNameHash(name);
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirPos + 4), nameHash);
      // Valid data length
      BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(dirPos + 8), data.Length);
      // First cluster
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 20), fileFirstCluster);
      // Data length
      BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(dirPos + 24), data.Length);
      dirPos += 32;

      // File Name entries (0xC1) — 15 UTF-16LE chars per entry
      for (var n = 0; n < nameEntries; n++) {
        disk[dirPos] = 0xC1;
        disk[dirPos + 1] = 0; // general secondary flags
        var startChar = n * 15;
        var charsToWrite = Math.Min(15, nameChars.Length - startChar);
        for (var c = 0; c < charsToWrite; c++) {
          BinaryPrimitives.WriteUInt16LittleEndian(
            disk.AsSpan(dirPos + 2 + c * 2), nameChars[startChar + c]);
        }
        dirPos += 32;
      }
    }

    // === Fill Allocation Bitmap ===
    var bitmapOffset = clusterHeapOffset + (int)(bitmapCluster - 2) * clusterSize;
    // Mark all used clusters (2 through nextCluster-1)
    for (var c = 2u; c < nextCluster; c++) {
      var bitIndex = (int)(c - 2);
      var byteIndex = bitIndex / 8;
      var bitPos = bitIndex % 8;
      if (bitmapOffset + byteIndex < disk.Length)
        disk[bitmapOffset + byteIndex] |= (byte)(1 << bitPos);
    }

    return disk;
  }

  private static void WriteVbr(byte[] disk, int offset, int bytesPerSector,
    int bytesPerSectorShift, int sectorsPerClusterShift,
    int fatOffsetSectors, int fatLengthSectors, int clusterHeapOffsetSectors,
    uint clusterCount, int totalSectors, int fatCount) {
    // Jump boot code
    disk[offset] = 0xEB; disk[offset + 1] = 0x76; disk[offset + 2] = 0x90;
    // "EXFAT   " at offset 3
    Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(disk, offset + 3);
    // Partition offset (uint64 at 64) — 0 for whole-disk images
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(offset + 64), 0);
    // Volume length in sectors (uint64 at 72)
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(offset + 72), (ulong)totalSectors);
    // FAT offset (uint32 at 80)
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 80), (uint)fatOffsetSectors);
    // FAT length (uint32 at 84)
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 84), (uint)fatLengthSectors);
    // Cluster heap offset (uint32 at 88)
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 88), (uint)clusterHeapOffsetSectors);
    // Cluster count (uint32 at 92)
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 92), clusterCount);
    // Root directory first cluster (uint32 at 96)
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 96), 2);
    // Bytes per sector shift (byte at 108)
    disk[offset + 108] = (byte)bytesPerSectorShift;
    // Sectors per cluster shift (byte at 109)
    disk[offset + 109] = (byte)sectorsPerClusterShift;
    // Number of FATs (byte at 110)
    disk[offset + 110] = (byte)fatCount;
    // Boot signature
    disk[offset + 510] = 0x55;
    disk[offset + 511] = 0xAA;
  }

  private static ushort ComputeNameHash(string name) {
    ushort hash = 0;
    foreach (var ch in name.ToUpperInvariant()) {
      hash = (ushort)(((hash << 15) | (hash >> 1)) + (ch & 0xFF));
      hash = (ushort)(((hash << 15) | (hash >> 1)) + (ch >> 8));
    }
    return hash;
  }
}
