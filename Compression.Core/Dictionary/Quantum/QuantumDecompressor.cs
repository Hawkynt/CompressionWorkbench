namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Decompresses data produced by <see cref="QuantumCompressor"/>.
/// </summary>
/// <remarks>
/// The stream carries no end marker, so the caller supplies the uncompressed size and
/// decoding stops once that many bytes have been produced.
/// </remarks>
public static class QuantumDecompressor {
  /// <summary>
  /// Decompresses a single Quantum-compressed block.
  /// </summary>
  /// <param name="compressed">The compressed input data, without any length header.</param>
  /// <param name="uncompressedSize">The expected uncompressed output size in bytes.</param>
  /// <param name="windowLevel">
  /// Window level (1–7); must match the level used to compress. The window size is
  /// 1024 &lt;&lt; (level − 1).
  /// </param>
  /// <param name="modelMaxTotal">
  /// The total frequency at which the adaptive models halve their counts; must match
  /// the value used to compress.
  /// </param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="windowLevel"/> is outside the valid range [1, 7],
  /// or when <paramref name="uncompressedSize"/> is negative.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the compressed data is malformed.
  /// </exception>
  public static byte[] Decompress(
    ReadOnlyMemory<byte> compressed,
    int uncompressedSize,
    int windowLevel,
    int modelMaxTotal = QuantumConstants.ModelMaxTotal) {
    ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);
    ArgumentOutOfRangeException.ThrowIfLessThan(windowLevel, QuantumConstants.MinWindowLevel, nameof(windowLevel));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(windowLevel, QuantumConstants.MaxWindowLevel, nameof(windowLevel));

    if (uncompressedSize == 0)
      return [];

    var decoder = new QuantumRangeDecoder(compressed);

    var literalModels = new QuantumModel[QuantumConstants.StateCount];
    var matchFlagModels = new QuantumModel[QuantumConstants.StateCount];
    for (var state = 0; state < QuantumConstants.StateCount; ++state) {
      literalModels[state] = new QuantumModel(QuantumConstants.LiteralSymbols, modelMaxTotal);
      matchFlagModels[state] = new QuantumModel(2, modelMaxTotal);
    }

    var lengthSlotModel = new QuantumModel(QuantumConstants.SlotSymbols, modelMaxTotal);
    var distanceSlotModel = new QuantumModel(QuantumConstants.SlotSymbols, modelMaxTotal);

    var output = new byte[uncompressedSize];
    var produced = 0;
    var currentState = 0;

    while (produced < uncompressedSize) {
      if (decoder.DecodeSymbol(matchFlagModels[currentState]) == 0) {
        output[produced++] = (byte)decoder.DecodeSymbol(literalModels[currentState]);
        currentState = QuantumConstants.LiteralNextState[currentState];
        continue;
      }

      var matchLength = QuantumSlotCoding.Decode(decoder, lengthSlotModel) + QuantumConstants.MinMatch - 1;
      var distance = QuantumSlotCoding.Decode(decoder, distanceSlotModel);

      if (distance > produced)
        throw new InvalidDataException(
          $"Quantum match distance {distance} exceeds the {produced} bytes decoded so far.");
      if (matchLength > uncompressedSize - produced)
        throw new InvalidDataException(
          $"Quantum match of {matchLength} bytes overruns the declared size {uncompressedSize}.");

      // Byte by byte, because a match may overlap itself — that is how runs are coded.
      for (var source = produced - (int)distance; matchLength > 0; --matchLength)
        output[produced++] = output[source++];

      currentState = QuantumConstants.MatchNextState[currentState];
    }

    return output;
  }
}
