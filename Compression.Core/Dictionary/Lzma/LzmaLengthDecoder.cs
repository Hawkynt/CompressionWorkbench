using Compression.Core.Entropy.RangeCoding;

namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA length decoder.
/// </summary>
internal sealed class LzmaLengthDecoder {
  private const int NumLowBits = 3;
  private const int NumMidBits = 3;
  private const int NumHighBits = 8;
  private const int NumPosStatesMax = 1 << 4;

  private int _choice;
  private int _choice2;
  private readonly BitTreeDecoder[] _lowCoder;
  private readonly BitTreeDecoder[] _midCoder;
  private readonly BitTreeDecoder _highCoder;

  /// <summary>
  /// Initializes a new length decoder.
  /// </summary>
  public LzmaLengthDecoder() {
    this._choice = RangeEncoder.ProbInitValue;
    this._choice2 = RangeEncoder.ProbInitValue;
    this._lowCoder = new BitTreeDecoder[LzmaLengthDecoder.NumPosStatesMax];
    this._midCoder = new BitTreeDecoder[LzmaLengthDecoder.NumPosStatesMax];
    for (var i = 0; i < LzmaLengthDecoder.NumPosStatesMax; ++i) {
      this._lowCoder[i] = new(LzmaLengthDecoder.NumLowBits);
      this._midCoder[i] = new(LzmaLengthDecoder.NumMidBits);
    }
    this._highCoder = new(LzmaLengthDecoder.NumHighBits);
  }

  /// <summary>
  /// Resets all probabilities.
  /// </summary>
  public void Reset() {
    this._choice = RangeEncoder.ProbInitValue;
    this._choice2 = RangeEncoder.ProbInitValue;
    for (var i = 0; i < LzmaLengthDecoder.NumPosStatesMax; ++i) {
      this._lowCoder[i].Reset();
      this._midCoder[i].Reset();
    }
    this._highCoder.Reset();
  }

  /// <summary>
  /// Decodes a length value.
  /// </summary>
  /// <param name="decoder">The range decoder.</param>
  /// <param name="posState">The position state bits.</param>
  /// <returns>The decoded length (2..273).</returns>
  public int Decode(RangeDecoder decoder, int posState) {
    if (decoder.DecodeBit(ref this._choice) == 0)
      return LzmaConstants.MatchMinLen + this._lowCoder[posState].Decode(decoder);
    if (decoder.DecodeBit(ref this._choice2) == 0)
      return LzmaConstants.MatchMinLen + (1 << LzmaLengthDecoder.NumLowBits) + this._midCoder[posState].Decode(decoder);

    return LzmaConstants.MatchMinLen + (1 << LzmaLengthDecoder.NumLowBits) + (1 << LzmaLengthDecoder.NumMidBits) + this._highCoder.Decode(decoder);
  }
}
