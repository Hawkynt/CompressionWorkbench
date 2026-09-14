#pragma warning disable CS1591
namespace FileSystem.Nwfs;

/// <summary>
/// Constants and geometry helpers for the Traditional NetWare file system.
/// NetWare's virtual-partition layer uses fixed 4 KiB physical blocks and
/// allocation clusters made from 1, 2, 4, 8 or 16 of those blocks.
/// </summary>
internal static class NwfsLayout {
  internal const int SectorSize = 512;
  internal const int IoBlockSize = 4096;
  internal const int SectorsPerIoBlock = IoBlockSize / SectorSize;

  internal const int HotfixBlocks = 64;
  internal const int HotfixSectors = HotfixBlocks * SectorsPerIoBlock;
  internal const int VolumeSegmentStartBlock = 20;
  internal const uint VolumeRootSector = VolumeSegmentStartBlock * SectorsPerIoBlock;

  internal static ReadOnlySpan<int> MasterCopySectors => [0x20, 0x40, 0x60, 0x80];
  internal static ReadOnlySpan<int> VolumeTableLogicalBlocks => [4, 8, 12, 16];

  internal const int VolumeTableBytes = 512;
  internal const int VolumeEntryBytes = 60;
  internal const int MaxVolumeEntries = 8;
  internal const int DirectoryEntryBytes = 128;
  internal const int FatEntryBytes = 8;
  internal const int FatEntriesPerIoBlock = IoBlockSize / FatEntryBytes;
  internal const int FatMirrorPhysicalBlockGap = 64;

  internal const uint EndOfChain = 0xFFFFFFFF;
  internal const uint FreeFatIndex = 0;
  internal const uint FreeFatCluster = 0;

  internal const uint FreeNode = 0xFFFFFFFF;
  internal const uint TrusteeNode = 0xFFFFFFFE;
  internal const uint RootNode = 0xFFFFFFFD;
  internal const uint RestrictionNode = 0xFFFFFFFC;
  internal const uint RootDirectoryRecord = 0;

  internal const byte DosNameSpace = 0;
  internal const byte FlagDeleted3 = 0x01;
  internal const byte FlagSubdirectory = 0x04;
  internal const byte FlagPrimaryNamespace = 0x10;
  internal const byte FlagDeleted4 = 0x20;

  internal const uint AttributeDirectory = 0x10;
  internal const uint AttributeArchive = 0x20;
  internal const uint SupervisorObjectId = 0x01000000;

  internal const int MaxNameLength = 12;
  internal const int MaxVolumeNameLength = 15;

  internal const uint HotfixFlags = 0x00010000;
  internal const uint FormatStamp = 0x0703F808;
  internal const uint MirrorInSyncFlags = 0x01860000;
  internal const uint MirrorBaseStatus = 0x00010000;

  internal static int BlocksPerCluster(int clusterSize) => clusterSize / IoBlockSize;

  internal static int ClusterCode(int clusterSize) {
    if (!IsValidBlockSize(clusterSize))
      throw new ArgumentOutOfRangeException(nameof(clusterSize));
    return 3 + System.Numerics.BitOperations.Log2((uint)BlocksPerCluster(clusterSize));
  }

  internal static int ClusterSizeFromCode(int code)
    => code is >= 3 and <= 7 ? IoBlockSize << (code - 3) : 0;

  internal static int FatGapClusters(int clusterSize)
    => FatMirrorPhysicalBlockGap / BlocksPerCluster(clusterSize);

  internal static bool IsValidBlockSize(int clusterSize)
    => clusterSize is >= IoBlockSize and <= 64 * 1024
       && (clusterSize & (clusterSize - 1)) == 0;

  internal static int FatPhysicalBlockCount(uint clusterCount, int clusterSize) {
    var blocksPerCluster = BlocksPerCluster(clusterSize);
    var blocks = Math.Max(1L, DivideRoundUp(clusterCount, FatEntriesPerIoBlock));
    var aligned = AlignUp(blocks, blocksPerCluster);
    if (aligned > int.MaxValue)
      throw new InvalidDataException("NWFS FAT is too large for the managed implementation.");
    return (int)aligned;
  }

  internal static int FatPrimaryPhysicalBlock(int streamBlockIndex)
    => checked(streamBlockIndex + streamBlockIndex / FatMirrorPhysicalBlockGap * FatMirrorPhysicalBlockGap);

  internal static int FatMirrorPhysicalBlock(int streamBlockIndex)
    => checked(FatPrimaryPhysicalBlock(streamBlockIndex) + FatMirrorPhysicalBlockGap);

