using System.Buffers.Binary;
using Compression.Core.Streams;
using FileFormat.Brotli;
using FileFormat.Lz4;
using FileFormat.Zstd;

namespace FileFormat.SevenZip;

/// <summary>
/// Decoders for the coders 7-Zip-zstd registers under 0x04F711xx (Zstandard, Brotli, LZ4), which
/// py7zr also writes for Zstandard and Brotli.
/// </summary>
/// <remarks>
/// <para>Framing, as measured on archives 7-Zip-zstd 26.02 and py7zr 1.1.0 wrote
/// (Compression.Tests/SevenZip/ReferenceVectors):</para>
/// <list type="bullet">
///   <item><description>Zstandard: one or more standard zstd frames (RFC 8878), nothing around them.</description></item>
///   <item><description>Brotli: 7-Zip-zstd prefixes every independently compressed chunk with a 16-byte
///   skippable frame — UInt32 0x184D2A50, UInt32 8, UInt32 compressed size, UInt16 "BR" (0x5242),
///   UInt16 size hint — followed by that many bytes of a plain RFC 7932 stream. py7zr writes one plain
///   stream with no prefix.</description></item>
///   <item><description>LZ4: 7-Zip-zstd's multi-threaded mode prefixes each chunk with a 12-byte
///   skippable frame — UInt32 0x184D2A50, UInt32 4, UInt32 compressed size — followed by standard LZ4
///   frames; single-threaded mode writes the standard LZ4 frames bare.</description></item>
/// </list>
/// </remarks>
internal static class SevenZipZstdFamilyCoders {

  public static byte[] DecodeZstd(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var zstd = new ZstdStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
    using var output = new MemoryStream();
    zstd.CopyTo(output);
    return output.ToArray();
  }

  public static byte[] DecodeBrotli(byte[] data) {
    if (!IsSkippable(data, 0, 8))
      return BrotliStream.Decompress(data);

    using var output = new MemoryStream();
    var pos = 0;
    while (pos < data.Length) {
      if (!IsSkippable(data, pos, 8) || data.Length - pos < 16)
        throw new InvalidDataException($"7z Brotli: expected a skippable-frame chunk header at offset {pos}.");
      var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 8));
      if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 12)) != SevenZipConstants.BrotliMtMarker)
        throw new InvalidDataException($"7z Brotli: chunk header at offset {pos} lacks the BR marker.");
      pos += 16;
      if (size > (uint)(data.Length - pos))
        throw new InvalidDataException("7z Brotli: chunk overruns the packed stream.");
      var chunk = BrotliStream.Decompress(data.AsSpan(pos, (int)size));
      output.Write(chunk);
      pos += (int)size;
    }
    return output.ToArray();
  }

  public static byte[] DecodeLz4(byte[] data) {
    using var output = new MemoryStream();
    var pos = 0;
    while (pos < data.Length) {
      if (IsSkippable(data, pos, 4)) {
        if (data.Length - pos < 12)
          throw new InvalidDataException("7z LZ4: truncated chunk header.");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 8));
        pos += 12;
        if (size > (uint)(data.Length - pos))
          throw new InvalidDataException("7z LZ4: chunk overruns the packed stream.");
        DecodeLz4Frames(data, pos, (int)size, output);
        pos += (int)size;
        continue;
      }
      DecodeLz4Frames(data, pos, data.Length - pos, output);
      break;
    }
    return output.ToArray();
  }

  private static void DecodeLz4Frames(byte[] data, int offset, int count, MemoryStream output) {
    using var input = new MemoryStream(data, offset, count, writable: false);
    var reader = new Lz4FrameReader(input);
    var frames = 0;
    while (input.Position < input.Length && reader.TryReadFrameTo(output))
      ++frames;
    if (frames == 0 || input.Position != input.Length)
      throw new InvalidDataException("7z LZ4: packed data is not a sequence of LZ4 frames.");
  }

  private static bool IsSkippable(byte[] data, int pos, uint headerPayload)
    => data.Length - pos >= 8
       && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) == SevenZipConstants.MtSkippableFrameMagic
       && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4)) == headerPayload;
}
