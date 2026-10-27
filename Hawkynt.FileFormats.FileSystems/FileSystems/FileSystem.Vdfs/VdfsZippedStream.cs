using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Core.DiskImage;
using FileFormat.Zlib;

namespace FileSystem.Vdfs;

/// <summary>
/// Union's ZippedStream: how a zipped VDF volume (<see cref="VdfsLayout.VolumeZipped" />)
/// stores one file's payload.
/// </summary>
/// <remarks>
/// <para>A 12-byte stream header — uncompressed length, block size, block count — then, per
/// block, a 12-byte block header (bytes the block inflates to, bytes of compressed data, the
/// stream's block size again) directly followed by that many bytes of a complete zlib stream.
/// Every block is inflated on its own, so any one can be read without the others.</para>
///
/// <para>The layout is ZenKit's (MIT, rung 1), confirmed on a volume VDFSTool zipped: that
/// tool sizes blocks at a quarter of the file, ZenKit at 8 KiB, and both read either.</para>
/// </remarks>
public static class VdfsZippedStream {

  /// <summary>Bytes of the stream header, and of each block header.</summary>
  public const int HeaderSize = 12;

  /// <summary>Block size this writer uses: ZenKit's, which it states is Union's default.</summary>
  public const int DefaultBlockSize = 8192;

  /// <summary>One block of a parsed stream.</summary>
  /// <param name="DataOffset">Absolute offset of the block's zlib data.</param>
  /// <param name="SourceLength">Bytes the block inflates to.</param>
  /// <param name="CompressedLength">Bytes of zlib data.</param>
  public readonly record struct Block(long DataOffset, int SourceLength, int CompressedLength);

  /// <summary>A parsed stream: where it ends and what its blocks are.</summary>
  /// <param name="StoredLength">Bytes the whole stream occupies, headers included.</param>
  /// <param name="BlockSize">The stream's nominal block size.</param>
  /// <param name="Blocks">Its blocks, in order.</param>
  public sealed record Layout(long StoredLength, int BlockSize, IReadOnlyList<Block> Blocks);

  /// <summary>
  /// Parses the stream at <paramref name="offset" /> if it is a well-formed ZippedStream of
  /// <paramref name="expectedLength" /> bytes, or returns null.
  /// </summary>
  /// <remarks>
  /// Null is an answer, not a failure: a zipped volume keeps <c>.wav</c>/<c>.ogg</c> payloads
  /// raw, and ZenKit tells them apart exactly this way. The header must also agree with the
  /// catalog's size, which ZenKit does not ask but which keeps a raw payload that merely
  /// starts with plausible numbers from being taken for a stream.
  /// </remarks>
  public static Layout? TryParse(ImageAccessor data, long offset, long expectedLength) {
    ArgumentNullException.ThrowIfNull(data);
    if (expectedLength <= 0 || offset < 0 || offset > data.Length - HeaderSize) return null;

    var length = data.ReadUInt32(offset);
    var blockSize = data.ReadUInt32(offset + 4);
    var blockCount = data.ReadUInt32(offset + 8);
    if (length != expectedLength || blockSize == 0 || blockSize > int.MaxValue) return null;
    if (blockCount != (length + (ulong)blockSize - 1) / blockSize) return null;

    var blocks = new List<Block>((int)Math.Min(blockCount, 1u << 16));
    var position = offset + HeaderSize;
    var total = 0L;
    for (var index = 0u; index < blockCount; ++index) {
      if (position > data.Length - HeaderSize) return null;
      var source = data.ReadUInt32(position);
      var compressed = data.ReadUInt32(position + 4);
      position += HeaderSize;
      if (source == 0 || source > blockSize || compressed == 0 || compressed > data.Length - position)
        return null;

      blocks.Add(new Block(position, (int)source, (int)compressed));
      position += compressed;
      total += source;
    }

    return total == length ? new Layout(position - offset, (int)blockSize, blocks) : null;
  }

  /// <summary>Inflates every block of <paramref name="layout" /> into <paramref name="destination" />.</summary>
  /// <exception cref="InvalidDataException">A block does not inflate to the length its header gives.</exception>
  public static void Decode(ImageAccessor data, Layout layout, Stream destination) {
    ArgumentNullException.ThrowIfNull(data);
    ArgumentNullException.ThrowIfNull(layout);
    ArgumentNullException.ThrowIfNull(destination);

    for (var index = 0; index < layout.Blocks.Count; ++index) {
      var block = layout.Blocks[index];
      byte[] inflated;
      try {
        inflated = ZlibStream.Decompress(data.Read(block.DataOffset, block.CompressedLength));
      } catch (Exception ex) when (ex is not OutOfMemoryException) {
        throw new InvalidDataException($"VDFS: ZippedStream block {index} does not inflate: {ex.Message}", ex);
      }

      if (inflated.Length != block.SourceLength)
        throw new InvalidDataException(
          $"VDFS: ZippedStream block {index} inflates to {inflated.Length:N0} bytes, its header says {block.SourceLength:N0}.");
      destination.Write(inflated);
    }
  }

  /// <summary>
  /// Writes <paramref name="length" /> bytes of <paramref name="source" /> as a ZippedStream and
  /// returns how many bytes that took.
  /// </summary>
  public static long Encode(Stream source, long length, Stream destination,
      int blockSize = DefaultBlockSize, DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(destination);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
    if (length > uint.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(length), "VDFS: a ZippedStream records its length in 32 bits.");

    var blockCount = (uint)((length + blockSize - 1) / blockSize);
    Span<byte> header = stackalloc byte[HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)length);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..], blockCount);
    destination.Write(header);
    var written = (long)HeaderSize;

    var buffer = new byte[blockSize];
    var remaining = length;
    while (remaining > 0) {
      var take = (int)Math.Min(blockSize, remaining);
      source.ReadExactly(buffer, 0, take);
      var compressed = ZlibStream.Compress(buffer.AsSpan(0, take), level);

      BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)take);
      BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)compressed.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)blockSize);
      destination.Write(header);
      destination.Write(compressed);

      written += HeaderSize + compressed.Length;
      remaining -= take;
    }

    return written;
  }

  /// <summary>Encodes a byte array; see <see cref="Encode(Stream, long, Stream, int, DeflateCompressionLevel)" />.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, int blockSize = DefaultBlockSize,
      DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    using var source = new MemoryStream(data.ToArray(), writable: false);
    using var output = new MemoryStream();
    Encode(source, data.Length, output, blockSize, level);
    return output.ToArray();
  }
}
