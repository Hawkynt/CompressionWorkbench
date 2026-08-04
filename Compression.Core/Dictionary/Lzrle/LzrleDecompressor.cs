using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lzrle;

/// <summary>
/// Decompresses data produced by <see cref="LzrleCompressor"/>.
/// </summary>
public static class LzrleDecompressor {
  /// <summary>
  /// Decompresses LZRLE-compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data, prefixed with a 4-byte little-endian original length.</param>
  /// <returns>The original decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">The compressed stream is malformed or truncated.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    if (compressed.Length < 4)
      throw new InvalidDataException("LZRLE stream too short for header.");

    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    var output = new byte[originalLength];
    if (originalLength == 0)
      return output;

    var data = compressed[4..];
    var pos = 0;
    var outPos = 0;

    while (outPos < originalLength) {
      if (pos >= data.Length)
        throw new InvalidDataException("LZRLE stream truncated at token.");

      var token = data[pos++];
      var type = token >> LzrleConstants.LengthFieldBits;
      var field = token & LzrleConstants.LengthFieldMax;

      int raw;
      if (field < LzrleConstants.LengthFieldMax)
        raw = field;
      else
        raw = LzrleConstants.LengthFieldMax + ReadExtendedLength(data, ref pos);

      switch (type) {
        case LzrleConstants.TypeLiteral: {
          var count = raw;
          if (pos + count > data.Length || outPos + count > originalLength)
            throw new InvalidDataException("LZRLE literal run overruns buffer.");
          data.Slice(pos, count).CopyTo(output.AsSpan(outPos));
          pos += count;
          outPos += count;
          break;
        }

        case LzrleConstants.TypeMatch: {
          var length = raw + LzrleConstants.MinMatch;
          if (pos + 4 > data.Length)
            throw new InvalidDataException("LZRLE match token truncated.");
          var distance = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
          pos += 4;
          if (distance == 0 || distance > (uint)outPos || outPos + length > originalLength)
            throw new InvalidDataException("LZRLE match references invalid distance.");
          var srcPos = outPos - (int)distance;
          for (var i = 0; i < length; ++i)
            output[outPos + i] = output[srcPos + i];
          outPos += length;
          break;
        }

        case LzrleConstants.TypeRun: {
          var length = raw + LzrleConstants.MinRun;
          if (pos >= data.Length || outPos + length > originalLength)
            throw new InvalidDataException("LZRLE run token truncated or overruns buffer.");
          var value = data[pos++];
          output.AsSpan(outPos, length).Fill(value);
          outPos += length;
          break;
        }

        default:
          throw new InvalidDataException($"LZRLE stream contains reserved token type {type}.");
      }
    }

    return output;
  }

  private static int ReadExtendedLength(ReadOnlySpan<byte> data, ref int pos) {
    var sum = 0;
    byte b;
    do {
      if (pos >= data.Length)
        throw new InvalidDataException("LZRLE extended length truncated.");
      b = data[pos++];
      sum += b;
    } while (b == 255);

    return sum;
  }
}
