using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.Zlib;

/// <summary>
/// Low-level helpers for working with raw Deflate bitstreams inside Zlib framing.
/// Enables zero-decompression restreaming between formats sharing the Deflate codec.
/// </summary>
public static class ZlibRawHelper {

  /// <summary>
  /// Extracts the raw Deflate bitstream from Zlib data without decompressing.
  /// Also returns the Adler-32 checksum from the trailer.
  /// </summary>
  /// <param name="zlibData">The complete Zlib data.</param>
  /// <returns>The raw Deflate bytes and the Adler-32 checksum of the uncompressed data.</returns>
  public static (byte[] DeflateData, uint Adler32) Unwrap(ReadOnlySpan<byte> zlibData) {
    if (zlibData.Length < ZlibConstants.HeaderSize + ZlibConstants.TrailerSize)
      throw new InvalidDataException("Zlib data too short.");

    // Verify header
    int cmf = zlibData[0];
    int flg = zlibData[1];
    if ((cmf * 256 + flg) % 31 != 0)
      throw new InvalidDataException("Invalid zlib header checksum.");
    if ((cmf & 0x0F) != ZlibConstants.CompressionMethodDeflate)
      throw new InvalidDataException("Not Deflate compression.");
    if ((flg & 0x20) != 0)
      throw new InvalidDataException("Preset dictionary not supported.");

    var deflateData = zlibData[ZlibConstants.HeaderSize..^ZlibConstants.TrailerSize].ToArray();
    var adler32 = BinaryPrimitives.ReadUInt32BigEndian(zlibData[^ZlibConstants.TrailerSize..]);
    return (deflateData, adler32);
  }

  /// <summary>
  /// Wraps a raw Deflate bitstream in Zlib framing.
  /// The caller must provide the Adler-32 of the uncompressed data.
  /// </summary>
  /// <param name="deflateData">The raw Deflate bitstream.</param>
  /// <param name="adler32">Adler-32 of the uncompressed data.</param>
  /// <returns>Complete Zlib data.</returns>
  public static byte[] Wrap(ReadOnlySpan<byte> deflateData, uint adler32) {
    using var ms = new MemoryStream();
    // Default header: method=8, window=15 (32KB), level=Default
    // CMF = 0x78 (method 8, info 7 → 32KB window)
    // FLG = adjusted so (CMF*256+FLG) % 31 == 0
    var cmf = 0x78;
    var flg = 0x01; // level bits = 0 (default)
    var check = 31 - ((cmf * 256 + flg) % 31);
    if (check == 31) check = 0;
    flg |= check;
    ms.WriteByte((byte)cmf);
    ms.WriteByte((byte)flg);

    ms.Write(deflateData);

    Span<byte> trailer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(trailer, adler32);
    ms.Write(trailer);

    return ms.ToArray();
  }
}
