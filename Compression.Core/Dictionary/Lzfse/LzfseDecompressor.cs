namespace Compression.Core.Dictionary.Lzfse;

/// <summary>
/// Decompresses data produced by <see cref="LzfseCompressor"/>.
/// </summary>
public static class LzfseDecompressor {
  /// <summary>
  /// Decompresses an LZFSE-inspired block.
  /// </summary>
  /// <param name="compressed">The compressed block.</param>
  /// <returns>The original decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">The block is malformed or truncated.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    var pos = 0;
    var originalLength = LzfseValueStream.ReadInt(compressed, ref pos);
    var output = new byte[originalLength];
    if (originalLength == 0)
      return output;

    var matchCount = LzfseValueStream.ReadInt(compressed, ref pos);
    var literalTotal = LzfseValueStream.ReadInt(compressed, ref pos);

    if (matchCount < 0 || literalTotal < 0)
      throw new InvalidDataException("LZFSE stream has a negative count.");

    var literalLengths = LzfseValueStream.ReadValues(compressed, ref pos, matchCount + 1);
    var matchLengths = LzfseValueStream.ReadValues(compressed, ref pos, matchCount);
    var distances = LzfseValueStream.ReadValues(compressed, ref pos, matchCount);

    var literalBlock = LzfseValueStream.ReadBlock(compressed, ref pos);
    var literalBytes = FseByteCodec.Decode(literalBlock, literalTotal);
    if (literalBytes.Length != literalTotal)
      throw new InvalidDataException("LZFSE literal stream length mismatch.");

    var outPos = 0;
    var litPos = 0;

    for (var i = 0; i < matchCount; ++i) {
      var literalRun = literalLengths[i];
      if (literalRun < 0 || litPos + literalRun > literalBytes.Length || outPos + literalRun > originalLength)
        throw new InvalidDataException("LZFSE literal run is out of range.");
      Array.Copy(literalBytes, litPos, output, outPos, literalRun);
      litPos += literalRun;
      outPos += literalRun;

      var matchLength = matchLengths[i] + LzfseConstants.MinMatch;
      var distance = distances[i];
      if (distance <= 0 || distance > outPos || outPos + matchLength > originalLength)
        throw new InvalidDataException("LZFSE match references an invalid distance.");

      var srcPos = outPos - distance;
      for (var j = 0; j < matchLength; ++j)
        output[outPos + j] = output[srcPos + j];
      outPos += matchLength;
    }

    var trailingLiteralRun = literalLengths[matchCount];
    if (trailingLiteralRun < 0 || litPos + trailingLiteralRun > literalBytes.Length || outPos + trailingLiteralRun > originalLength)
      throw new InvalidDataException("LZFSE trailing literal run is out of range.");
    Array.Copy(literalBytes, litPos, output, outPos, trailingLiteralRun);
    outPos += trailingLiteralRun;

    if (outPos != originalLength)
      throw new InvalidDataException("LZFSE stream did not reconstruct the expected length.");

    return output;
  }
}
