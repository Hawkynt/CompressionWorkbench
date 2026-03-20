using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzo;

namespace FileFormat.Lzop;

/// <summary>
/// Reads and decompresses LZOP format files.
/// </summary>
public sealed class LzopReader {
  private readonly Stream _stream;

  /// <summary>
  /// Gets the original filename stored in the LZOP header, or <c>null</c> if none was stored.
  /// </summary>
  public string? OriginalFileName { get; private set; }

  /// <summary>
  /// Initializes a new <see cref="LzopReader"/> that reads from the given stream.
  /// </summary>
  /// <param name="stream">The stream containing LZOP data to read.</param>
  /// <exception cref="ArgumentNullException">Thrown if <paramref name="stream"/> is null.</exception>
  public LzopReader(Stream stream) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
  }

  /// <summary>
  /// Reads and decompresses all blocks from the LZOP stream.
  /// </summary>
  /// <returns>The fully decompressed data.</returns>
  /// <exception cref="InvalidDataException">Thrown if the LZOP data is malformed or checksums fail.</exception>
  public byte[] Decompress() {
    this.ReadHeader();
    return this.ReadBlocks();
  }

  private void ReadHeader() {
    // Verify magic
    Span<byte> magic = stackalloc byte[LzopConstants.Magic.Length];
    ReadExact(this._stream, magic);
    if (!magic.SequenceEqual(LzopConstants.Magic))
      throw new InvalidDataException("Not a valid LZOP file: magic bytes mismatch.");

    // We record the stream position just after magic, so we can checksum
    // everything from 'version' through 'name' inclusive.
    using var checksumBuffer = new MemoryStream();

    // version (u16 BE)
    var version = ReadU16Be(this._stream, checksumBuffer);
    // lib_version (u16 BE)
    ReadU16Be(this._stream, checksumBuffer);
    // version_needed (u16 BE)
    var versionNeeded = ReadU16Be(this._stream, checksumBuffer);

    if (version < versionNeeded)
      throw new InvalidDataException($"LZOP file requires version 0x{versionNeeded:X4} but we support 0x{LzopConstants.Version:X4}.");

    // method (u8)
    var method = ReadU8(this._stream, checksumBuffer);
    if (method != LzopConstants.MethodLzo1X1)
      throw new InvalidDataException($"Unsupported LZOP compression method: {method}.");

    // level (u8)
    ReadU8(this._stream, checksumBuffer);

    // flags (u32 BE)
    var flags = ReadU32Be(this._stream, checksumBuffer);

    // filter (u32 BE) — only present if flag bit 11 is set
    if ((flags & 0x00000800u) != 0)
      ReadU32Be(this._stream, checksumBuffer);

    // mode (u32 BE)
    ReadU32Be(this._stream, checksumBuffer);
    // mtime_low (u32 BE)
    ReadU32Be(this._stream, checksumBuffer);
    // mtime_high (u32 BE) — present if version >= 0x0940
    if (version >= 0x0940)
      ReadU32Be(this._stream, checksumBuffer);

    // name_length (u8) + name bytes
    var nameLen = ReadU8(this._stream, checksumBuffer);
    if (nameLen > 0) {
      var nameBytes = new byte[nameLen];
      ReadExact(this._stream, nameBytes);
      checksumBuffer.Write(nameBytes);
      this.OriginalFileName = System.Text.Encoding.UTF8.GetString(nameBytes);
    }

    // header_checksum (u32 BE) — Adler-32 of everything from version through name
    Span<byte> checksumBytes = stackalloc byte[4];
    ReadExact(this._stream, checksumBytes);
    var storedChecksum = (uint)((checksumBytes[0] << 24) | (checksumBytes[1] << 16) | (checksumBytes[2] << 8) | checksumBytes[3]);

    var computed = Adler32.Compute(checksumBuffer.ToArray());
    if (computed != storedChecksum)
      throw new InvalidDataException($"LZOP header checksum mismatch: expected 0x{storedChecksum:X8}, computed 0x{computed:X8}.");

    // Store flags for use during block reading
    this._flags = flags;
  }

  private uint _flags;

  private byte[] ReadBlocks() {
    var result = new MemoryStream();
    var adler = new Adler32();
    var buf4 = new byte[4];

    while (true) {
      // uncompressed_size (u32 BE)
      ReadExact(this._stream, buf4);
      var uncompressedSize = (int)((uint)((buf4[0] << 24) | (buf4[1] << 16) | (buf4[2] << 8) | buf4[3]));

      // End-of-stream marker
      if (uncompressedSize == 0)
        break;

      // compressed_size (u32 BE)
      ReadExact(this._stream, buf4);
      var compressedSize = (int)((uint)((buf4[0] << 24) | (buf4[1] << 16) | (buf4[2] << 8) | buf4[3]));

      // uncompressed checksum (u32 BE) if flag set
      uint storedUncompressedChecksum = 0;
      var hasUncompressedChecksum = (this._flags & LzopConstants.FlagAdler32D) != 0;
      if (hasUncompressedChecksum) {
        ReadExact(this._stream, buf4);
        storedUncompressedChecksum = (uint)((buf4[0] << 24) | (buf4[1] << 16) | (buf4[2] << 8) | buf4[3]);
      }

      // compressed checksum (u32 BE) if flag set and block is actually compressed
      var hasCompressedChecksum = (this._flags & LzopConstants.FlagAdler32C) != 0;
      uint storedCompressedChecksum = 0;
      if (hasCompressedChecksum && compressedSize != uncompressedSize) {
        ReadExact(this._stream, buf4);
        storedCompressedChecksum = (uint)((buf4[0] << 24) | (buf4[1] << 16) | (buf4[2] << 8) | buf4[3]);
      }

      // Read compressed data
      var compressedData = new byte[compressedSize];
      ReadExact(this._stream, compressedData);

      // Verify compressed checksum
      if (hasCompressedChecksum && compressedSize != uncompressedSize) {
        var computedCompressed = Adler32.Compute(compressedData);
        if (computedCompressed != storedCompressedChecksum)
          throw new InvalidDataException($"LZOP block compressed-data checksum mismatch.");
      }

      // Decompress or copy stored block
      byte[] block;
      if (compressedSize == uncompressedSize) {
        block = compressedData;
      } else {
        block = Lzo1xDecompressor.Decompress(compressedData, uncompressedSize);
      }

      // Verify uncompressed checksum
      if (hasUncompressedChecksum) {
        adler.Reset();
        adler.Update(block);
        if (adler.Value != storedUncompressedChecksum)
          throw new InvalidDataException($"LZOP block uncompressed-data checksum mismatch.");
      }

      result.Write(block);
    }

    return result.ToArray();
  }

  private static void ReadExact(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer[read..]);
      if (n == 0)
        throw new InvalidDataException("LZOP stream ended unexpectedly.");
      read += n;
    }
  }

  private static ushort ReadU16Be(Stream stream, MemoryStream? checksumBuffer = null) {
    Span<byte> b = stackalloc byte[2];
    ReadExact(stream, b);
    checksumBuffer?.Write(b);
    return (ushort)((b[0] << 8) | b[1]);
  }

  private static uint ReadU32Be(Stream stream, MemoryStream? checksumBuffer = null) {
    Span<byte> b = stackalloc byte[4];
    ReadExact(stream, b);
    checksumBuffer?.Write(b);
    return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
  }

  private static byte ReadU8(Stream stream, MemoryStream? checksumBuffer = null) {
    Span<byte> b = stackalloc byte[1];
    ReadExact(stream, b);
    checksumBuffer?.Write(b);
    return b[0];
  }
}
