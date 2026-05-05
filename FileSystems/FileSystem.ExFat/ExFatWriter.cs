#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ExFat;

/// <summary>
/// Builds exFAT filesystem images that Windows 10+ actually mounts.
/// <para>
/// Default layout: 8&#160;MB image, 512&#8239;B/sector, 8&#160;sectors/cluster (4&#160;KB clusters).
/// VBR at sector&#160;0, backup VBR at sector&#160;12, FAT at sector&#160;24, cluster heap thereafter;
/// cluster&#160;2 = root, cluster&#160;3 = allocation bitmap, cluster&#160;4 = up-case table.
/// </para>
/// <para>
/// Key real-world fixes over the original implementation: Set-checksum on each File
/// directory entry set (required — Windows silently ignores files whose set-checksum
/// is wrong), up-case table checksum, timestamps on create/modify/access, volume
/// serial number, filesystem revision (1.0), stream-extension GeneralSecondaryFlags
/// advertising FAT-chain allocation. These are the fields fsck/chkdsk and
/// <c>diskutil</c>/<c>fsck_exfat</c> audit before declaring the volume clean.
/// </para>
/// </summary>
public sealed class ExFatWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];
  private const uint EocMarker = 0xFFFFFFFFu;

  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  public byte[] Build(int totalSizeMB = 8) {
    const int bytesPerSector = 512;
    const int sectorsPerClusterShift = 3;
    const int sectorsPerCluster = 1 << sectorsPerClusterShift;
    const int clusterSize = bytesPerSector * sectorsPerCluster;
    const int bytesPerSectorShift = 9;
    const int fatOffsetSectors = 24;
    const int fatCount = 1;

    var totalBytes = totalSizeMB * 1024 * 1024;
    var totalSectors = totalBytes / bytesPerSector;

    // First-pass FAT sizing, then fix-up once we know final cluster count.
    var fatLengthSectors = 1;
    var clusterHeapOffsetSectors = fatOffsetSectors + fatLengthSectors;
    var clusterCount = (totalSectors - clusterHeapOffsetSectors) / sectorsPerCluster;

    var fatBytesNeeded = (clusterCount + 2) * 4;
    fatLengthSectors = ((int)fatBytesNeeded + bytesPerSector - 1) / bytesPerSector;
    clusterHeapOffsetSectors = fatOffsetSectors + fatLengthSectors;
    clusterCount = (totalSectors - clusterHeapOffsetSectors) / sectorsPerCluster;

    var disk = new byte[totalBytes];
    var nowStamp = BuildExFatTimestamp(DateTime.UtcNow);
    var volumeSerial = unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    // Cluster 2 = root dir, 3 = alloc bitmap, 4 = upcase. Single-cluster chains for each.
    var fatOffset = fatOffsetSectors * bytesPerSector;
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset), 0xFFFFFFF8);    // media type
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 4), EocMarker); // reserved
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 2 * 4), EocMarker); // root EOC
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 3 * 4), EocMarker); // bitmap EOC
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 4 * 4), EocMarker); // upcase EOC

    var nextCluster = 5u;
    var clusterHeapOffset = clusterHeapOffsetSectors * bytesPerSector;

    // --- Up-Case Table (cluster 4): minimal ASCII identity with upper-case transform. ---
    const int upcaseEntries = 128;
    const int upcaseBytes = upcaseEntries * 2;
    const uint upcaseCluster = 4u;
    var upcaseOffset = clusterHeapOffset + (int)(upcaseCluster - 2) * clusterSize;
    for (var i = 0; i < upcaseEntries; ++i) {
      var ch = (ushort)(i >= 'a' && i <= 'z' ? i - 32 : i);
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(upcaseOffset + i * 2), ch);
    }
    var upcaseChecksum = TableChecksum(disk.AsSpan(upcaseOffset, upcaseBytes));

    // --- Allocation Bitmap (cluster 3) — filled once all clusters are known. ---
    const uint bitmapCluster = 3u;
    var bitmapSize = ((int)clusterCount + 7) / 8;

    // --- Root Directory (cluster 2) ---
    var rootOffset = clusterHeapOffset + (int)(2 - 2) * clusterSize;
    var dirPos = rootOffset;

    // Volume label entry (0x83) with zero characters; Windows tolerates this.
    disk[dirPos] = 0x83;
    disk[dirPos + 1] = 0;
    dirPos += 32;

    // Allocation Bitmap entry (0x81)
    disk[dirPos] = 0x81;
    disk[dirPos + 1] = 0; // BitmapFlags: bit 0 = 0 → first bitmap (only bitmap)
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 20), bitmapCluster);
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(dirPos + 24), bitmapSize);
    dirPos += 32;

    // Up-Case Table entry (0x82) — with TableChecksum at bytes 4-7.
    disk[dirPos] = 0x82;
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 4), upcaseChecksum);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 20), upcaseCluster);
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(dirPos + 24), upcaseBytes);
    dirPos += 32;

    // --- File entry sets ---
    foreach (var (name, data) in _files) {
      var clustersNeeded = Math.Max(1, (data.Length + clusterSize - 1) / clusterSize);
      var fileFirstCluster = nextCluster;

      var dataOffset = clusterHeapOffset + (int)(fileFirstCluster - 2) * clusterSize;
      if (dataOffset + data.Length <= disk.Length)
        data.CopyTo(disk, dataOffset);

      // Chain file clusters through FAT.
      for (var c = 0; c < clustersNeeded; ++c) {
        var cluster = fileFirstCluster + (uint)c;
        var nextVal = (c + 1 < clustersNeeded) ? cluster + 1 : EocMarker;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + (int)cluster * 4), nextVal);
      }
      nextCluster += (uint)clustersNeeded;

      var nameChars = name.ToCharArray();
      var nameEntries = (nameChars.Length + 14) / 15;
      var secondaryCount = 1 + nameEntries;

      var setStart = dirPos;

      // File entry (0x85) — fill everything except SetChecksum, compute it at the end.
      disk[dirPos] = 0x85;
      disk[dirPos + 1] = (byte)secondaryCount;
      // bytes 2-3 = SetChecksum (write later)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirPos + 4), 0x0020);      // FileAttributes: archive
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 8), nowStamp);    // CreateTimestamp
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 12), nowStamp);   // LastModifiedTimestamp
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 16), nowStamp);   // LastAccessedTimestamp
      dirPos += 32;

      // Stream Extension (0xC0)
      disk[dirPos] = 0xC0;
      disk[dirPos + 1] = 0x01; // AllocationPossible; NoFatChain=0 → cluster chain read from FAT.
      disk[dirPos + 3] = (byte)nameChars.Length;
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirPos + 4), ComputeNameHash(name));
      BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(dirPos + 8), data.Length);      // ValidDataLength
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 20), fileFirstCluster);
      BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(dirPos + 24), data.Length);      // DataLength
      dirPos += 32;

      // File Name entries (0xC1)
      for (var n = 0; n < nameEntries; ++n) {
        disk[dirPos] = 0xC1;
        disk[dirPos + 1] = 0;
        var startChar = n * 15;
        var charsToWrite = Math.Min(15, nameChars.Length - startChar);
        for (var c = 0; c < charsToWrite; ++c)
          BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirPos + 2 + c * 2), nameChars[startChar + c]);
        dirPos += 32;
      }

      // Compute and write SetChecksum now that the whole entry-set is laid out.
      var setBytes = 32 * (1 + secondaryCount);
      var checksum = EntrySetChecksum(disk.AsSpan(setStart, setBytes));
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(setStart + 2), checksum);
    }

    // --- Fill Allocation Bitmap ---
    var bitmapOffset = clusterHeapOffset + (int)(bitmapCluster - 2) * clusterSize;
    for (var c = 2u; c < nextCluster; ++c) {
      var bitIndex = (int)(c - 2);
      disk[bitmapOffset + bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
    }

    // --- VBRs (primary + backup) ---
    var usedClusters = nextCluster - 2;
    var percentInUse = clusterCount == 0 ? (byte)0 : (byte)Math.Min(100, usedClusters * 100 / clusterCount);
    WriteVbr(disk, 0, bytesPerSector, bytesPerSectorShift, sectorsPerClusterShift,
      fatOffsetSectors, fatLengthSectors, clusterHeapOffsetSectors,
      (uint)clusterCount, totalSectors, fatCount, volumeSerial, percentInUse);
    WriteVbr(disk, 12 * bytesPerSector, bytesPerSector, bytesPerSectorShift, sectorsPerClusterShift,
      fatOffsetSectors, fatLengthSectors, clusterHeapOffsetSectors,
      (uint)clusterCount, totalSectors, fatCount, volumeSerial, percentInUse);

    // --- Boot Checksum sector (spec §3.1.3) — required by chkdsk.
    // Rotate-right-one-then-add over the 11 sectors of the VBR region excluding
    // bytes 106/107/112 (VolumeFlags and PercentInUse are volatile). Then replicate
    // the 32-bit checksum for the entire sector. Primary at sector 11, backup at 23.
    WriteBootChecksumSector(disk, 0, bytesPerSector);
    WriteBootChecksumSector(disk, 12 * bytesPerSector, bytesPerSector);

    return disk;
  }

  private static void WriteBootChecksumSector(byte[] disk, int vbrOffset, int bytesPerSector) {
    var checksumSectorOffset = vbrOffset + 11 * bytesPerSector;
    if (checksumSectorOffset + bytesPerSector > disk.Length) return;
    uint checksum = 0;
    var spanLen = 11 * bytesPerSector;
    for (var i = 0; i < spanLen; ++i) {
      // Skip VolumeFlags (106/107) and PercentInUse (112) per spec §3.1.3.
      if (i == 106 || i == 107 || i == 112) continue;
      checksum = ((checksum & 1) != 0 ? 0x80000000u : 0) + (checksum >> 1) + disk[vbrOffset + i];
    }
    for (var i = 0; i < bytesPerSector; i += 4)
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(checksumSectorOffset + i), checksum);
  }

  private static void WriteVbr(byte[] disk, int offset, int bytesPerSector,
    int bytesPerSectorShift, int sectorsPerClusterShift,
    int fatOffsetSectors, int fatLengthSectors, int clusterHeapOffsetSectors,
    uint clusterCount, int totalSectors, int fatCount, uint volumeSerial, byte percentInUse) {
    disk[offset] = 0xEB; disk[offset + 1] = 0x76; disk[offset + 2] = 0x90;
    Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(disk, offset + 3);
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(offset + 64), 0);               // PartitionOffset
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(offset + 72), (ulong)totalSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 80), (uint)fatOffsetSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 84), (uint)fatLengthSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 88), (uint)clusterHeapOffsetSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 92), clusterCount);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 96), 2);                // RootDirCluster
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 100), volumeSerial);    // VolumeSerialNumber
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(offset + 104), 0x0100);          // FileSystemRevision 1.0
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(offset + 106), 0);               // VolumeFlags
    disk[offset + 108] = (byte)bytesPerSectorShift;
    disk[offset + 109] = (byte)sectorsPerClusterShift;
    disk[offset + 110] = (byte)fatCount;
    disk[offset + 112] = percentInUse;
    disk[offset + 510] = 0x55;
    disk[offset + 511] = 0xAA;
  }

  /// <summary>
  /// exFAT set-checksum per spec §7.4.3 — rotate-right-one-bit-then-add over every
  /// byte of the entry set, skipping bytes 2 and 3 of the first (File) entry which
  /// are the checksum field itself.
  /// </summary>
  private static ushort EntrySetChecksum(ReadOnlySpan<byte> set) {
    ushort checksum = 0;
    for (var i = 0; i < set.Length; ++i) {
      if (i == 2 || i == 3) continue;
      checksum = (ushort)((((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[i]) & 0xFFFF);
    }
    return checksum;
  }

  /// <summary>
  /// Up-case table checksum per spec §7.2.2 — same rotate-add, but uint32 and over the
  /// table bytes directly (no skip).
  /// </summary>
  private static uint TableChecksum(ReadOnlySpan<byte> table) {
    uint checksum = 0;
    foreach (var b in table)
      checksum = ((checksum & 1) != 0 ? 0x80000000u : 0) + (checksum >> 1) + b;
    return checksum;
  }

  private static ushort ComputeNameHash(string name) {
    ushort hash = 0;
    foreach (var ch in name.ToUpperInvariant()) {
      hash = (ushort)(((hash << 15) | (hash >> 1)) + (ch & 0xFF));
      hash = (ushort)(((hash << 15) | (hash >> 1)) + (ch >> 8));
    }
    return hash;
  }

  private static uint BuildExFatTimestamp(DateTime dt) {
    // exFAT double-seconds-resolution timestamp (same layout as FAT16 DOS date/time).
    uint year = dt.Year >= 1980 ? (uint)(dt.Year - 1980) : 0u;
    uint time = ((uint)dt.Hour << 11) | ((uint)dt.Minute << 5) | ((uint)(dt.Second / 2));
    uint date = (year << 9) | ((uint)dt.Month << 5) | (uint)dt.Day;
    return (date << 16) | time;
  }
}
