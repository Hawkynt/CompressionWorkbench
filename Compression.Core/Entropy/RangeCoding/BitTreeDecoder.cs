namespace Compression.Core.Entropy.RangeCoding;

/// <summary>
/// Binary tree decoder using range coding. Decodes N-bit values
/// using a tree of adaptive probability variables.
/// </summary>
public sealed class BitTreeDecoder {
  private readonly int _numBits;

  /// <summary>
  /// Gets the probability array. Size is <c>1 &lt;&lt; numBits</c>.
  /// </summary>
  public int[] Probs { get; }

  /// <summary>
  /// Initializes a new <see cref="BitTreeDecoder"/> for the specified number of bits.
  /// </summary>
  /// <param name="numBits">The number of bits per symbol.</param>
  public BitTreeDecoder(int numBits) {
    this._numBits = numBits;
    this.Probs = new int[1 << numBits];
    this.Reset();
  }

  /// <summary>
  /// Resets all probabilities to the midpoint.
  /// </summary>
  public void Reset() => this.Probs.AsSpan().Fill(RangeEncoder.ProbInitValue);

  /// <summary>
  /// Decodes a value MSB-first using the bit tree.
  /// </summary>
  /// <param name="decoder">The range decoder.</param>
  /// <returns>The decoded value (0 to 2^numBits - 1).</returns>
  public int Decode(RangeDecoder decoder) {
    var index = 1;
    for (var i = 0; i < this._numBits; ++i)
      index = (index << 1) | decoder.DecodeBit(ref this.Probs[index]);

    return index - (1 << this._numBits);
  }

  /// <summary>
  /// Decodes a value LSB-first (reverse) using the bit tree.
  /// </summary>
  /// <param name="decoder">The range decoder.</param>
  /// <returns>The decoded value.</returns>
  public int ReverseDecode(RangeDecoder decoder) {
    var index = 1;
    var result = 0;
    for (var i = 0; i < this._numBits; ++i) {
      var bit = decoder.DecodeBit(ref this.Probs[index]);
      index = (index << 1) | bit;
      result |= bit << i;
    }

    return result;
  }

  /// <summary>
  /// Reverse-decodes a value from the given probability array.
  /// Static helper for shared prob arrays.
  /// </summary>
  /// <param name="decoder">The range decoder.</param>
  /// <param name="probs">The probability array.</param>
  /// <param name="startIndex">The starting index in the prob array.</param>
  /// <param name="numBits">The number of bits to decode.</param>
  /// <returns>The decoded value.</returns>
  public static int ReverseDecode(RangeDecoder decoder, Span<int> probs, int startIndex, int numBits) {
    var index = 1;
    var result = 0;
    for (var i = 0; i < numBits; ++i) {
      var bit = decoder.DecodeBit(ref probs[startIndex + index]);
      index = (index << 1) | bit;
      result |= bit << i;
    }

    return result;
  }
}
