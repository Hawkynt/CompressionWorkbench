namespace Compression.Core.Entropy.RangeCoding;

/// <summary>
/// Binary tree encoder using range coding. Encodes N-bit values
/// using a tree of adaptive probability variables.
/// </summary>
public sealed class BitTreeEncoder {
  private readonly int _numBits;

  /// <summary>
  /// Gets the probability array. Size is <c>1 &lt;&lt; numBits</c>.
  /// </summary>
  public int[] Probs { get; }

  /// <summary>
  /// Initializes a new <see cref="BitTreeEncoder"/> for the specified number of bits.
  /// </summary>
  /// <param name="numBits">The number of bits per symbol (e.g., 8 for a byte).</param>
  public BitTreeEncoder(int numBits) {
    this._numBits = numBits;
    this.Probs = new int[1 << numBits];
    this.Reset();
  }

  /// <summary>
  /// Resets all probabilities to the midpoint.
  /// </summary>
  public void Reset() => this.Probs.AsSpan().Fill(RangeEncoder.ProbInitValue);

  /// <summary>
  /// Encodes a value MSB-first using the bit tree.
  /// </summary>
  /// <param name="encoder">The range encoder.</param>
  /// <param name="value">The value to encode (0 to 2^numBits - 1).</param>
  public void Encode(RangeEncoder encoder, int value) {
    var index = 1;
    for (var bitIndex = this._numBits - 1; bitIndex >= 0; --bitIndex) {
      var bit = (value >> bitIndex) & 1;
      encoder.EncodeBit(ref this.Probs[index], bit);
      index = (index << 1) | bit;
    }
  }

  /// <summary>
  /// Encodes a value LSB-first (reverse) using the bit tree.
  /// Used for distance extra bits in LZMA.
  /// </summary>
  /// <param name="encoder">The range encoder.</param>
  /// <param name="value">The value to encode.</param>
  public void ReverseEncode(RangeEncoder encoder, int value) {
    var index = 1;
    for (var i = 0; i < this._numBits; ++i) {
      var bit = value & 1;
      encoder.EncodeBit(ref this.Probs[index], bit);
      index = (index << 1) | bit;
      value >>= 1;
    }
  }

  /// <summary>
  /// Reverse-encodes a value into the given probability array.
  /// Static helper for shared prob arrays (e.g., LZMA distance special positions).
  /// </summary>
  /// <param name="encoder">The range encoder.</param>
  /// <param name="probs">The probability array.</param>
  /// <param name="startIndex">The starting index in the prob array.</param>
  /// <param name="numBits">The number of bits to encode.</param>
  /// <param name="value">The value to encode.</param>
  public static void ReverseEncode(RangeEncoder encoder, Span<int> probs, int startIndex, int numBits, int value) {
    var index = 1;
    for (var i = 0; i < numBits; ++i) {
      var bit = value & 1;
      encoder.EncodeBit(ref probs[startIndex + index], bit);
      index = (index << 1) | bit;
      value >>= 1;
    }
  }
}