  internal static long PartitionOffset(uint partitionStartSector)
    => checked((long)partitionStartSector * SectorSize);

  internal static long LogicalPartitionOffset(uint partitionStartSector)
    => checked(PartitionOffset(partitionStartSector) + (long)HotfixBlocks * IoBlockSize);

  internal static long VolumeOffset(uint partitionStartSector)
    => checked(LogicalPartitionOffset(partitionStartSector) + (long)VolumeSegmentStartBlock * IoBlockSize);

  internal static long TightImageLength(uint partitionStartSector, int clusterSize, uint clusterCount)
    => checked(VolumeOffset(partitionStartSector) + (long)clusterSize * clusterCount);

  internal static VolumePlan Plan(
      int clusterSize,
      int directoryClusters,
      int fileClusters,
      uint minimumClusters = 0) {
    if (!IsValidBlockSize(clusterSize))
      throw new ArgumentOutOfRangeException(nameof(clusterSize));
    if (directoryClusters < 1)
      throw new ArgumentOutOfRangeException(nameof(directoryClusters));
    if (fileClusters < 0)
      throw new ArgumentOutOfRangeException(nameof(fileClusters));

    var gap = FatGapClusters(clusterSize);
    var candidate = Math.Max((long)minimumClusters, gap + 3L + directoryClusters * 2L + fileClusters);

    while (true) {
      if (candidate > uint.MaxValue)
        throw new InvalidDataException("NWFS volume has too many allocation clusters.");

      var clusterCount = (uint)candidate;
      var fatBlocks = FatPhysicalBlockCount(clusterCount, clusterSize);
      var blocksPerCluster = BlocksPerCluster(clusterSize);
      var fatClustersPerCopy = fatBlocks / blocksPerCluster;
      var fat1 = new uint[fatClustersPerCopy];
      var fat2 = new uint[fatClustersPerCopy];
      var used = new HashSet<uint>();

      for (var i = 0; i < fatClustersPerCopy; ++i) {
        var streamBlock = checked(i * blocksPerCluster);
        fat1[i] = checked((uint)(FatPrimaryPhysicalBlock(streamBlock) / blocksPerCluster));
        fat2[i] = checked((uint)(FatMirrorPhysicalBlock(streamBlock) / blocksPerCluster));
        used.Add(fat1[i]);
        used.Add(fat2[i]);
      }

      var dir1 = new uint[directoryClusters];
      var dir2 = new uint[directoryClusters];
      uint search = 1;
      for (var i = 0; i < directoryClusters; ++i) {
        while (true) {
          var mirror = checked(search + (uint)gap + 1);
          if (!used.Contains(search) && !used.Contains(mirror)) {
            dir1[i] = search;
            dir2[i] = mirror;
            used.Add(search);
            used.Add(mirror);
            ++search;
            break;
          }
          ++search;
        }
      }

      var data = new uint[fileClusters];
      search = 1;
      for (var i = 0; i < data.Length; ++i) {
        while (used.Contains(search))
          ++search;
        data[i] = search;
        used.Add(search);
        ++search;
      }

      var highest = used.Count == 0 ? 0u : used.Max();
      var required = Math.Max((long)minimumClusters, (long)highest + 1);
      var requiredFatBlocks = FatPhysicalBlockCount((uint)required, clusterSize);
      if (required == candidate && requiredFatBlocks == fatBlocks)
        return new VolumePlan(
          clusterSize,
          (uint)candidate,
          fatBlocks,
          fat1,
          fat2,
          dir1,
          dir2,
          data);

      candidate = required;
    }
  }

  internal static long AlignUp(long value, int alignment)
    => checked(DivideRoundUp(value, alignment) * alignment);

  internal static long DivideRoundUp(long value, long divisor)
    => checked((value + divisor - 1) / divisor);

  internal sealed record VolumePlan(
    int ClusterSize,
    uint ClusterCount,
    int FatPhysicalBlocks,
    uint[] Fat1Clusters,
    uint[] Fat2Clusters,
    uint[] Directory1Clusters,
    uint[] Directory2Clusters,
    uint[] DataClusters) {

    internal int BlocksPerCluster => NwfsLayout.BlocksPerCluster(this.ClusterSize);
    internal uint Fat1 => this.Fat1Clusters[0];
    internal uint Fat2 => this.Fat2Clusters[0];
    internal uint Directory1 => this.Directory1Clusters[0];
    internal uint Directory2 => this.Directory2Clusters[0];
  }
}
