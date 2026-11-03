#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lz4;
using FileFormat.Zstd;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// How one extent's bytes are stored, when they are anything other than plain
/// unchecksummed sectors: an encoded region (checksummed, possibly compressed),
/// or data held inline in the key itself.
/// </summary>
/// <remarks>
/// <c>docs/BCACHEFS-ON-DISK.md</c>, "Compressed and checksummed data", is the
/// specification this follows.
/// </remarks>
internal sealed record BcacheFsStoredExtent(long RegionSector, BcacheFsExtentCrc? Crc, byte[]? Inline) {

  /// <summary>Sectors the extent occupies on the device; zero for inline data.</summary>
  internal int PhysicalSectors => this.Inline != null ? 0 : (int)(this.Crc?.CompressedSize ?? 0);

  internal bool Compressed => this.Crc?.Compressed == true;
}

/// <summary>Verifies and decodes the stored form of an extent into its live bytes.</summary>
internal static class BcacheFsExtentData {

  /// <summary>
  /// Whether this reader can decode an extent described by <paramref name="crc" />,
  /// and if not, why.
  /// </summary>
  internal static bool CanDecode(BcacheFsExtentCrc crc, out string reason) {
    if (crc.ChecksumType is BcacheFsChecksumType.ChaCha20Poly1305_80 or BcacheFsChecksumType.ChaCha20Poly1305_128) {
      reason = "extent is encrypted (ChaCha20/Poly1305) and this reader has no cipher or key support";
      return false;
    }
    if (!Enum.IsDefined(crc.ChecksumType)) {
      reason = $"extent checksum type {(int)crc.ChecksumType} is not known here";
      return false;
    }
    if (!Enum.IsDefined(crc.CompressionType)) {
      reason = $"extent compression type {(int)crc.CompressionType} is not known here";
      return false;
    }
    if (crc.Offset + 1 > crc.UncompressedSize) {
      reason = $"extent offset {crc.Offset} lies outside its {crc.UncompressedSize}-sector region";
      return false;
    }
    if (!crc.Compressed && crc.CompressedSize != crc.UncompressedSize) {
      reason = "an uncompressed extent claims different stored and decoded sizes";
      return false;
    }
    reason = string.Empty;
    return true;
  }

  /// <summary>
  /// The <paramref name="liveSectors" /> sectors of live data an extent holds,
  /// checked against its checksum and decompressed.
  /// </summary>
  /// <exception cref="InvalidDataException">The checksum does not match or the data does not decode.</exception>
  internal static byte[] ReadLive(Stream device, BcacheFsStoredExtent stored, int liveSectors) {
    var live = new byte[(long)liveSectors * SectorSize];
    if (stored.Inline is { } inline) {
      inline.AsSpan(0, Math.Min(inline.Length, live.Length)).CopyTo(live);
      return live;
    }

    var crc = stored.Crc ?? throw new InvalidOperationException("a stored extent has neither inline data nor a checksum entry");
    if (crc.Offset + (long)liveSectors > crc.UncompressedSize)
      throw new InvalidDataException(
        $"bcachefs: extent's {liveSectors} live sectors at offset {crc.Offset} overrun its {crc.UncompressedSize}-sector region.");

    var region = new byte[(long)crc.CompressedSize * SectorSize];
    device.Position = stored.RegionSector * SectorSize;
    device.ReadExactly(region);

    if (!BcacheFsChecksumCodec.TryCompute(crc.ChecksumType, region, out var computed))
      throw new NotSupportedException($"bcachefs: checksum type {crc.ChecksumType} cannot be verified here.");
    if (computed != crc.Checksum)
      throw new InvalidDataException(
        $"bcachefs: {crc.ChecksumType} checksum mismatch in the extent at sector {stored.RegionSector}: "
        + $"stored {crc.Checksum}, computed {computed}.");

    var decoded = crc.Compressed ? Decompress(crc, region) : region;
    decoded.AsSpan((int)crc.Offset * SectorSize, live.Length).CopyTo(live);
    return live;
  }

