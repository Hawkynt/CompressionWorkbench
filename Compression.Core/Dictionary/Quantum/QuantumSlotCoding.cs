namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Magnitude-slot coding for the positive integers Quantum uses for match lengths
/// and match distances.
/// </summary>
/// <remarks>
/// A value n &gt;= 1 is written as its bit length (the "slot"), entropy coded through
/// an adaptive model, followed by the slot − 1 bits below the implicit leading one bit,
/// written most-significant-bit first at a fixed 50/50 probability. Small values
/// therefore cost little and the alphabet stays small no matter how large n gets.
/// </remarks>
internal static class QuantumSlotCoding {
  /// <summary>Returns the number of bits needed to represent a value.</summary>
  /// <param name="value">A non-negative value.</param>
  /// <returns>The bit length; 0 for a value of 0.</returns>
  public static int BitLength(long value) {
    var length = 0;
    for (var remaining = value; remaining > 0; remaining /= 2)
      ++length;

    return length;
  }

  /// <summary>Encodes a positive integer as a slot plus its remainder bits.</summary>
  /// <param name="encoder">The arithmetic encoder.</param>
  /// <param name="slotModel">The adaptive model for the slot alphabet.</param>
  /// <param name="value">The value to encode; must be at least 1.</param>
  public static void Encode(QuantumRangeEncoder encoder, QuantumModel slotModel, long value) {
    var slot = BitLength(value);
    encoder.EncodeSymbol(slotModel, slot);

    var remainder = value - (1L << (slot - 1));
    for (var i = slot - 2; i >= 0; --i)
      encoder.EncodeEqualProbabilityBit((int)((remainder >> i) & 1));
  }

  /// <summary>Decodes a positive integer written by <see cref="Encode"/>.</summary>
  /// <param name="decoder">The arithmetic decoder.</param>
  /// <param name="slotModel">The adaptive model for the slot alphabet.</param>
  /// <returns>The decoded value.</returns>
  /// <exception cref="InvalidDataException">The stream named slot 0, which encodes no value.</exception>
  public static long Decode(QuantumRangeDecoder decoder, QuantumModel slotModel) {
    var slot = decoder.DecodeSymbol(slotModel);
    if (slot == 0)
      throw new InvalidDataException("Quantum stream contains a zero magnitude slot.");

    var value = 1L << (slot - 1);
    for (var i = slot - 2; i >= 0; --i)
      if (decoder.DecodeEqualProbabilityBit() != 0)
        value |= 1L << i;

    return value;
  }
}
