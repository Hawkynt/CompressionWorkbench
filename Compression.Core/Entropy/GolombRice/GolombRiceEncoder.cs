namespace Compression.Core.Entropy.GolombRice;

/// <summary>
/// Encodes values using Golomb-Rice coding.
/// </summary>
/// <remarks>
/// Each value v is encoded as:
/// - Quotient q = v >> k written as q unary 1-bits followed by a 0-bit
/// - Remainder r = v &amp; ((1 &lt;&lt; k) - 1) written as k binary bits
/// </remarks>
public sealed class GolombRiceEncoder {
  private readonly List<byte> _output = [];
  private int _bitPos;
  private byte _current;

  /// <summary>
  /// Gets or sets the Rice parameter k (number of remainder bits).
  /// </summary>
  public int K { get; set; }

  /// <summary>
  /// Initializes a new <see cref="GolombRiceEncoder"/> with the given Rice parameter.
  /// </summary>
  /// <param name="k">The Rice parameter (number of remainder bits).</param>
  public GolombRiceEncoder(int k = 0) {
    this.K = k;
  }

  /// <summary>
  /// Encodes a non-negative value.
  /// </summary>
  /// <param name="value">The value to encode (must be &gt;= 0).</param>
  public void Encode(int value) {
    int q = value >> this.K;
    int r = value & ((1 << this.K) - 1);

    // Write q unary 1-bits
    for (int i = 0; i < q; ++i)
      WriteBit(1);
    // Write terminating 0-bit
    WriteBit(0);

    // Write k-bit remainder (MSB first)
    for (int i = this.K - 1; i >= 0; --i)
      WriteBit((r >> i) & 1);
  }

  /// <summary>
  /// Encodes a signed value using zig-zag mapping: 0→0, -1→1, 1→2, -2→3, ...
  /// </summary>
  /// <param name="value">The signed value.</param>
  public void EncodeSigned(int value) {
    int mapped = value >= 0 ? value * 2 : (-value) * 2 - 1;
    Encode(mapped);
  }

  /// <summary>
  /// Returns the encoded data and resets the encoder.
  /// </summary>
  public byte[] ToArray() {
    if (this._bitPos > 0)
      this._output.Add(this._current);
    byte[] result = [.. this._output];
    this._output.Clear();
    this._bitPos = 0;
    this._current = 0;
    return result;
  }

  private void WriteBit(int bit) {
    if (bit != 0)
      this._current |= (byte)(1 << (7 - this._bitPos));
    if (++this._bitPos == 8) {
      this._output.Add(this._current);
      this._current = 0;
      this._bitPos = 0;
    }
  }
}
