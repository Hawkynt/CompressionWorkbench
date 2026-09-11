#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Ext;

namespace FileSystem.Lustre;

/// <summary>
/// Conservative maintenance primitives for an offline Lustre ldiskfs MDT/OST backing image.
/// They deliberately operate below the Lustre namespace layer: no inode, directory or Lustre
/// xattr is reconstructed. Only allocation-bitmap-proven free blocks are wiped, and shrinking
/// uses the ext in-place geometry editor so every surviving allocated block remains intact.
/// </summary>
/// <remarks>
/// The layout facts used here come from the Linux ext4 on-disk documentation. In particular,
/// block-group descriptors locate the block bitmaps, one clear bitmap bit means one free block,
/// and a BLOCK_UNINIT group does not carry a trustworthy on-disk bitmap. Destructive maintenance
/// additionally requires metadata_csum and verifies the superblock, group descriptor and block
/// bitmap checksums before trusting allocation state. Lustre documents ldiskfs as its ext4-derived
/// backing filesystem. No external implementation code is copied.
/// </remarks>
internal static class LustreLdiskfsMaintenance {
  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;

  private const uint CompatSparseSuper2 = 0x0200;

  private const uint IncompatRecover = 0x0004;
  private const uint IncompatJournalDev = 0x0008;
  private const uint IncompatMetaBg = 0x0010;
  private const uint Incompat64Bit = 0x0080;
  private const uint IncompatCsumSeed = 0x2000;

  private const uint RoCompatBigalloc = 0x0200;
  private const uint RoCompatMetadataCsum = 0x0400;
  private const uint RoCompatReadOnly = 0x1000;

  private const ushort BgBlockUninit = 0x0002;

  private readonly record struct Geometry(
    int BlockSize,
    ulong TotalBlocks,
    uint FirstDataBlock,
    uint BlocksPerGroup,
    uint GroupCount,
    int DescriptorSize,
    long DescriptorTableOffset,
    uint FeatureCompat,
    uint FeatureIncompat,
    uint FeatureRoCompat,
    uint ChecksumSeed);

  /// <summary>
  /// Wipes only complete blocks that the initialized ldiskfs allocation bitmap proves free.
  /// BLOCK_UNINIT groups are skipped rather than inferred, because ext4 explicitly permits
  /// their on-disk bitmaps to contain meaningless data while fixed metadata is still present.
  /// </summary>
  public static long WipeFreeBlocks(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Lustre wipe requires a readable, writable, seekable stream.", nameof(image));

    var geometry = ReadGeometry(image);
    ValidateWritableProfile(geometry);

    var descriptor = new byte[geometry.DescriptorSize];
    var bitmap = new byte[geometry.BlockSize];
    var zeros = new byte[Math.Min(64 * 1024, Math.Max(geometry.BlockSize, 4096))];
    long wiped = 0;

    for (uint group = 0; group < geometry.GroupCount; ++group) {
      ReadDescriptor(image, geometry, group, descriptor);

      // BLOCK_UNINIT is itself a safe stop condition: if corruption merely sets this
      // bit we skip a writable group rather than touching it. If corruption clears the
      // bit on a genuinely uninitialised group, the descriptor checksum below catches it.
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(0x12, 2));
      if ((flags & BgBlockUninit) != 0)
        continue;

      ValidateGroupDescriptorChecksum(geometry, group, descriptor);
      ReadAndValidateBlockBitmap(image, geometry, group, descriptor, bitmap);

      var groupFirst = (ulong)geometry.FirstDataBlock + (ulong)group * geometry.BlocksPerGroup;
      if (groupFirst >= geometry.TotalBlocks)
        break;
      var blocksInGroup = (int)Math.Min((ulong)geometry.BlocksPerGroup, geometry.TotalBlocks - groupFirst);

      var runStart = -1;
      for (var bit = 0; bit <= blocksInGroup; ++bit) {
        var isFree = bit < blocksInGroup && (bitmap[bit >> 3] & (1 << (bit & 7))) == 0;
        if (isFree) {
          if (runStart < 0) runStart = bit;
          continue;
        }

        if (runStart < 0)
          continue;

        var runBlocks = bit - runStart;
        var firstBlock = groupFirst + (uint)runStart;
        var offset = CheckedByteOffset(firstBlock, geometry.BlockSize);
        var length = checked((long)runBlocks * geometry.BlockSize);
        if (offset + length > image.Length)
          throw new InvalidDataException($"Lustre/ldiskfs: free run in group {group} extends beyond the image.");

        ZeroRange(image, offset, length, zeros);
        wiped = checked(wiped + length);
        runStart = -1;
      }
    }

