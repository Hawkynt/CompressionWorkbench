using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzo;

namespace FileFormat.Lzop;

/// <summary>
/// Writes data to the LZOP file format.
/// </summary>
public static class LzopWriter {
  /// <summary>
  /// Compresses the given data and returns it wrapped in an LZOP container.
  /// </summary>
  /// <param name="data">The input data to compress.</param>
  /// <param name="fileName">Optional original filename to embed in the LZOP header.</param>
  /// <param name="level">The compression level.</param>
  /// <returns>A byte array containing the complete LZOP file.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, string? fileName = null,
      LzoCompressionLevel level = LzoCompressionLevel.Fast) {
    using var ms = new MemoryStream();
    WriteHeader(ms, fileName);
    WriteBlocks(ms, data, level);
    return ms.ToArray();
  }

  private static void WriteHeader(Stream stream, string? fileName) {
    // Magic
    stream.Write(LzopConstants.Magic);

    // We need to checksum everything from 'version' through 'name', so buffer it.
    using var headerBuf = new MemoryStream();

    // version (u16 BE)
    WriteU16Be(headerBuf, LzopConstants.Version);
    // lib_version (u16 BE)
    WriteU16Be(headerBuf, LzopConstants.LibVersion);
    // version_needed (u16 BE)
    WriteU16Be(headerBuf, LzopConstants.VersionNeeded);
    // method (u8)
    headerBuf.WriteByte(LzopConstants.MethodLzo1X1);
    // level (u8)
    headerBuf.WriteByte(LzopConstants.DefaultLevel);
    // flags (u32 BE) — we include Adler-32 checksums for both uncompressed and compressed data
    WriteU32Be(headerBuf, LzopConstants.FlagAdler32D | LzopConstants.FlagAdler32C);
    // No filter field (flag bit 11 not set)
    // mode (u32 BE) — 0 means unspecified
    WriteU32Be(headerBuf, 0);
    // mtime_low (u32 BE) — 0
    WriteU32Be(headerBuf, 0);
    // mtime_high (u32 BE) — 0 (version >= 0x0940)
    WriteU32Be(headerBuf, 0);

    // name_length + name
    if (fileName is { Length: > 0 }) {
      var nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);
      // Truncate to 255 bytes max
      if (nameBytes.Length > 255)
        nameBytes = nameBytes[..255];
      headerBuf.WriteByte((byte)nameBytes.Length);
      headerBuf.Write(nameBytes);
    } else {
      headerBuf.WriteByte(0);
    }

    // Compute Adler-32 of the header bytes and write everything + checksum
    var headerBytes = headerBuf.ToArray();
    stream.Write(headerBytes);

    var checksum = Adler32.Compute(headerBytes);
    WriteU32Be(stream, checksum);
  }

  private static void WriteBlocks(Stream stream, ReadOnlySpan<byte> data,
      LzoCompressionLevel level = LzoCompressionLevel.Fast) {
    var adler = new Adler32();
    var offset = 0;

    while (offset < data.Length) {
      var blockLen = Math.Min(LzopConstants.BlockSize, data.Length - offset);
      var block = data.Slice(offset, blockLen);
      offset += blockLen;

      // Compute uncompressed checksum
      adler.Reset();
      adler.Update(block);
      var uncompressedChecksum = adler.Value;

      // Compress the block
      var compressed = Lzo1xCompressor.Compress(block, level);

      // If compression made it larger, store uncompressed
      bool stored;
      byte[] payload;
      if (compressed.Length >= blockLen) {
        payload = block.ToArray();
        stored = true;
      } else {
        payload = compressed;
        stored = false;
      }

      // Compute compressed checksum
      adler.Reset();
      adler.Update(payload);
      var compressedChecksum = adler.Value;

      // uncompressed_size (u32 BE)
      WriteU32Be(stream, (uint)blockLen);
      // compressed_size (u32 BE) — same as uncompressed if stored
      WriteU32Be(stream, (uint)payload.Length);
      // uncompressed checksum (FlagAdler32D is set)
      WriteU32Be(stream, uncompressedChecksum);
      // compressed checksum (FlagAdler32C is set, only if not stored)
      if (!stored)
        WriteU32Be(stream, compressedChecksum);

      // payload
      stream.Write(payload);
    }

    // End-of-stream: uncompressed_size = 0
    WriteU32Be(stream, 0);
  }

  private static void WriteU16Be(Stream stream, ushort value) {
    stream.WriteByte((byte)(value >> 8));
    stream.WriteByte((byte)(value & 0xFF));
  }

  private static void WriteU32Be(Stream stream, uint value) {
    stream.WriteByte((byte)(value >> 24));
    stream.WriteByte((byte)((value >> 16) & 0xFF));
    stream.WriteByte((byte)((value >> 8) & 0xFF));
    stream.WriteByte((byte)(value & 0xFF));
  }
}
