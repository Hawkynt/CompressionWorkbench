namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// A match candidate. <see cref="Distance"/> 0 is a literal, 1-4 select a repeat distance, anything
/// larger is a plain match at <c>Distance - 4</c>. In the optimal parser's price table the same slot
/// holds a price instead of a length, which is why both names share one field.
/// </summary>
internal struct CscMatch {
  /// <summary>Match length (or price, see <see cref="Price"/>).</summary>
  public uint Length;

  /// <summary>Coded distance: 0 literal, 1-4 repeat index + 1, else real distance + 4.</summary>
  public uint Distance;

  /// <summary>Price in 1/128 bit; aliases <see cref="Length"/> in price tables.</summary>
  public uint Price {
    readonly get => this.Length;
    set => this.Length = value;
  }

  public CscMatch(uint length, uint distance) {
    this.Length = length;
    this.Distance = distance;
  }
}
