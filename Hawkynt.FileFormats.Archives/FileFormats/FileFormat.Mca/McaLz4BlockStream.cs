#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lz4;

namespace FileFormat.Mca;

/// <summary>Clean-room implementation of the LZ4-Java block stream used by MCA compression id 4.</summary>
internal static class McaLz4BlockStream {
  private static ReadOnlySpan<byte> Magic => "LZ4Block"u8;
  private const int BlockSize = 1 << 16;
  private const int CompressionLevel = 6;
  private const uint ChecksumSeed = 0x9747B28C;
  private const int HeaderSize = 21;
  private const int MaxDecodedSize = 256 * 1024 * 1024;
  private const byte RawMethod = 0x10;
  private const byte CompressedMethod = 0x20;

  public static byte[] Compress(ReadOnlySpan<byte> source, Lz4CompressionLevel level) {
    using var output = new MemoryStream();
    output.Write(Magic);
    var offset = 0;
    Span<byte> header = stackalloc byte[HeaderSize];
    while (offset < source.Length) {
      var block = source.Slice(offset, Math.Min(BlockSize, source.Length - offset));
      var compressed = Lz4BlockCompressor.Compress(block, level);
      var isRaw = compressed.Length >= block.Length;
      var payload = isRaw ? block : compressed.AsSpan();
      Magic.CopyTo(header);
      header[8] = (byte)((isRaw ? RawMethod : CompressedMethod) | CompressionLevel);
      BinaryPrimitives.WriteInt32LittleEndian(header[9..], payload.Length);
      BinaryPrimitives.WriteInt32LittleEndian(header[13..], block.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(header[17..], XxHash32.Compute(block, ChecksumSeed));
      output.Write(header);
      output.Write(payload);
      offset += block.Length;
    }

    WriteEndMarker(output, header);
    return output.ToArray();
  }

  public static byte[] Decompress(ReadOnlySpan<byte> source) {
    using var output = new MemoryStream();
    var offset = 0;
    while (true) {
      if (source.Length - offset < HeaderSize)
        throw new InvalidDataException("Truncated LZ4-Java block header in MCA chunk.");
      var header = source.Slice(offset, HeaderSize);
      offset += HeaderSize;
      if (!header[..8].SequenceEqual(Magic))
        throw new InvalidDataException("Invalid LZ4-Java block magic in MCA chunk.");

      var token = header[8];
      var method = (byte)(token & 0xF0);
      var level = token & 0x0F;
      if (method is not (RawMethod or CompressedMethod))
        throw new InvalidDataException($"Unsupported LZ4-Java block method 0x{method:X2}.");

      var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(header[9..]);
      var uncompressedLength = BinaryPrimitives.ReadInt32LittleEndian(header[13..]);
      var checksum = BinaryPrimitives.ReadUInt32LittleEndian(header[17..]);
      if (compressedLength == 0 && uncompressedLength == 0) {
        if (checksum != 0 || offset != source.Length)
          throw new InvalidDataException("Invalid LZ4-Java end marker or trailing bytes in MCA chunk.");
        return output.ToArray();
      }

      var blockSize = 1 << (level + 10);
      if (compressedLength <= 0 || uncompressedLength <= 0 || uncompressedLength > blockSize ||
          compressedLength > source.Length - offset || output.Length + uncompressedLength > MaxDecodedSize)
        throw new InvalidDataException("Invalid or oversized LZ4-Java block lengths in MCA chunk.");
      var payload = source.Slice(offset, compressedLength);
      offset += compressedLength;
      var decoded = method switch {
        RawMethod when compressedLength == uncompressedLength => payload.ToArray(),
        RawMethod => throw new InvalidDataException("Raw LZ4-Java block lengths do not match."),
        CompressedMethod => Lz4BlockDecompressor.Decompress(payload, uncompressedLength),
        _ => throw new InvalidDataException("Unsupported LZ4-Java block method."),
      };
      if (XxHash32.Compute(decoded, ChecksumSeed) != checksum)
        throw new InvalidDataException("LZ4-Java block checksum mismatch in MCA chunk.");
      output.Write(decoded);
    }
  }

  private static void WriteEndMarker(Stream output, Span<byte> header) {
    Magic.CopyTo(header);
    header[8] = (byte)(RawMethod | CompressionLevel);
    header[9..].Clear();
    output.Write(header);
  }
}
