#pragma warning disable CS1591
namespace FileSystem.Qnx6;

/// <summary>
/// The two things a QNX6 volume's numbers mean: where block zero of the
/// filesystem actually is, and what the superblock's checksum is over.
/// </summary>
/// <remarks>
/// <para>A block pointer in a QNX6 inode is not a device block. The driver adds
/// a fixed offset to every one of them — the boot block area plus the
/// superblock area, twelve blocks at the usual 1024-byte block size — so a
/// pointer of zero means the first block after the superblock, not the start of
/// the device. Writing device blocks into those fields puts every file twelve
/// blocks further out than the volume says.</para>
///
/// <para>The mirror superblock's position follows from the same arithmetic: the
/// driver looks for it at the block count recorded in the superblock, plus that
/// same offset. Putting it at the end of the image only works if the image ends
/// exactly there.</para>
/// </remarks>
internal static class Qnx6Geometry {

  /// <summary>The boot block area at the front of the volume.</summary>
  internal const int BootBlockBytes = 0x2000;

  /// <summary>The area reserved for the superblock, which follows the boot block.</summary>
  internal const int SuperblockAreaBytes = 0x1000;

  /// <summary>How many blocks sit before the filesystem's own block zero.</summary>
  internal static long BlocksBefore(int blockSize)
    => (BootBlockBytes + SuperblockAreaBytes) / blockSize;

  /// <summary>Where a filesystem block actually is in the image.</summary>
  internal static long ByteOffsetOf(long filesystemBlock, int blockSize)
    => (filesystemBlock + BlocksBefore(blockSize)) * blockSize;

  /// <summary>
  /// The superblock's checksum: a big-endian CRC-32 over everything after it,
  /// which is bytes 8 through 511.
  /// </summary>
  /// <remarks>
  /// This is the kernel's <c>crc32_be</c> — the unreflected CRC-32 with
  /// polynomial 0x04C11DB7, seeded with zero and not inverted at the end. It is
  /// not the CRC-32 of ZIP or PNG, which is the reflected one; feeding the same
  /// bytes to that gives a different answer and a volume the driver refuses.
  /// </remarks>
  internal static uint Checksum(ReadOnlySpan<byte> superblock) {
    var crc = 0u;
    for (var i = 8; i < 512 && i < superblock.Length; ++i) {
      crc ^= (uint)superblock[i] << 24;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
    }

    return crc;
  }
}
