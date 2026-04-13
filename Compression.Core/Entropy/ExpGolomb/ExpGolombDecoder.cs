namespace Compression.Core.Entropy.ExpGolomb;

/// <summary>
/// Exponential Golomb decoder. Reads exp-Golomb coded values from a bitstream.
/// </summary>
public sealed class ExpGolombDecoder {
  private readonly int _order;
  private readonly byte[] _data;
  private int _bitPos;

  /// <summary>
  /// Creates a decoder with the specified order k.
  /// </summary>
  public ExpGolombDecoder(byte[] data, int order = 0) {
    _data = data;
    _order = order;
  }

  /// <summary>
  /// Decodes one exp-Golomb coded value.
  /// </summary>
  public int Decode() {
    // Count leading zeros.
    var zeros = 0;
    while (ReadBit() == 0)
      zeros++;

    // Read (zeros + 1) bits for the value.
    var value = 1; // The leading 1 already read above.
    for (var i = 0; i < zeros; i++)
      value = (value << 1) | ReadBit();

    var adjusted = value - 1;

    // For order-k: result = adjusted - (2^k - 1).
    return adjusted - ((1 << _order) - 1);
  }

  /// <summary>
  /// Returns true if there are at least 8 bits remaining to read.
  /// </summary>
  public bool HasData => _bitPos / 8 < _data.Length;

  private int ReadBit() {
    if (_bitPos / 8 >= _data.Length)
      throw new InvalidDataException("Unexpected end of exp-Golomb bitstream.");
    var bit = (_data[_bitPos / 8] >> (7 - (_bitPos % 8))) & 1;
    _bitPos++;
    return bit;
  }
}
