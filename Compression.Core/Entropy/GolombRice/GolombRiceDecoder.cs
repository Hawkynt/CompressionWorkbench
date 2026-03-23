namespace Compression.Core.Entropy.GolombRice;

/// <summary>
/// Decodes values encoded with Golomb-Rice coding.
/// </summary>
public sealed class GolombRiceDecoder {
  private readonly byte[] _data;
  private int _bitPos;

  /// <summary>
  /// Gets or sets the Rice parameter k (number of remainder bits).
  /// </summary>
  public int K { get; set; }

  /// <summary>
  /// Initializes a new <see cref="GolombRiceDecoder"/>.
  /// </summary>
  /// <param name="data">The encoded data.</param>
  /// <param name="k">The Rice parameter (number of remainder bits).</param>
  public GolombRiceDecoder(byte[] data, int k = 0) {
    this._data = data;
    this.K = k;
  }

  /// <summary>
  /// Gets the current bit position in the data.
  /// </summary>
  public int BitPosition => this._bitPos;

  /// <summary>
  /// Decodes a non-negative value.
  /// </summary>
  public int Decode() {
    // Read unary: count 1-bits until 0-bit
    var q = 0;
    while (ReadBit() != 0)
      ++q;

    // Read k-bit remainder (MSB first)
    var r = 0;
    for (var i = 0; i < this.K; ++i)
      r = (r << 1) | ReadBit();

    return (q << this.K) | r;
  }

  /// <summary>
  /// Decodes a signed value using zig-zag mapping.
  /// </summary>
  public int DecodeSigned() {
    var mapped = Decode();
    return (mapped & 1) != 0 ? -((mapped + 1) >> 1) : mapped >> 1;
  }

  private int ReadBit() {
    var byteIdx = this._bitPos >> 3;
    if (byteIdx >= this._data.Length)
      return 0;
    var bitIdx = 7 - (this._bitPos & 7);
    ++this._bitPos;
    return (this._data[byteIdx] >> bitIdx) & 1;
  }
}
