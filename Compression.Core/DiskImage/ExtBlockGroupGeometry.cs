namespace Compression.Core.DiskImage;

/// <summary>
/// Block-group arithmetic shared by the ext-family writers. ext1 and ext2/3/4
/// lay their groups out identically; only the superblock revision and magic differ.
/// </summary>
public static class ExtBlockGroupGeometry {

  /// <summary>
  /// How wide one group descriptor is on the volume whose superblock this is.
  /// </summary>
  /// <remarks>
  /// The classic thirty-two bytes, unless the volume declares 64BIT, in which case
  /// it says so itself. Stepping through the table at the wrong stride lands in
  /// the middle of the next descriptor and reads nonsense out of it.
  /// </remarks>
  public static int DescriptorSize(ReadOnlySpan<byte> superblock) {
    const uint feature64Bit = 0x0080;
    if (superblock.Length < 256) return 32;

    var incompat = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(superblock[96..]);
    if ((incompat & feature64Bit) == 0) return 32;

    var declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(superblock[254..]);
    return declared < 32 ? 32 : declared;
  }

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
  /// <param name="descriptorSize">
  /// Bytes per group descriptor: the classic 32, or the 64 a 64BIT volume uses.
  /// </param>
  public static ExtGeometry Compute(int blockSize, int totalBlocks, int inodeSize, int neededInodes,
                                    int descriptorSize = 32) {
    var firstDataBlock = blockSize == 1024 ? 1 : 0;
    var blocksPerGroup = 8 * blockSize;
    var inodesPerBlock = blockSize / inodeSize;
    totalBlocks = Math.Max(totalBlocks, firstDataBlock + 64);
  
    int groupCount = 1, inodesPerGroup = 0, inodeTableBlocks = 0, gdtBlocks = 1, perGroupMeta = 0;
    for (var pass = 0; pass < 8; ++pass) {
      groupCount = Math.Max(1, (int)(((long)totalBlocks - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup));
      // How many inodes the volume gets is a matter of policy, not need: mke2fs
      // hands out one per so many bytes of capacity and takes the count from
      // there. Sizing the table to the files actually being written leaves a
      // volume with a hundred-odd inodes where every other tool would put
      // thousands, which is as plain a tell as any label.
      var volumeBytes = (long)totalBlocks * blockSize;
      var byRatio = volumeBytes / BytesPerInode(volumeBytes);
      var wanted = Math.Max(ChooseInodeCount(neededInodes), (int)Math.Min(int.MaxValue, byRatio));
      inodesPerGroup = (wanted + groupCount - 1) / groupCount + 7 & ~7;
      inodesPerGroup = Math.Min(8 * blockSize, (inodesPerGroup + inodesPerBlock - 1) / inodesPerBlock * inodesPerBlock);
      inodeTableBlocks = inodesPerGroup * inodeSize / blockSize;
      gdtBlocks = (groupCount * descriptorSize + blockSize - 1) / blockSize;
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
  /// <summary>
  /// Bytes of capacity mke2fs allows per inode, by how large the volume is.
  /// </summary>
  /// <remarks>
  /// These are the ratios in mke2fs.conf's size types — floppy, small, default,
  /// big, huge — which is where a volume's inode count actually comes from.
  /// </remarks>
  public static int BytesPerInode(long volumeBytes) => volumeBytes switch {
    < 3L * 1024 * 1024 => 8192,
    < 512L * 1024 * 1024 => 4096,
    < 4L * 1024 * 1024 * 1024 * 1024 => 16384,
    < 16L * 1024 * 1024 * 1024 * 1024 => 32768,
    _ => 65536,
  };

  public static int ChooseInodeCount(int needed) {
    var withHeadroom = Math.Max(128, needed + needed / 10 + 16);
    return withHeadroom + 7 & ~7;
  }
}
