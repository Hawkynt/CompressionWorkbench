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
    this._lowCoder = new BitTreeEncoder[NumPosStatesMax];
    this._midCoder = new BitTreeEncoder[NumPosStatesMax];
    for (int i = 0; i < NumPosStatesMax; ++i) {
      this._lowCoder[i] = new BitTreeEncoder(NumLowBits);
      this._midCoder[i] = new BitTreeEncoder(NumMidBits);
    }
    this._highCoder = new BitTreeEncoder(NumHighBits);
  }

  /// <summary>
  /// Resets all probabilities.
  /// </summary>
  public void Reset() {
    this._choice = RangeEncoder.ProbInitValue;
    this._choice2 = RangeEncoder.ProbInitValue;
    for (int i = 0; i < NumPosStatesMax; ++i) {
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

    if (length < (1 << NumLowBits)) {
      encoder.EncodeBit(ref this._choice, 0);
      this._lowCoder[posState].Encode(encoder, length);
    }
    else if (length < (1 << NumLowBits) + (1 << NumMidBits)) {
      encoder.EncodeBit(ref this._choice, 1);
      encoder.EncodeBit(ref this._choice2, 0);
      this._midCoder[posState].Encode(encoder, length - (1 << NumLowBits));
    }
    else {
      encoder.EncodeBit(ref this._choice, 1);
      encoder.EncodeBit(ref this._choice2, 1);
      this._highCoder.Encode(encoder, length - (1 << NumLowBits) - (1 << NumMidBits));
    }
  }
}

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
    this._lowCoder = new BitTreeDecoder[NumPosStatesMax];
    this._midCoder = new BitTreeDecoder[NumPosStatesMax];
    for (int i = 0; i < NumPosStatesMax; ++i) {
      this._lowCoder[i] = new BitTreeDecoder(NumLowBits);
      this._midCoder[i] = new BitTreeDecoder(NumMidBits);
    }
    this._highCoder = new BitTreeDecoder(NumHighBits);
  }

  /// <summary>
  /// Resets all probabilities.
  /// </summary>
  public void Reset() {
    this._choice = RangeEncoder.ProbInitValue;
    this._choice2 = RangeEncoder.ProbInitValue;
    for (int i = 0; i < NumPosStatesMax; ++i) {
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
    else if (decoder.DecodeBit(ref this._choice2) == 0)
      return LzmaConstants.MatchMinLen + (1 << NumLowBits) + this._midCoder[posState].Decode(decoder);
    else
      return LzmaConstants.MatchMinLen + (1 << NumLowBits) + (1 << NumMidBits) + this._highCoder.Decode(decoder);
  }
}
