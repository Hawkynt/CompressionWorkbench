namespace Compression.Core.Entropy.ExpGolomb;

/// <summary>
/// Exponential Golomb encoder. Used in H.264/H.265 video codecs.
/// Order-k exp-Golomb maps non-negative integer n to a variable-length codeword.
/// </summary>
public sealed class ExpGolombEncoder {
  private readonly int _order;
  private byte _buffer;
  private int _bitCount;
  private readonly Stream _output;

  /// <summary>
  /// Creates an encoder with the specified order k.
  /// </summary>
  public ExpGolombEncoder(Stream output, int order = 0) {
    _output = output;
    _order = order;
  }

  /// <summary>
  /// Encodes a non-negative value using exp-Golomb coding.
  /// </summary>
  public void Encode(int value) {
    // For order-k: encode (value + 2^k - 1) as order-0, then write k suffix bits.
    var adjusted = value + (1 << _order) - 1;

    // Order-0: value n is coded as: (floor(log2(n+1))) zeros, then binary(n+1).
    var n1 = adjusted + 1;
    var bits = 0;
    var temp = n1;
    while (temp > 1) { bits++; temp >>= 1; }

    // Write 'bits' zeros.
    for (var i = 0; i < bits; i++)
      WriteBit(0);

    // Write (bits+1) bits of (n+1) MSB first.
    for (var i = bits; i >= 0; i--)
      WriteBit((n1 >> i) & 1);
  }

  /// <summary>
  /// Flushes any remaining bits in the buffer.
  /// </summary>
  public void Flush() {
    if (_bitCount > 0) {
      _buffer <<= (8 - _bitCount);
      _output.WriteByte(_buffer);
      _buffer = 0;
      _bitCount = 0;
    }
  }

  private void WriteBit(int bit) {
    _buffer = (byte)((_buffer << 1) | (bit & 1));
    _bitCount++;
    if (_bitCount == 8) {
      _output.WriteByte(_buffer);
      _buffer = 0;
      _bitCount = 0;
    }
  }
}
