using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lzvn;

/// <summary>
/// Decompresses data produced by <see cref="LzvnCompressor"/>.
/// </summary>
public static class LzvnDecompressor {
  /// <summary>
  /// Decompresses LZVN-compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data, prefixed with a 4-byte little-endian original length.</param>
  /// <returns>The original decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">The compressed stream is malformed or truncated.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    if (compressed.Length < 4)
      throw new InvalidDataException("LZVN stream too short for header.");

    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    var output = new byte[originalLength];
    if (originalLength == 0)
      return output;

    var data = compressed[4..];
    var pos = 0;
    var outPos = 0;

    while (outPos < originalLength) {
      if (pos >= data.Length)
        throw new InvalidDataException("LZVN stream truncated at token.");

      var token = data[pos++];
      var literalField = token >> 4;
      var matchNibble = token & 0x0F;

      var literalCount = literalField < LzvnConstants.LiteralExtended
        ? literalField
        : LzvnConstants.MaxDirectLiteral + 1 + ReadExtended(data, ref pos);

      if (pos + literalCount > data.Length || outPos + literalCount > originalLength)
        throw new InvalidDataException("LZVN literal run overruns buffer.");
      data.Slice(pos, literalCount).CopyTo(output.AsSpan(outPos));
      pos += literalCount;
      outPos += literalCount;

      if (matchNibble == LzvnConstants.MatchNone)
        continue;

      var matchField = matchNibble <= LzvnConstants.MaxDirectMatch
        ? matchNibble
        : LzvnConstants.MatchExtended + ReadExtended(data, ref pos);
      var matchLength = matchField + LzvnConstants.MinMatch;

      var distance = ReadDistance(data, ref pos);
      if (distance <= 0 || distance > outPos || outPos + matchLength > originalLength)
        throw new InvalidDataException("LZVN match references invalid distance.");

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
        throw new InvalidDataException("LZVN extended length truncated.");
      b = data[pos++];
      sum += b;
    } while (b == 255);

    return sum;
  }

  private static int ReadDistance(ReadOnlySpan<byte> data, ref int pos) {
    if (pos >= data.Length)
      throw new InvalidDataException("LZVN distance truncated.");

    var b0 = data[pos++];
    if (b0 < 0x80)
      return b0 + 1;

    if (b0 != LzvnConstants.DistanceTier3Marker) {
      if (pos >= data.Length)
        throw new InvalidDataException("LZVN distance truncated.");
      var b1 = data[pos++];
      var hi = b0 - 0x80;
      return LzvnConstants.DistanceTier1Max + 1 + (hi << 8) + b1;
    }

    if (pos + 4 > data.Length)
      throw new InvalidDataException("LZVN distance truncated.");
    var distance = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
    pos += 4;
    return distance;
  }
}
