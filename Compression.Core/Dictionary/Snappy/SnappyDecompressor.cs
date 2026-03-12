using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Snappy;

/// <summary>
/// Decompresses data in the Snappy block format.
/// </summary>
public static class SnappyDecompressor {
  /// <summary>
  /// Decompresses Snappy-compressed data.
  /// </summary>
  /// <param name="source">The compressed data (including varint size header).</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> source) {
    var pos = 0;
    var originalSize = ReadVarInt(source, ref pos);
    var output = new byte[originalSize];
    var written = DecompressBlock(source[pos..], output);
    return written != originalSize ? throw new InvalidDataException($"Snappy decompression size mismatch: expected {originalSize}, got {written}.") : output;

  }

  /// <summary>
  /// Decompresses Snappy-compressed data into an output buffer.
  /// </summary>
  /// <param name="source">The compressed data (including varint size header).</param>
  /// <param name="dest">The output buffer.</param>
  /// <returns>Number of bytes written.</returns>
  public static int Decompress(ReadOnlySpan<byte> source, Span<byte> dest) {
    var pos = 0;
    ReadVarInt(source, ref pos);
    return DecompressBlock(source[pos..], dest);
  }

  private static int DecompressBlock(ReadOnlySpan<byte> src, Span<byte> dst) {
    var srcPos = 0;
    var dstPos = 0;
    var srcLen = src.Length;
    var dstLen = dst.Length;

    while (srcPos < srcLen && dstPos < dstLen) {
      int tag = src[srcPos++];
      var tagType = tag & 0x03;

      switch (tagType) {
        case SnappyConstants.TagLiteral: {
          var litLen = (tag >> 2) + 1;
          if (litLen > 60) {
            var extraBytes = litLen - 60;
            litLen = 1;
            for (var i = 0; i < extraBytes && srcPos < srcLen; i++)
              litLen += src[srcPos++] << (8 * i);
          }

          if (srcPos + litLen > srcLen)
            throw new InvalidDataException("Snappy: unexpected end of data in literals.");

          if (dstPos + litLen > dstLen)
            throw new InvalidDataException("Snappy: output overflow in literals.");

          src.Slice(srcPos, litLen).CopyTo(dst[dstPos..]);
          srcPos += litLen;
          dstPos += litLen;
          break;
        }

        case SnappyConstants.TagCopy1: {
          if (srcPos >= srcLen)
            throw new InvalidDataException("Snappy: unexpected end of data in copy-1.");

          var length = ((tag >> 2) & 0x07) + 4;
          var offset = ((tag >> 5) << 8) | src[srcPos++];
          if (offset == 0)
            throw new InvalidDataException("Snappy: zero offset in copy-1.");

          dstPos = CopyMatch(dst, dstPos, dstLen, offset, length);
          break;
        }

        case SnappyConstants.TagCopy2: {
          if (srcPos + 1 >= srcLen)
            throw new InvalidDataException("Snappy: unexpected end of data in copy-2.");

          var length = ((tag >> 2) & 0x3F) + 1;
          int offset = BinaryPrimitives.ReadUInt16LittleEndian(src[srcPos..]);
          srcPos += 2;
          if (offset == 0)
            throw new InvalidDataException("Snappy: zero offset in copy-2.");

          dstPos = CopyMatch(dst, dstPos, dstLen, offset, length);
          break;
        }

        case SnappyConstants.TagCopy4: {
          if (srcPos + 3 >= srcLen)
            throw new InvalidDataException("Snappy: unexpected end of data in copy-4.");

          var length = ((tag >> 2) & 0x3F) + 1;
          var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(src[srcPos..]);
          srcPos += 4;
          if (offset == 0)
            throw new InvalidDataException("Snappy: zero offset in copy-4.");

          dstPos = CopyMatch(dst, dstPos, dstLen, offset, length);
          break;
        }
      }
    }

    return dstPos;
  }

  private static int CopyMatch(Span<byte> dst, int dstPos, int dstLen, int offset, int length) {
    var matchSrc = dstPos - offset;
    if (matchSrc < 0)
      throw new InvalidDataException("Snappy: match offset exceeds output position.");
    if (dstPos + length > dstLen)
      throw new InvalidDataException("Snappy: output overflow in match copy.");

    for (var i = 0; i < length; i++)
      dst[dstPos + i] = dst[matchSrc + i];
    dstPos += length;
    return dstPos;
  }

  private static int ReadVarInt(ReadOnlySpan<byte> data, ref int pos) {
    var result = 0;
    var shift = 0;
    while (pos < data.Length) {
      int b = data[pos++];
      result |= (b & 0x7F) << shift;
      if ((b & 0x80) == 0)
        return result;

      shift += 7;
      if (shift > 28)
        throw new InvalidDataException("Snappy: varint too large.");
    }
    throw new InvalidDataException("Snappy: unexpected end of data in varint.");
  }
}
