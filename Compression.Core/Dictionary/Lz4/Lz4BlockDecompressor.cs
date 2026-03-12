using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lz4;

/// <summary>
/// Decompresses data in the LZ4 block format.
/// </summary>
public static class Lz4BlockDecompressor {
  /// <summary>
  /// Decompresses an LZ4-compressed block.
  /// </summary>
  /// <param name="source">The compressed data.</param>
  /// <param name="originalSize">The expected decompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> source, int originalSize) {
    var output = new byte[originalSize];
    var written = Decompress(source, output);
    if (written != originalSize)
      throw new InvalidDataException(
        $"LZ4 decompression size mismatch: expected {originalSize}, got {written}.");
    return output;
  }

  /// <summary>
  /// Decompresses into a destination buffer, returning bytes written.
  /// </summary>
  /// <param name="source">The compressed data.</param>
  /// <param name="dest">The output buffer.</param>
  /// <returns>Number of bytes written.</returns>
  public static int Decompress(ReadOnlySpan<byte> source, Span<byte> dest) {
    var srcPos = 0;
    var dstPos = 0;
    var srcLen = source.Length;
    var dstLen = dest.Length;

    while (srcPos < srcLen) {
      // Read token
      int token = source[srcPos++];
      var litLen = token >> 4;
      var matchLen = token & 0x0F;

      // Decode literal length
      if (litLen == Lz4Constants.RunMask) {
        int extra;
        do {
          if (srcPos >= srcLen)
            throw new InvalidDataException("Unexpected end of LZ4 data in literal length.");
          extra = source[srcPos++];
          litLen += extra;
        } while (extra == 255);
      }

      // Copy literals
      if (litLen > 0) {
        if (srcPos + litLen > srcLen)
          throw new InvalidDataException("Unexpected end of LZ4 data in literals.");
        if (dstPos + litLen > dstLen)
          throw new InvalidDataException("LZ4 output buffer overflow in literals.");
        source.Slice(srcPos, litLen).CopyTo(dest[dstPos..]);
        srcPos += litLen;
        dstPos += litLen;
      }

      // Check if this is the last sequence (no match follows)
      if (srcPos >= srcLen)
        break;

      // Read match offset (16-bit LE)
      if (srcPos + 1 >= srcLen)
        throw new InvalidDataException("Unexpected end of LZ4 data in match offset.");
      int offset = BinaryPrimitives.ReadUInt16LittleEndian(source[srcPos..]);
      srcPos += 2;

      if (offset == 0)
        throw new InvalidDataException("LZ4 match offset cannot be zero.");

      // Decode match length
      matchLen += Lz4Constants.MinMatch;
      if ((token & 0x0F) == Lz4Constants.RunMask) {
        int extra;
        do {
          if (srcPos >= srcLen)
            throw new InvalidDataException("Unexpected end of LZ4 data in match length.");
          extra = source[srcPos++];
          matchLen += extra;
        } while (extra == 255);
      }

      // Copy match (may overlap)
      var matchSrc = dstPos - offset;
      if (matchSrc < 0)
        throw new InvalidDataException("LZ4 match offset exceeds output position.");
      if (dstPos + matchLen > dstLen)
        throw new InvalidDataException("LZ4 output buffer overflow in match copy.");

      for (var i = 0; i < matchLen; i++)
        dest[dstPos + i] = dest[matchSrc + i];
      dstPos += matchLen;
    }

    return dstPos;
  }
}
