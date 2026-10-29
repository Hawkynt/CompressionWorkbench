using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzo;
using Compression.Core.Streams;

namespace FileSystem.Btrfs;

/// <summary>
/// Decodes the payload of a compressed btrfs file extent (the <c>compression</c> byte of
/// <c>btrfs_file_extent_item</c>): 1 = zlib, 2 = LZO, 3 = zstd.
/// </summary>
/// <remarks>
/// <para>Framing, per the btrfs on-disk format documentation (btrfs.readthedocs.io,
/// "Compression"):</para>
/// <list type="bullet">
///   <item>zlib — one RFC 1950 stream per extent; the rest of the last sector is padding.</item>
///   <item>LZO — a little-endian u32 holding the total framed length (header included),
///   then segments of <c>u32 length + LZO1X data</c>, each decompressing to at most one
///   sector. A segment header never straddles a sector boundary: when fewer than four
///   bytes are left in the current sector they are zero padding and the next header
///   starts on the following sector.</item>
///   <item>zstd — standard RFC 8878 frames, followed by sector padding.</item>
/// </list>
/// <para>The same framing applies to compressed inline extents. Unknown methods are
/// refused rather than returned as raw bytes.</para>
/// </remarks>
internal static class BtrfsCompression {
  public const byte None = 0;
  public const byte Zlib = 1;
  public const byte Lzo = 2;
  public const byte Zstd = 3;

  /// <summary>
  /// Decompresses <paramref name="data" /> to exactly <paramref name="ramBytes" /> bytes
  /// (the extent's decompressed length), zero-filling a stream that ends early as the kernel does.
  /// </summary>
  public static byte[] Decompress(byte method, ReadOnlySpan<byte> data, long ramBytes, uint sectorSize) {
    if (ramBytes is < 0 or > 64L << 20)
      throw new InvalidDataException($"btrfs: implausible decompressed extent length {ramBytes}.");
    var expected = (int)ramBytes;
    var output = method switch {
      Zlib => DecompressZlib(data),
      Lzo => DecompressLzo(data, expected, sectorSize),
      Zstd => DecompressZstd(data),
      _ => throw new NotSupportedException($"btrfs: compression method {method} is not supported."),
    };
    if (output.Length == expected) return output;
    // ram_bytes is rounded up to the sector size; the stream of a file's tail extent
    // ends at end-of-file, and the kernel zero-fills the rest of the range.
    var result = new byte[expected];
    output.AsSpan(0, Math.Min(output.Length, expected)).CopyTo(result);
    return result;
  }

  private static byte[] DecompressZlib(ReadOnlySpan<byte> data) {
    // RFC 1950 header: CMF (CM = 8 deflate) + FLG, (CMF*256 + FLG) % 31 == 0, no preset dictionary.
    if (data.Length < 2 || (data[0] & 0x0F) != 8 || ((data[0] << 8) | data[1]) % 31 != 0 || (data[1] & 0x20) != 0)
      throw new InvalidDataException("btrfs: zlib extent does not start with a valid zlib header.");
    return DeflateDecompressor.Decompress(data[2..]);
  }

  private static byte[] DecompressLzo(ReadOnlySpan<byte> data, int expected, uint sectorSize) {
    const int lenSize = 4;
    if (data.Length < lenSize)
      throw new InvalidDataException("btrfs: LZO extent is shorter than its header.");
    var total = BinaryPrimitives.ReadUInt32LittleEndian(data);
    if (total < lenSize || total > data.Length)
      throw new InvalidDataException($"btrfs: LZO extent header claims {total} bytes, extent holds {data.Length}.");

    var sector = (int)(sectorSize == 0 ? 4096 : sectorSize);
    // One segment covers at most one sector of output; historic kernels used PAGE_SIZE,
    // so allow up to 64 KiB per segment and let the stream's end marker bound it.
    var segmentMax = Math.Max(sector, 65536);
    var output = new byte[expected];
    var produced = 0;
    var pos = lenSize;
    while (pos < total && produced < expected) {
      var leftInSector = sector - pos % sector;
      if (leftInSector < lenSize)
        pos += leftInSector;
      if (pos + lenSize > total)
        break;
      var segLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
      pos += lenSize;
      if (segLen <= 0 || pos + segLen > total)
        throw new InvalidDataException($"btrfs: LZO segment of {segLen} bytes overruns the extent.");
      var seg = Lzo1xDecompressor.DecompressUpTo(data.Slice(pos, segLen), segmentMax);
      var take = Math.Min(seg.Length, expected - produced);
      seg.AsSpan(0, take).CopyTo(output.AsSpan(produced));
      produced += take;
      pos += segLen;
    }
    return produced == expected ? output : output.AsSpan(0, produced).ToArray();
  }

  private static byte[] DecompressZstd(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray(), writable: false);
    using var zstd = new FileFormat.Zstd.ZstdStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
    using var output = new MemoryStream();
    zstd.CopyTo(output);
    return output.ToArray();
  }
}
