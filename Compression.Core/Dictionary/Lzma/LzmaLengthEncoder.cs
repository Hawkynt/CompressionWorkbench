using Compression.Core.Entropy.RangeCoding;

namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA length encoder. Encodes lengths in three ranges:
/// 2..9 (low, 3-bit), 10..17 (mid, 3-bit), 18..273 (high, 8-bit).
/// </summary>
internal sealed class LzmaLengthEncoder {
  private const int NumLowBits = 3;
  private const int NumMidBits = 3;
  private const int NumHighBits = 8;
  private const int NumPosStatesMax = 1 << 4; // max pb = 4

  private int _choice;
  private int _choice2;
  private readonly BitTreeEncoder[] _lowCoder;
  private readonly BitTreeEncoder[] _midCoder;
  private readonly BitTreeEncoder _highCoder;

  /// <summary>
  /// Initializes a new length encoder.
  /// </summary>
  public LzmaLengthEncoder() {
    this._choice = RangeEncoder.ProbInitValue;
    this._choice2 = RangeEncoder.ProbInitValue;
    this._lowCoder = new BitTreeEncoder[LzmaLengthEncoder.NumPosStatesMax];
    this._midCoder = new BitTreeEncoder[LzmaLengthEncoder.NumPosStatesMax];
    for (var i = 0; i < LzmaLengthEncoder.NumPosStatesMax; ++i) {
      this._lowCoder[i] = new(LzmaLengthEncoder.NumLowBits);
      this._midCoder[i] = new(LzmaLengthEncoder.NumMidBits);
    }
    this._highCoder = new(LzmaLengthEncoder.NumHighBits);
  }

  /// <summary>
  /// Resets all probabilities.
  /// </summary>
  public void Reset() {
    this._choice = RangeEncoder.ProbInitValue;
    this._choice2 = RangeEncoder.ProbInitValue;
    for (var i = 0; i < LzmaLengthEncoder.NumPosStatesMax; ++i) {
      this._lowCoder[i].Reset();
      this._midCoder[i].Reset();
    }
    this._highCoder.Reset();
  }

  /// <summary>
  /// Encodes a length value.
  /// </summary>
  /// <param name="encoder">The range encoder.</param>
  /// <param name="length">The length to encode (2..273).</param>
  /// <param name="posState">The position state bits.</param>
  public void Encode(RangeEncoder encoder, int length, int posState) {
    length -= LzmaConstants.MatchMinLen;

    switch (length) {
      case < 1 << LzmaLengthEncoder.NumLowBits:
        encoder.EncodeBit(ref this._choice, 0);
        this._lowCoder[posState].Encode(encoder, length);
        break;
      case < (1 << LzmaLengthEncoder.NumLowBits) + (1 << LzmaLengthEncoder.NumMidBits):
        encoder.EncodeBit(ref this._choice, 1);
        encoder.EncodeBit(ref this._choice2, 0);
        this._midCoder[posState].Encode(encoder, length - (1 << LzmaLengthEncoder.NumLowBits));
        break;
      default:
        encoder.EncodeBit(ref this._choice, 1);
        encoder.EncodeBit(ref this._choice2, 1);
        this._highCoder.Encode(encoder, length - (1 << LzmaLengthEncoder.NumLowBits) - (1 << LzmaLengthEncoder.NumMidBits));
        break;
    }
  }
}
