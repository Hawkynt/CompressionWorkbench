namespace Compression.Core.DiskImage;

/// <summary>
/// Block-group arithmetic shared by the ext-family writers. ext1 and ext2/3/4
/// lay their groups out identically; only the superblock revision and magic differ.
/// </summary>
public static class ExtBlockGroupGeometry {

  /// <summary>Block-group geometry derived from the block size and the volume size.</summary>
  public readonly record struct ExtGeometry(
    int TotalBlocks, int FirstDataBlock, int BlocksPerGroup, int GroupCount,
    int GdtBlocks, int InodesPerGroup, int InodeTableBlocks, int PerGroupMetaBlocks);
  
  /// <summary>
  /// Works out how many block groups an ext-family volume needs and how the per-group
  /// metadata is sized. A group holds 8 * blockSize blocks because its block
  /// bitmap is a single block; anything larger takes more groups. Inodes are
  /// shared evenly across them, so a one-group volume ends up with exactly the
  /// geometry this writer produced before groups existed.
  /// </summary>
  public static ExtGeometry Compute(int blockSize, int totalBlocks, int inodeSize, int neededInodes) {
    var firstDataBlock = blockSize == 1024 ? 1 : 0;
    var blocksPerGroup = 8 * blockSize;
    var inodesPerBlock = blockSize / inodeSize;
    totalBlocks = Math.Max(totalBlocks, firstDataBlock + 64);
  
    int groupCount = 1, inodesPerGroup = 0, inodeTableBlocks = 0, gdtBlocks = 1, perGroupMeta = 0;
    for (var pass = 0; pass < 8; ++pass) {
      groupCount = Math.Max(1, (int)(((long)totalBlocks - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup));
      inodesPerGroup = ChooseInodeCount((neededInodes + groupCount - 1) / groupCount);
      inodesPerGroup = Math.Min(8 * blockSize, (inodesPerGroup + inodesPerBlock - 1) / inodesPerBlock * inodesPerBlock);
      inodeTableBlocks = inodesPerGroup * inodeSize / blockSize;
      gdtBlocks = (groupCount * 32 + blockSize - 1) / blockSize;
      // superblock + descriptor table + block bitmap + inode bitmap + inode table
      perGroupMeta = 1 + gdtBlocks + 2 + inodeTableBlocks;
  
      // A trailing group with no room for its own metadata is not worth keeping;
      // dropping it costs a sliver of capacity and leaves every group well-formed.
      var lastGroupBlocks = (long)totalBlocks - (firstDataBlock + (long)(groupCount - 1) * blocksPerGroup);
      if (groupCount == 1 || lastGroupBlocks > perGroupMeta) break;
      totalBlocks = firstDataBlock + (groupCount - 1) * blocksPerGroup;
    }
  
    totalBlocks = Math.Max(totalBlocks, firstDataBlock + perGroupMeta + 1);
    return new ExtGeometry(totalBlocks, firstDataBlock, blocksPerGroup, groupCount,
                           gdtBlocks, inodesPerGroup, inodeTableBlocks, perGroupMeta);
  }

  /// <summary>
  /// Rounds the required inode count up to a sensible group size: every
  /// reserved/dir/file inode with headroom, never below the classic 128, and a
  /// multiple of 8 so the inode bitmap's byte boundaries stay tidy.
  /// </summary>
  public static int ChooseInodeCount(int needed) {
    var withHeadroom = Math.Max(128, needed + needed / 10 + 16);
    return withHeadroom + 7 & ~7;
  }
}
