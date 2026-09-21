#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileSystem.Btrfs;

/// <summary>
/// Verifies the checksum field shared by the Btrfs superblock and tree-block
/// headers. The mounted profile currently accepts CRC32C volumes only; other
/// checksum algorithms remain fail-closed until their existing project
/// implementations are wired into the filesystem validator.
/// </summary>
internal static class BtrfsMetadataChecksum {
  internal const ushort Crc32CType = 0;
  internal const int ChecksumFieldSize = 32;
  private const int Crc32CSize = 4;

  internal static uint ComputeCrc32C(ReadOnlySpan<byte> block) {
    if (block.Length < ChecksumFieldSize)
      throw new ArgumentException("Btrfs metadata block is shorter than its checksum field.", nameof(block));
    var crc = new Crc32(Crc32.Castagnoli);
    crc.Update(block[ChecksumFieldSize..]);
    return crc.Value;
  }

  internal static void ValidateCrc32C(ReadOnlySpan<byte> block, string description) {
    if (block.Length < ChecksumFieldSize)
      throw new InvalidDataException($"Btrfs {description} is shorter than its checksum field.");

    var expected = BinaryPrimitives.ReadUInt32LittleEndian(block[..Crc32CSize]);
    var actual = ComputeCrc32C(block);
    if (actual != expected)
      throw new InvalidDataException(
        $"Btrfs {description} CRC32C mismatch: stored 0x{expected:X8}, computed 0x{actual:X8}.");
  }
}