    image.Flush();
    return wiped;
  }

  /// <summary>
  /// Copies <paramref name="input"/> to <paramref name="output"/> and trims trailing
  /// bitmap-free ldiskfs blocks using the existing ext in-place shrinker. There is no
  /// extract/re-create fallback: such a fallback would lose Lustre-specific xattrs and
  /// target metadata even when ordinary file contents happened to round-trip.
  /// </summary>
  public static void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("Lustre shrink requires a readable, seekable input stream.", nameof(input));
    if (!output.CanRead || !output.CanWrite || !output.CanSeek)
      throw new ArgumentException("Lustre shrink requires a readable, writable, seekable output stream.", nameof(output));
    if (ReferenceEquals(input, output))
      throw new ArgumentException("Lustre shrink requires a distinct output stream so failure cannot partially rewrite the source.", nameof(output));

    var geometry = ReadGeometry(input);
    ValidateWritableProfile(geometry);
    ValidateShrinkProfile(input, geometry);

    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    input.CopyTo(output);
    output.Position = 0;

    // This path updates the ext block bitmap, group descriptors, primary + backup
    // superblocks and supported checksums in place. It never re-authors the inode tree.
    _ = ExtInPlaceShrinker.ShrinkToFit(output);
    output.Position = 0;
  }

  /// <summary>
  /// Reports the allocation unit that is safe to observe without pretending that a
  /// block-size conversion is currently available for Lustre target metadata.
  /// </summary>
  public static LayoutAnalysis AnalyzeLayout(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var geometry = ReadGeometry(image);
    return new LayoutAnalysis {
      ImageSize = image.Length,
      CurrentUnitSize = geometry.BlockSize,
      CurrentSlackBytes = 0,
      OptimalUnitSize = geometry.BlockSize,
      OptimalSlackBytes = 0,
      RequiresRebuild = [],
      Notes = [
        "Lustre ldiskfs block geometry can be inspected safely in place.",
        "Block-size relayout is deliberately not offered: rebuilding as generic ext would discard Lustre LMA/LOV/FID xattrs and target metadata.",
      ],
    };
  }

  private static Geometry ReadGeometry(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("Lustre ldiskfs maintenance requires a readable, seekable stream.", nameof(image));
    if (image.Length < SuperblockOffset + 1024)
      throw new InvalidDataException("Lustre/ldiskfs: image is too small for an ext superblock.");

    var superblock = new byte[1024];
    image.Position = SuperblockOffset;
    image.ReadExactly(superblock);

    var magic = BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(56, 2));
    if (magic != ExtMagic)
      throw new InvalidDataException($"Lustre maintenance requires an ldiskfs backing image (ext magic 0xEF53, found 0x{magic:X4}).");

    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(24, 4));
    if (logBlockSize > 6)
      throw new InvalidDataException($"Lustre/ldiskfs: unsupported block-size exponent {logBlockSize}.");
    var blockSize = checked(1024 << (int)logBlockSize);

    var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(20, 4));
    var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(32, 4));
    if (blocksPerGroup == 0 || (ulong)blocksPerGroup > (ulong)blockSize * 8)
      throw new InvalidDataException($"Lustre/ldiskfs: invalid s_blocks_per_group value {blocksPerGroup} for {blockSize}-byte blocks.");

    var featureCompat = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(92, 4));
    var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(96, 4));
    var featureRoCompat = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(100, 4));

    uint checksumSeed = 0;
    if ((featureRoCompat & RoCompatMetadataCsum) != 0) {
      ValidateSuperblockChecksum(superblock);
      checksumSeed = (featureIncompat & IncompatCsumSeed) != 0
        ? BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(0x270, 4))
        : Crc32c(0xFFFFFFFFu, superblock.AsSpan(104, 16));
    }

    ulong totalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(4, 4));
    if ((featureIncompat & Incompat64Bit) != 0)
      totalBlocks |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(0x150, 4)) << 32;
    if (totalBlocks <= firstDataBlock)
      throw new InvalidDataException("Lustre/ldiskfs: invalid block count.");

    var descriptorSize = 32;
    if ((featureIncompat & Incompat64Bit) != 0) {
      descriptorSize = BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(0xFE, 2));
      if (descriptorSize < 64 || descriptorSize > blockSize || (descriptorSize & 7) != 0)
        throw new InvalidDataException($"Lustre/ldiskfs: invalid 64-bit group descriptor size {descriptorSize}.");
    }

    var groupCount64 = (totalBlocks - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup;
    if (groupCount64 is 0 or > uint.MaxValue)
      throw new InvalidDataException($"Lustre/ldiskfs: unsupported block-group count {groupCount64}.");
    var groupCount = (uint)groupCount64;

    var descriptorTableOffset = checked((long)(firstDataBlock + 1UL) * blockSize);
    var descriptorBytes = checked((long)groupCount * descriptorSize);
    if (descriptorTableOffset < 0 || descriptorTableOffset + descriptorBytes > image.Length)
      throw new InvalidDataException("Lustre/ldiskfs: primary group descriptor table is truncated.");

    if (totalBlocks > (ulong)(long.MaxValue / blockSize))
      throw new InvalidDataException("Lustre/ldiskfs: volume is too large for stream byte addressing.");
    var declaredBytes = checked((long)totalBlocks * blockSize);
    if (declaredBytes > image.Length)
      throw new InvalidDataException($"Lustre/ldiskfs: superblock declares {declaredBytes} bytes but the image contains only {image.Length}.");

    return new Geometry(
      blockSize, totalBlocks, firstDataBlock, blocksPerGroup, groupCount,
      descriptorSize, descriptorTableOffset, featureCompat, featureIncompat, featureRoCompat,
      checksumSeed);
  }

  private static void ValidateWritableProfile(Geometry geometry) {
    if ((geometry.FeatureRoCompat & RoCompatMetadataCsum) == 0)
      throw new NotSupportedException(
        "Lustre destructive maintenance requires ldiskfs metadata_csum so corrupted allocation bitmaps can be detected before any block is overwritten.");
    if ((geometry.FeatureIncompat & IncompatRecover) != 0)
      throw new NotSupportedException("Lustre maintenance refuses an ldiskfs image that still needs journal recovery; replay it first.");
    if ((geometry.FeatureIncompat & IncompatJournalDev) != 0)
      throw new NotSupportedException("Lustre maintenance does not support ldiskfs images with an external journal device.");
    if ((geometry.FeatureIncompat & IncompatMetaBg) != 0)
      throw new NotSupportedException("Lustre maintenance does not yet support ext META_BG descriptor placement.");
    if ((geometry.FeatureRoCompat & RoCompatBigalloc) != 0)
      throw new NotSupportedException("Lustre maintenance does not yet support ext BIGALLOC cluster bitmaps.");
    if ((geometry.FeatureRoCompat & RoCompatReadOnly) != 0)
      throw new NotSupportedException("Lustre maintenance refuses an ext image explicitly marked read-only.");
  }

  private static void ValidateShrinkProfile(Stream image, Geometry geometry) {
    if (geometry.TotalBlocks > uint.MaxValue)
      throw new NotSupportedException("Lustre shrink currently supports ldiskfs volumes up to UInt32 block counts.");
    if ((geometry.FeatureCompat & CompatSparseSuper2) != 0)
      throw new NotSupportedException("Lustre shrink does not yet rewrite SPARSE_SUPER2 backup locations.");

    var descriptor = new byte[geometry.DescriptorSize];
    var bitmap = new byte[geometry.BlockSize];
    for (uint group = 0; group < geometry.GroupCount; ++group) {
      ReadDescriptor(image, geometry, group, descriptor);
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(0x12, 2));
      if ((flags & BgBlockUninit) != 0)
        throw new NotSupportedException(
          $"Lustre shrink refuses lazy BLOCK_UNINIT group {group}; its on-disk allocation bitmap is not authoritative yet.");

      ValidateGroupDescriptorChecksum(geometry, group, descriptor);
      ReadAndValidateBlockBitmap(image, geometry, group, descriptor, bitmap);
    }
  }

  private static void ReadDescriptor(Stream image, Geometry geometry, uint group, byte[] destination) {
    var offset = checked(geometry.DescriptorTableOffset + (long)group * geometry.DescriptorSize);
    if (offset + geometry.DescriptorSize > image.Length)
      throw new InvalidDataException($"Lustre/ldiskfs: group descriptor {group} is truncated.");
    image.Position = offset;
    image.ReadExactly(destination);
  }

  private static void ReadAndValidateBlockBitmap(
      Stream image,
      Geometry geometry,
      uint group,
      ReadOnlySpan<byte> descriptor,
      byte[] bitmap) {
    var bitmapBlock = ReadBlockBitmapBlock(descriptor, geometry.DescriptorSize);
    if (bitmapBlock >= geometry.TotalBlocks)
      throw new InvalidDataException($"Lustre/ldiskfs: group {group} block bitmap points outside the volume ({bitmapBlock}).");

    var bitmapOffset = CheckedByteOffset(bitmapBlock, geometry.BlockSize);
    if (bitmapOffset + geometry.BlockSize > image.Length)
      throw new InvalidDataException($"Lustre/ldiskfs: group {group} block bitmap is truncated.");

    image.Position = bitmapOffset;
    image.ReadExactly(bitmap);
    ValidateBlockBitmapChecksum(geometry, group, descriptor, bitmap);
  }

  private static ulong ReadBlockBitmapBlock(ReadOnlySpan<byte> descriptor, int descriptorSize) {
    ulong block = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[..4]);
    if (descriptorSize >= 64)
      block |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(0x20, 4)) << 32;
    return block;
  }

  private static void ValidateSuperblockChecksum(ReadOnlySpan<byte> superblock) {
    var expected = BinaryPrimitives.ReadUInt32LittleEndian(superblock.Slice(0x3FC, 4));
    var actual = Crc32c(0xFFFFFFFFu, superblock[..0x3FC]);
    if (actual != expected)
      throw new InvalidDataException(
        $"Lustre/ldiskfs: superblock metadata checksum mismatch (stored 0x{expected:X8}, calculated 0x{actual:X8}).");
  }

  private static void ValidateGroupDescriptorChecksum(Geometry geometry, uint group, byte[] descriptor) {
    var expected = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(0x1E, 2));
    var copy = (byte[])descriptor.Clone();
    BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(0x1E, 2), 0);

    Span<byte> groupBytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(groupBytes, group);
    var actual = Crc32c(Crc32c(geometry.ChecksumSeed, groupBytes), copy);
    if ((ushort)actual != expected)
      throw new InvalidDataException(
        $"Lustre/ldiskfs: group {group} descriptor checksum mismatch (stored 0x{expected:X4}, calculated 0x{(ushort)actual:X4}).");
  }

  private static void ValidateBlockBitmapChecksum(
      Geometry geometry,
      uint group,
      ReadOnlySpan<byte> descriptor,
      ReadOnlySpan<byte> bitmap) {
    var meaningfulBytes = checked((int)(((ulong)geometry.BlocksPerGroup + 7) / 8));
    if (meaningfulBytes > bitmap.Length)
      throw new InvalidDataException($"Lustre/ldiskfs: group {group} block bitmap geometry exceeds its bitmap block.");

    var actual = Crc32c(geometry.ChecksumSeed, bitmap[..meaningfulBytes]);
    uint expected = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(0x18, 2));
    if (geometry.DescriptorSize >= 64)
      expected |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(0x38, 2)) << 16;

    var matches = geometry.DescriptorSize >= 64
      ? actual == expected
      : (ushort)actual == (ushort)expected;
    if (!matches)
      throw new InvalidDataException(
        $"Lustre/ldiskfs: group {group} block bitmap checksum mismatch (stored 0x{expected:X8}, calculated 0x{actual:X8}).");
  }

  private static uint Crc32c(uint seed, ReadOnlySpan<byte> data) {
    const uint polynomial = 0x82F63B78u;
    var crc = seed;
    foreach (var value in data) {
      crc ^= value;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
    }
    return crc;
  }

  private static long CheckedByteOffset(ulong block, int blockSize) {
    if (block > (ulong)(long.MaxValue / blockSize))
      throw new InvalidDataException("Lustre/ldiskfs: block address exceeds stream addressing range.");
    return checked((long)block * blockSize);
  }

  private static void ZeroRange(Stream image, long offset, long length, byte[] zeros) {
    image.Position = offset;
    while (length > 0) {
      var chunk = (int)Math.Min(length, zeros.Length);
      image.Write(zeros, 0, chunk);
      length -= chunk;
    }
  }
}