  /// <summary>
  /// Whether zeroing the live sectors' bytes from <paramref name="liveBytes" /> to
  /// <paramref name="allocatedBytes" /> leaves the extent's checksum valid: true for an
  /// uncompressed region whose checksum was taken with that tail already zero.
  /// </summary>
  internal static bool ZeroingTailKeepsChecksum(Stream device, BcacheFsStoredExtent stored, long liveBytes, long allocatedBytes) {
    if (stored.Inline != null || stored.Crc is not { Compressed: false } crc) return false;
    var region = new byte[(long)crc.CompressedSize * SectorSize];
    var start = crc.Offset * (long)SectorSize;
    if (start + allocatedBytes > region.Length) return false;
    device.Position = stored.RegionSector * SectorSize;
    device.ReadExactly(region);
    region.AsSpan((int)(start + liveBytes), (int)(allocatedBytes - liveBytes)).Clear();
    return BcacheFsChecksumCodec.TryCompute(crc.ChecksumType, region, out var computed) && computed == crc.Checksum;
  }

  private static byte[] Decompress(BcacheFsExtentCrc crc, byte[] region) {
    var size = checked((int)crc.UncompressedSize * SectorSize);
    try {
      var output = crc.CompressionType switch {
        BcacheFsCompressionType.Lz4 or BcacheFsCompressionType.Lz4Old =>
          Lz4BlockDecompressor.Decompress(region.AsSpan(0, Lz4BlockLength(region, size)), size),
        BcacheFsCompressionType.Gzip => DeflateDecompressor.Decompress(region),
        BcacheFsCompressionType.Zstd => Zstd(region, size),
        _ => throw new NotSupportedException($"bcachefs: compression type {crc.CompressionType} is not decoded here."),
      };
      if (output.Length != size)
        throw new InvalidDataException(
          $"bcachefs: {crc.CompressionType} extent decoded to {output.Length} bytes, not {size}.");
      return output;
    } catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException
                                  or EndOfStreamException) {
      throw new InvalidDataException($"bcachefs: {crc.CompressionType} extent does not decode: {e.Message}", e);
    }
  }

  /// <summary>
  /// How many bytes of a padded LZ4 block produce the first <paramref name="size" />
  /// output bytes.
  /// </summary>
  /// <remarks>
  /// The block is followed by zero padding to the next block boundary, which is
  /// not LZ4 and must not be parsed; the kernel's partial decoder simply stops once
  /// the output is full. This walks the sequence headers, counting output, to find
  /// where that happens.
  /// </remarks>
  private static int Lz4BlockLength(ReadOnlySpan<byte> block, int size) {
    var at = 0;
    var produced = 0L;
    while (at < block.Length) {
      var token = block[at++];
      long literals = token >> 4;
      if (literals == 15) {
        byte more;
        do {
          if (at >= block.Length) throw new InvalidDataException("bcachefs: LZ4 block ends inside a literal length.");
          more = block[at++];
          literals += more;
        } while (more == 255);
      }
      at = checked(at + (int)literals);
      produced += literals;
      if (produced >= size || at >= block.Length) return Math.Min(at, block.Length);

      at += 2;                                                                // match offset
      long match = (token & 15) + 4;
      if ((token & 15) == 15) {
        byte more;
        do {
          if (at >= block.Length) throw new InvalidDataException("bcachefs: LZ4 block ends inside a match length.");
          more = block[at++];
          match += more;
        } while (more == 255);
      }
      produced += match;
      if (produced >= size) return Math.Min(at, block.Length);
    }
    return block.Length;
  }

  private static byte[] Zstd(byte[] region, int size) {
    if (region.Length < 4) throw new InvalidDataException("bcachefs: zstd extent is shorter than its length prefix.");
    var length = BinaryPrimitives.ReadUInt32LittleEndian(region);
    if (length > region.Length - 4)
      throw new InvalidDataException($"bcachefs: zstd extent claims {length} bytes in a {region.Length - 4}-byte region.");

    var output = new ZstdBuildingBlock().Decompress(region.AsSpan(4, (int)length));
    if (output.Length != size)
      throw new InvalidDataException($"bcachefs: zstd extent decodes to {output.Length} bytes, not {size}.");
    return output;
  }
}
