#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Ext;

/// <summary>
/// The crc32c sums an ext4 volume with <c>metadata_csum</c> carries, and where in
/// each structure they sit.
/// </summary>
/// <remarks>
/// <para>Every one of them is salted with the volume's own seed, so a structure
/// lifted from one volume into another fails its sum rather than being read as
/// belonging there. The seed is derived from the volume's identity, or stored
/// outright when the volume declares <c>metadata_csum_seed</c> — which is what
/// lets the identity be changed later without rewriting every sum on the disk.</para>
///
/// <para>Two of them are salted twice over: an inode's and a directory block's are
/// seeded with the inode's number and generation as well, so neither can be moved
/// to another inode either.</para>
/// </remarks>
internal static class ExtChecksums {

  /// <summary>Byte in a directory tail's file-type slot that marks it as a sum, not an entry.</summary>
  internal const byte DirectoryTailMarker = 0xDE;

  /// <summary>Bytes a directory block gives up at its end to carry a sum.</summary>
  internal const int DirectoryTailBytes = 12;

  /// <summary>
  /// crc32c of <paramref name="data" />, continuing from <paramref name="seed" /> and
  /// left uninverted — which is the convention ext4 sums under, so that one sum can
  /// be fed straight into the next.
  /// </summary>
  internal static uint Crc32c(uint seed, ReadOnlySpan<byte> data) {
    const uint castagnoli = 0x82F63B78u;
    var crc = seed;
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; ++i)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ castagnoli : crc >> 1;
    }

    return crc;
  }

  private static uint Crc32c(uint seed, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    return Crc32c(seed, bytes);
  }

  /// <summary>The volume's seed, as derived from its identity when it is not stored.</summary>
  internal static uint SeedFromUuid(ReadOnlySpan<byte> uuid) => Crc32c(0xFFFFFFFFu, uuid);

  /// <summary>The seed an inode's own structures are salted with.</summary>
  internal static uint InodeSeed(uint volumeSeed, uint inode, uint generation)
    => Crc32c(Crc32c(volumeSeed, inode), generation);

  /// <summary>
  /// Stamps an inode with its own sum, over itself with the two halves of that sum
  /// blanked.
  /// </summary>
  internal static void StampInode(Span<byte> inode, int inodeSize, uint seed) {
    const int checksumLow = 0x7C;
    const int checksumHigh = 0x82;
    const int classicInodeSize = 128;

    BinaryPrimitives.WriteUInt16LittleEndian(inode[checksumLow..], 0);

    // The high half lives past the classic inode, and only counts when the inode
    // says it uses enough of the space there to reach it.
    var hasHigh = inodeSize > classicInodeSize
      && BinaryPrimitives.ReadUInt16LittleEndian(inode[classicInodeSize..]) >= 4;
    if (hasHigh) BinaryPrimitives.WriteUInt16LittleEndian(inode[checksumHigh..], 0);

    var crc = Crc32c(seed, inode[..inodeSize]);

    BinaryPrimitives.WriteUInt16LittleEndian(inode[checksumLow..], (ushort)(crc & 0xFFFF));
    if (hasHigh) BinaryPrimitives.WriteUInt16LittleEndian(inode[checksumHigh..], (ushort)(crc >> 16));
  }

  /// <summary>
  /// Writes the twelve-byte tail a directory block ends with: an entry shaped so no
  /// reader mistakes it for one, holding the sum of everything before it.
  /// </summary>
  internal static void StampDirectoryTail(Span<byte> block, int blockSize, uint inodeSeed) {
    var tail = blockSize - DirectoryTailBytes;
    BinaryPrimitives.WriteUInt32LittleEndian(block[tail..], 0);              // inode 0: not an entry
    BinaryPrimitives.WriteUInt16LittleEndian(block[(tail + 4)..], DirectoryTailBytes);
    block[tail + 6] = 0;                                                    // no name
    block[tail + 7] = DirectoryTailMarker;

    var crc = Crc32c(inodeSeed, block[..tail]);
    BinaryPrimitives.WriteUInt32LittleEndian(block[(blockSize - 4)..], crc);
  }

  /// <summary>The sum of one of a group's two bitmaps, split across the descriptor.</summary>
  internal static void StampBitmap(Span<byte> descriptor, int descriptorSize,
                                   ReadOnlySpan<byte> bitmap, int meaningfulBytes, uint volumeSeed,
                                   bool isBlockBitmap) {
    var crc = Crc32c(volumeSeed, bitmap[..meaningfulBytes]);
    var low = isBlockBitmap ? 0x18 : 0x1A;
    var high = isBlockBitmap ? 0x38 : 0x3A;
    BinaryPrimitives.WriteUInt16LittleEndian(descriptor[low..], (ushort)(crc & 0xFFFF));
    if (descriptorSize >= 64)
      BinaryPrimitives.WriteUInt16LittleEndian(descriptor[high..], (ushort)(crc >> 16));
  }

  /// <summary>
  /// Stamps a group descriptor, over the group's number and then the descriptor
  /// itself with its own sum blanked.
  /// </summary>
  internal static void StampGroupDescriptor(Span<byte> descriptor, int descriptorSize,
                                            uint group, uint volumeSeed) {
    const int checksumOffset = 0x1E;
    BinaryPrimitives.WriteUInt16LittleEndian(descriptor[checksumOffset..], 0);
    var crc = Crc32c(Crc32c(volumeSeed, group), descriptor[..descriptorSize]);
    BinaryPrimitives.WriteUInt16LittleEndian(descriptor[checksumOffset..], (ushort)(crc & 0xFFFF));
  }

  /// <summary>What every block of the orphan file opens its tail with.</summary>
  internal const uint OrphanBlockMagic = 0x0B10CA04;

  /// <summary>
  /// Writes the tail of one orphan-file block: the magic that marks it as one, and
  /// a sum salted with the file's inode and the block's own place on the disk, so
  /// the block cannot be read as belonging anywhere else.
  /// </summary>
  internal static void StampOrphanBlock(Span<byte> block, int blockSize, uint volumeSeed,
                                        uint inode, long physicalBlock) {
    const int tailBytes = 8;
    var summed = (blockSize - tailBytes) / 4 * 4;

    BinaryPrimitives.WriteUInt32LittleEndian(block[(blockSize - tailBytes)..], OrphanBlockMagic);

    Span<byte> salt = stackalloc byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(salt, inode);
    BinaryPrimitives.WriteUInt64LittleEndian(salt[8..], (ulong)physicalBlock);
    var crc = Crc32c(Crc32c(volumeSeed, salt), block[..summed]);
    BinaryPrimitives.WriteUInt32LittleEndian(block[(blockSize - 4)..], crc);
  }

  /// <summary>Stamps a superblock, over everything ahead of the sum's own place.</summary>
  internal static void StampSuperblock(Span<byte> superblock) {
    const int checksumOffset = 0x3FC;
    var crc = Crc32c(0xFFFFFFFFu, superblock[..checksumOffset]);
    BinaryPrimitives.WriteUInt32LittleEndian(superblock[checksumOffset..], crc);
  }
}
