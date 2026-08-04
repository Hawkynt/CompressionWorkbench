using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lzturbo;

/// <summary>
/// Decompresses data produced by <see cref="LzturboCompressor"/>.
/// </summary>
public static class LzturboDecompressor {
  /// <summary>
  /// Decompresses an LZTURBO-inspired block.
  /// </summary>
  /// <param name="compressed">The compressed block, including magic, method, and length header.</param>
  /// <returns>The original decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">The block header or body is malformed or truncated.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    if (compressed.Length < LzturboConstants.HeaderSize)
      throw new InvalidDataException("LZTURBO block too short for header.");

    if (!compressed[..4].SequenceEqual(LzturboConstants.Magic))
      throw new InvalidDataException("LZTURBO block has an invalid magic.");

    var method = compressed[4];
    if (method != LzturboConstants.Method)
      throw new InvalidDataException($"LZTURBO block uses unsupported method {method}.");

    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(compressed[5..]);
    var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(compressed[9..]);

    var data = compressed[LzturboConstants.HeaderSize..];
    if (bodyLength != data.Length)
      throw new InvalidDataException("LZTURBO block body length does not match header.");

    var output = new byte[originalLength];
    if (originalLength == 0)
      return output;

    var pos = 0;
    var outPos = 0;

    while (outPos < originalLength) {
      if (pos >= data.Length)
        throw new InvalidDataException("LZTURBO block truncated at token.");

      var token = data[pos++];
      var literalField = token >> 4;
      var matchNibble = token & 0x0F;

      var literalCount = literalField < LzturboConstants.LiteralExtended
        ? literalField
        : LzturboConstants.MaxDirectLiteral + 1 + ReadExtended(data, ref pos);

      if (pos + literalCount > data.Length || outPos + literalCount > originalLength)
        throw new InvalidDataException("LZTURBO literal run overruns buffer.");
      data.Slice(pos, literalCount).CopyTo(output.AsSpan(outPos));
      pos += literalCount;
      outPos += literalCount;

      if (matchNibble == LzturboConstants.MatchNone)
        continue;

      var matchField = matchNibble <= LzturboConstants.MaxDirectMatch
        ? matchNibble
        : LzturboConstants.MatchExtended + ReadExtended(data, ref pos);
      var matchLength = matchField + LzturboConstants.MinMatch;

      if (pos + LzturboConstants.DistanceBytes > data.Length)
        throw new InvalidDataException("LZTURBO match token truncated.");
      var distance = 0;
      for (var i = 0; i < LzturboConstants.DistanceBytes; ++i)
        distance |= data[pos + i] << (8 * i);
      pos += LzturboConstants.DistanceBytes;

      if (distance <= 0 || distance > outPos || outPos + matchLength > originalLength)
        throw new InvalidDataException("LZTURBO match references invalid distance.");

      var srcPos = outPos - distance;
      for (var i = 0; i < matchLength; ++i)
        output[outPos + i] = output[srcPos + i];
      outPos += matchLength;
    }

    return output;
  }

  private static int ReadExtended(ReadOnlySpan<byte> data, ref int pos) {
    var sum = 0;
    byte b;
    do {
      if (pos >= data.Length)
        throw new InvalidDataException("LZTURBO extended length truncated.");
      b = data[pos++];
      sum += b;
    } while (b == 255);

    return sum;
  }
}
