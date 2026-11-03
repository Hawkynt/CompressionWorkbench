#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzo;
using Compression.Core.Streams;
using FileFormat.Zstd;

namespace FileSystem.Reiser4;

/// <summary>Compression transform plugins (plugin type 0xC) a cryptcompress file names.</summary>
internal enum Reiser4CompressionId : ushort { Lzo1 = 0, Gzip1 = 1, Zstd1 = 2 }

/// <summary>Compression mode plugins (plugin type 0xD).</summary>
internal enum Reiser4CompressionModeId : ushort { None = 0, Lattice = 1, Ultimate = 2, Force = 3, Convert = 4 }

/// <summary>
/// The disk-cluster encoding of a cryptcompress (<c>ccreg40</c>) file: each logical cluster is
/// stored either as its plain bytes or, when that is shorter, as the compressed stream followed by
/// the stream's 32-bit Adler checksum, little-endian.
/// </summary>
/// <remarks>
/// <para>Which of the two a disk cluster holds is not flagged anywhere: it is compressed exactly when
/// it is shorter than the logical cluster it stands for — the cluster size, or what is left of the
/// file in the last cluster. The compressors are the workbench's own: raw LZO1X for <c>lzo1</c>, a
/// raw deflate stream for <c>gzip1</c>, a Zstandard frame for <c>zstd1</c>. A compressed result is
/// kept only when it and its checksum together are shorter than the input, and inputs below the
/// plugin's minimum (256 bytes for lzo1 and zstd1, 64 for gzip1) are stored plain.</para>
/// <para>Verified by a Reiser4 kernel reading clusters this writes and by this reader decoding
/// clusters the kernel wrote; see <c>docs/REISER4-NATIVE-PROFILE.md</c>.</para>
/// </remarks>
internal static class Reiser4Clusters {

  internal const int ChecksumBytes = 4;

  internal static int MinimumToCompress(Reiser4CompressionId id) => id == Reiser4CompressionId.Gzip1 ? 64 : 256;

  /// <summary>The logical bytes of one disk cluster.</summary>
  internal static byte[] Decode(ReadOnlySpan<byte> disk, int logicalLength, Reiser4CompressionId id) {
    if (disk.Length == logicalLength) return disk.ToArray();
    if (disk.Length > logicalLength || disk.Length <= ChecksumBytes)
      throw new InvalidDataException("Reiser4: a disk cluster is longer than the data it stands for.");
    var stream = disk[..^ChecksumBytes];
    if (Adler32.Compute(stream) != BinaryPrimitives.ReadUInt32LittleEndian(disk[^ChecksumBytes..]))
      throw new InvalidDataException("Reiser4: a compressed disk cluster fails its checksum.");
    var plain = id switch {
      Reiser4CompressionId.Lzo1 => Lzo1xDecompressor.Decompress(stream, logicalLength),
      Reiser4CompressionId.Gzip1 => DeflateDecompressor.Decompress(stream),
      Reiser4CompressionId.Zstd1 => new ZstdBuildingBlock().Decompress(stream),
      _ => throw new NotSupportedException($"Reiser4: compression plugin {id} is not supported."),
    };
    if (plain.Length != logicalLength)
      throw new InvalidDataException("Reiser4: a disk cluster decompresses to the wrong length.");
    return plain;
  }

  /// <summary>The disk form of a logical cluster: compressed with its checksum when that is shorter.</summary>
  internal static byte[] Encode(ReadOnlySpan<byte> logical, Reiser4CompressionId id, Reiser4CompressionModeId mode) {
    if (mode == Reiser4CompressionModeId.None || logical.Length < MinimumToCompress(id)) return logical.ToArray();
    var stream = id switch {
      Reiser4CompressionId.Lzo1 => Lzo1xCompressor.Compress(logical),
      Reiser4CompressionId.Gzip1 => DeflateCompressor.Compress(logical, DeflateCompressionLevel.Fast),
      Reiser4CompressionId.Zstd1 => CompressZstd(logical),
      _ => throw new NotSupportedException($"Reiser4: compression plugin {id} is not supported."),
    };
    if (stream.Length + ChecksumBytes >= logical.Length) return logical.ToArray();
    var disk = new byte[stream.Length + ChecksumBytes];
    stream.CopyTo(disk, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(stream.Length), Adler32.Compute(stream));
    return disk;
  }

  private static byte[] CompressZstd(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    using (var zstd = new ZstdStream(output, CompressionStreamMode.Compress, ZstdCompressionLevel.Fast, leaveOpen: true))
      zstd.Write(data);
    return output.ToArray();
  }
}
