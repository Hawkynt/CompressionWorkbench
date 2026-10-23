using System.Buffers.Binary;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Lzjb;
using Compression.Core.Streams;
using FileFormat.Zlib;
using Compression.Core.Transforms;
using FileFormat.Zstd;

namespace FileSystem.Zfs;

/// <summary>
/// Turns a block's physical bytes into its logical bytes for every compression id ZFS
/// writes (table in <c>docs/ZFS-ON-DISK.md</c>), through this repository's own codecs.
/// </summary>
internal static class ZfsCompression {
  public static string Name(byte id) => id switch {
    ZfsConstants.CompressOff => "off",
    ZfsConstants.CompressLzjb => "lzjb",
    ZfsConstants.CompressEmpty => "empty",
    >= ZfsConstants.CompressGzip1 and <= ZfsConstants.CompressGzip9 => $"gzip-{id - ZfsConstants.CompressGzip1 + 1}",
    ZfsConstants.CompressZle => "zle",
    ZfsConstants.CompressLz4 => "lz4",
    ZfsConstants.CompressZstd => "zstd",
    _ => $"compression {id}",
  };

  public static byte[] Decompress(byte id, byte[] physical, int logicalSize) {
    switch (id) {
      case ZfsConstants.CompressOff:
        return physical.Length == logicalSize ? physical : Fit(physical, logicalSize);
      case ZfsConstants.CompressEmpty:
        return new byte[logicalSize];
      case ZfsConstants.CompressLzjb:
        return LzjbDecompressor.Decompress(physical, logicalSize);
      case ZfsConstants.CompressZle: {
        var output = new byte[logicalSize];
        ZleEncoding.Decode(physical, output);
        return output;
      }
      case >= ZfsConstants.CompressGzip1 and <= ZfsConstants.CompressGzip9:
        return Gzip(physical, logicalSize);
      case ZfsConstants.CompressLz4: {
        var length = LengthPrefix(physical, 4, "LZ4");
        var output = new byte[logicalSize];
        var written = Lz4BlockDecompressor.Decompress(physical.AsSpan(4, length), output);
        if (written != logicalSize)
          throw new InvalidDataException($"ZFS: LZ4 block decoded to {written} of {logicalSize} bytes.");
        return output;
      }
      case ZfsConstants.CompressZstd:
        return Zstd(physical.AsSpan(8, LengthPrefix(physical, 8, "zstd")), logicalSize);
      default:
        throw new NotSupportedException($"ZFS: compression id {id} is not a ZFS algorithm.");
    }
  }

  private static int LengthPrefix(byte[] physical, int header, string what) {
    if (physical.Length < header)
      throw new InvalidDataException($"ZFS: {what} block shorter than its header.");
    var length = BinaryPrimitives.ReadUInt32BigEndian(physical);
    if (length > (uint)(physical.Length - header))
      throw new InvalidDataException($"ZFS: {what} block declares {length} bytes, holds {physical.Length - header}.");
    return (int)length;
  }

  private static byte[] Gzip(byte[] physical, int logicalSize) {
    using var input = new MemoryStream(physical, writable: false);
    using var output = new MemoryStream(logicalSize);
    // The zlib stream ends at its Adler-32; the zeros after it pad the block to a sector.
    ZlibStream.Decompress(input, output);
    return Fit(output.GetBuffer().AsSpan(0, (int)output.Length).ToArray(), logicalSize, "gzip");
  }

  private static byte[] Zstd(ReadOnlySpan<byte> frame, int logicalSize) {
    // OpenZFS writes magicless frames: the 4-byte magic is implied, so it is put back.
    var full = new byte[4 + frame.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(full, 0xFD2FB528);
    frame.CopyTo(full.AsSpan(4));
    using var input = new MemoryStream(full, writable: false);
    using var zstd = new ZstdStream(input, CompressionStreamMode.Decompress);
    var output = new byte[logicalSize];
    var total = 0;
    while (total < logicalSize) {
      var n = zstd.Read(output, total, logicalSize - total);
      if (n == 0) break;
      total += n;
    }
    if (total != logicalSize)
      throw new InvalidDataException($"ZFS: zstd frame decoded to {total} of {logicalSize} bytes.");
    return output;
  }

  private static byte[] Fit(byte[] data, int logicalSize, string? what = null) {
    if (what != null && data.Length != logicalSize)
      throw new InvalidDataException($"ZFS: {what} block decoded to {data.Length} of {logicalSize} bytes.");
    if (data.Length == logicalSize) return data;
    var fitted = new byte[logicalSize];
    data.AsSpan(0, Math.Min(data.Length, logicalSize)).CopyTo(fitted);
    return fitted;
  }
}
