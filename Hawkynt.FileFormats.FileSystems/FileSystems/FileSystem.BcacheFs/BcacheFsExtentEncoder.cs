#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lz4;
using FileFormat.Zstd;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Lays one run of a file's data down the way the volume's IO options ask for it: compressed
/// with the volume's (background) compression where that makes it smaller — kept raw and marked
/// incompressible where it does not — and checksummed with its data checksum, so that a volume
/// at 1.39 finds nothing in the extent it still owes work for.
/// </summary>
/// <remarks>
/// The region shapes are those the reader decodes (<see cref="BcacheFsExtentData" />): an LZ4
/// block, a raw DEFLATE stream ("gzip"), a zstd frame behind its 32-bit length; zero padding to
/// whole blocks after each. The codecs are this package's own.
/// </remarks>
internal static class BcacheFsExtentEncoder {

  /// <summary>How new data is stored: its checksum type and the compression it is offered to.</summary>
  internal readonly record struct DataOptions(BcacheFsChecksumType Checksum, BcacheFsCompressionType Compression) {
    /// <summary>What this package's writer stores: crc32c, uncompressed.</summary>
    internal static DataOptions Plain => new(BcacheFsChecksumType.Crc32C, BcacheFsCompressionType.None);
  }

  /// <summary>One encoded run: the bytes to write and the checksum/compression entry that describes them (none for plain unchecksummed data).</summary>
  internal sealed record Encoded(byte[] Region, int RegionSectors, byte[] Entry);

  /// <summary>
  /// The options a 1.39 superblock asks new data to be stored with: <c>data_checksum</c>, and
  /// <c>background_compression</c>, else <c>compression</c>.
  /// </summary>
  internal static DataOptions Of(ulong[] flags) {
    ulong Bits(int word, int from, int to) => word < flags.Length ? (flags[word] >> from) & ((1UL << (to - from)) - 1) : 0;
    var compression = (int)(Bits(1, 4, 8) | (Bits(4, 56, 60) << 4)) & 0xF;           // the type; the level is the high nibble
    var background = (int)(Bits(2, 0, 4) | (Bits(4, 60, 64) << 4)) & 0xF;
    var option = background != 0 ? background : compression;
    var checksum = Bits(0, 44, 48) switch {
      0 => BcacheFsChecksumType.None,
      1 => BcacheFsChecksumType.Crc32C,
      2 => BcacheFsChecksumType.Crc64,
      3 => BcacheFsChecksumType.XxHash,
      var other => throw new NotSupportedException($"bcachefs: data checksum option {other} is not one this writes."),
    };
    var type = option switch {
      0 => BcacheFsCompressionType.None,
      1 => BcacheFsCompressionType.Lz4,
      2 => BcacheFsCompressionType.Gzip,
      3 => BcacheFsCompressionType.Zstd,
      _ => throw new NotSupportedException($"bcachefs: compression option {option} is not one this writes."),
    };
    return new(checksum, type);
  }

  /// <summary>Encodes <paramref name="live" /> (whole blocks, <paramref name="liveSectors" /> sectors).</summary>
  internal static Encoded Encode(ReadOnlySpan<byte> live, int liveSectors, DataOptions options) {
    var compression = BcacheFsCompressionType.None;
    byte[] region = live.ToArray();
    if (options.Compression != BcacheFsCompressionType.None) {
      var packed = Compress(live, options.Compression);
      var packedSectors = BcacheFsBucketGeometry.RoundUpSectors(packed.Length);
      if (packedSectors < liveSectors) {
        compression = options.Compression;
        region = new byte[packedSectors * SectorSize];
        packed.CopyTo(region, 0);
      } else
        compression = BcacheFsCompressionType.Incompressible;
    }
    var regionSectors = region.Length / SectorSize;

    if (options.Checksum == BcacheFsChecksumType.None && compression == BcacheFsCompressionType.None)
      return new Encoded(region, regionSectors, []);

    if (!BcacheFsChecksumCodec.TryCompute(options.Checksum, region, out var checksum))
      throw new NotSupportedException($"bcachefs: checksum type {options.Checksum} cannot be computed here.");

    // A 32-bit checksum of a run of at most 128 sectors fits the one-word crc32 entry; a 64-bit
    // one takes the two-word crc64 entry.
    var wide = options.Checksum is BcacheFsChecksumType.Crc64 or BcacheFsChecksumType.XxHash or BcacheFsChecksumType.Crc64Nonzero;
    if (!wide && regionSectors <= MaxCrc32ExtentSectors && liveSectors <= MaxCrc32ExtentSectors) {
      var word = 2UL                                                         // entry type: crc32
        | (ulong)(regionSectors - 1) << 2
        | (ulong)(liveSectors - 1) << 9
        | (ulong)options.Checksum << 24
        | (ulong)compression << 28
        | (checksum.Lo & 0xFFFFFFFF) << 32;
      var entry = new byte[8];
      BinaryPrimitives.WriteUInt64LittleEndian(entry, word);
      return new Encoded(region, regionSectors, entry);
    }

    if (regionSectors > 512 || liveSectors > 512)
      throw new NotSupportedException("bcachefs: a crc64 extent entry holds at most 512 sectors.");
    var header = 4UL                                                         // entry type: crc64
      | (ulong)(regionSectors - 1) << 3
      | (ulong)(liveSectors - 1) << 12
      | (ulong)options.Checksum << 40
      | (ulong)compression << 44
      | (checksum.Hi & 0xFFFF) << 48;
    var wideEntry = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(wideEntry, header);
    BinaryPrimitives.WriteUInt64LittleEndian(wideEntry.AsSpan(8), checksum.Lo);
    return new Encoded(region, regionSectors, wideEntry);
  }

  private static byte[] Compress(ReadOnlySpan<byte> data, BcacheFsCompressionType type) {
    switch (type) {
      case BcacheFsCompressionType.Lz4:
        return Lz4BlockCompressor.Compress(data);
      case BcacheFsCompressionType.Gzip:
        return DeflateCompressor.Compress(data);
      case BcacheFsCompressionType.Zstd: {
        var frame = new ZstdBuildingBlock().Compress(data);
        var prefixed = new byte[4 + frame.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(prefixed, (uint)frame.Length);
        frame.CopyTo(prefixed, 4);
        return prefixed;
      }
      default:
        throw new NotSupportedException($"bcachefs: compression type {type} is not written here.");
    }
  }
}
