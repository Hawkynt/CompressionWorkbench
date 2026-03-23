using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.Zlib;

/// <summary>
/// Compresses and decompresses data in the zlib format (RFC 1950).
/// </summary>
/// <remarks>
/// The zlib format wraps a Deflate stream with a 2-byte header and a
/// 4-byte Adler-32 checksum trailer. The header encodes the compression
/// method (always Deflate), window size, and compression level.
/// </remarks>
public static class ZlibStream {
  /// <summary>
  /// Compresses data to zlib format.
  /// </summary>
  /// <param name="input">The stream containing uncompressed data.</param>
  /// <param name="output">The stream to write zlib-compressed data to.</param>
  /// <param name="level">The Deflate compression level.</param>
  /// <param name="windowBits">
  /// The window size exponent (8-15). Defaults to 15 (32 KB window).
  /// </param>
  public static void Compress(Stream input, Stream output,
      DeflateCompressionLevel level = DeflateCompressionLevel.Default,
      int windowBits = ZlibConstants.DefaultWindowBits) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentOutOfRangeException.ThrowIfLessThan(windowBits, 8);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(windowBits, 15);

    // Read all input data (needed for Adler-32 and Deflate)
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    // Write 2-byte header
    WriteHeader(output, windowBits, level);

    // Compress with Deflate
    var compressed = DeflateCompressor.Compress(data, level);
    output.Write(compressed);

    // Write Adler-32 checksum (big-endian)
    var adler = Adler32.Compute(data);
    Span<byte> trailer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(trailer, adler);
    output.Write(trailer);
  }

  /// <summary>
  /// Compresses a byte span to zlib format.
  /// </summary>
  /// <param name="data">The uncompressed data.</param>
  /// <param name="level">The Deflate compression level.</param>
  /// <returns>The zlib-compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data,
      DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    using var output = new MemoryStream();
    WriteHeader(output, ZlibConstants.DefaultWindowBits, level);

    var compressed = DeflateCompressor.Compress(data, level);
    output.Write(compressed);

    var adler = Adler32.Compute(data);
    Span<byte> trailer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(trailer, adler);
    output.Write(trailer);

    return output.ToArray();
  }

  /// <summary>
  /// Decompresses zlib-formatted data.
  /// </summary>
  /// <param name="input">The stream containing zlib-compressed data.</param>
  /// <param name="output">The stream to write decompressed data to.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the zlib header is invalid or the Adler-32 checksum does not match.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read 2-byte header
    Span<byte> header = stackalloc byte[2];
    input.ReadExactly(header);
    ParseHeader(header);

    // Read remaining bytes (Deflate data + 4-byte Adler-32)
    using var remaining = new MemoryStream();
    input.CopyTo(remaining);
    var buf = remaining.ToArray();

    if (buf.Length < ZlibConstants.TrailerSize)
      throw new InvalidDataException("Zlib stream is too short for Adler-32 trailer.");

    // Split into Deflate data and trailer
    var deflateData = buf.AsSpan(0, buf.Length - ZlibConstants.TrailerSize);
    var trailerSpan = buf.AsSpan(buf.Length - ZlibConstants.TrailerSize);
    var expectedAdler = BinaryPrimitives.ReadUInt32BigEndian(trailerSpan);

    // Decompress Deflate data
    var decompressed = DeflateDecompressor.Decompress(deflateData);

    // Verify checksum
    var actualAdler = Adler32.Compute(decompressed);
    if (actualAdler != expectedAdler)
      throw new InvalidDataException(
        $"Zlib Adler-32 mismatch: expected 0x{expectedAdler:X8}, got 0x{actualAdler:X8}.");

    output.Write(decompressed);
  }

  /// <summary>
  /// Decompresses zlib-formatted data from a byte span.
  /// </summary>
  /// <param name="data">The zlib-compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < ZlibConstants.HeaderSize + ZlibConstants.TrailerSize)
      throw new InvalidDataException("Zlib data is too short.");

    ParseHeader(data[..2]);

    var deflateData = data[ZlibConstants.HeaderSize..^ZlibConstants.TrailerSize];
    var expectedAdler = BinaryPrimitives.ReadUInt32BigEndian(data[^ZlibConstants.TrailerSize..]);

    var decompressed = DeflateDecompressor.Decompress(deflateData);

    var actualAdler = Adler32.Compute(decompressed);
    if (actualAdler != expectedAdler)
      throw new InvalidDataException(
        $"Zlib Adler-32 mismatch: expected 0x{expectedAdler:X8}, got 0x{actualAdler:X8}.");

    return decompressed;
  }

  private static void WriteHeader(Stream output, int windowBits, DeflateCompressionLevel level) {
    // CMF: method=8 (Deflate), info = windowBits - 8
    var cmf = ZlibConstants.CompressionMethodDeflate | ((windowBits - 8) << 4);

    // FLG: FLEVEL (bits 6-7), FDICT=0 (bit 5), FCHECK (bits 0-4)
    var flevel = level switch {
      DeflateCompressionLevel.None or DeflateCompressionLevel.Fast
        => ZlibConstants.LevelFastest,
      DeflateCompressionLevel.Default => ZlibConstants.LevelDefault,
      _ => ZlibConstants.LevelMaximum,
    };

    var flg = flevel << 6;

    // Adjust FCHECK so (CMF*256 + FLG) is a multiple of 31
    var check = 31 - ((cmf * 256 + flg) % 31);
    flg |= check;

    output.WriteByte((byte)cmf);
    output.WriteByte((byte)flg);
  }

  private static void ParseHeader(ReadOnlySpan<byte> header) {
    int cmf = header[0];
    int flg = header[1];

    // Verify checksum
    if ((cmf * 256 + flg) % 31 != 0)
      throw new InvalidDataException("Invalid zlib header checksum.");

    // Verify compression method
    var method = cmf & 0x0F;
    if (method != ZlibConstants.CompressionMethodDeflate)
      throw new InvalidDataException($"Unsupported zlib compression method: {method}.");

    // FDICT not supported
    if ((flg & 0x20) != 0)
      throw new InvalidDataException("Zlib preset dictionary is not supported.");
  }
}
