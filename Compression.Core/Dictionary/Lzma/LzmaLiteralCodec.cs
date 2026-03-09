using Compression.Core.Entropy.RangeCoding;

namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA literal encoder using sub-coders indexed by (position, previous byte).
/// </summary>
internal sealed class LzmaLiteralEncoder {
  private readonly int _lc;
  private readonly int _lp;
  private readonly int[][] _coders;

  /// <summary>
  /// Initializes a new literal encoder.
  /// </summary>
  /// <param name="lc">Number of literal context bits (0-8).</param>
  /// <param name="lp">Number of literal position bits (0-4).</param>
  public LzmaLiteralEncoder(int lc, int lp) {
    this._lc = lc;
    this._lp = lp;
    int numCoders = 1 << (lc + lp);
    _coders = new int[numCoders][];
    for (int i = 0; i < numCoders; ++i) {
      _coders[i] = new int[0x300]; // 3 * 256 probs
      Array.Fill(_coders[i], RangeEncoder.ProbInitValue);
    }
  }

  /// <summary>
  /// Resets all probabilities to the initial value.
  /// </summary>
  public void Reset() {
    for (int i = 0; i < _coders.Length; ++i)
      Array.Fill(_coders[i], RangeEncoder.ProbInitValue);
  }

  /// <summary>
  /// Encodes a literal byte.
  /// </summary>
  public void Encode(RangeEncoder encoder, int state, byte curByte, byte matchByte, int position, byte prevByte) {
    int[] probs = GetSubCoder(position, prevByte);

    if (LzmaConstants.StateIsLiteral(state))
      EncodeNormal(encoder, probs, curByte);
    else
      EncodeMatched(encoder, probs, curByte, matchByte);
  }

  private int[] GetSubCoder(int position, byte prevByte) {
    int index = ((position & ((1 << this._lp) - 1)) << this._lc) + (prevByte >> (8 - this._lc));
    return _coders[index];
  }

  private static void EncodeNormal(RangeEncoder encoder, int[] probs, byte symbol) {
    int context = 1;
    for (int i = 7; i >= 0; --i) {
      int bit = (symbol >> i) & 1;
      encoder.EncodeBit(ref probs[context], bit);
      context = (context << 1) | bit;
    }
  }

  private static void EncodeMatched(RangeEncoder encoder, int[] probs, byte symbol, byte matchByte) {
    int context = 1;
    bool mismatchFound = false;

    for (int i = 7; i >= 0; --i) {
      int bit = (symbol >> i) & 1;
      int matchBit = (matchByte >> i) & 1;

      if (mismatchFound)
        encoder.EncodeBit(ref probs[context], bit);
      else {
        int offset = 0x100 + (matchBit << 8);
        encoder.EncodeBit(ref probs[offset + context], bit);
        if (bit != matchBit)
          mismatchFound = true;
      }

      context = (context << 1) | bit;
    }
  }
}

/// <summary>
/// LZMA literal decoder using sub-coders indexed by (position, previous byte).
/// </summary>
internal sealed class LzmaLiteralDecoder {
  private readonly int _lc;
  private readonly int _lp;
  private readonly int[][] _coders;

  /// <summary>
  /// Initializes a new literal decoder.
  /// </summary>
  /// <param name="lc">Number of literal context bits (0-8).</param>
  /// <param name="lp">Number of literal position bits (0-4).</param>
  public LzmaLiteralDecoder(int lc, int lp) {
    this._lc = lc;
    this._lp = lp;
    int numCoders = 1 << (lc + lp);
    _coders = new int[numCoders][];
    for (int i = 0; i < numCoders; ++i) {
      _coders[i] = new int[0x300];
      Array.Fill(_coders[i], RangeEncoder.ProbInitValue);
    }
  }

  /// <summary>
  /// Resets all probabilities to the initial value.
  /// </summary>
  public void Reset() {
    for (int i = 0; i < _coders.Length; ++i)
      Array.Fill(_coders[i], RangeEncoder.ProbInitValue);
  }

  /// <summary>
  /// Decodes a literal byte.
  /// </summary>
  public byte Decode(RangeDecoder decoder, int state, byte matchByte, int position, byte prevByte) {
    int[] probs = GetSubCoder(position, prevByte);

    if (LzmaConstants.StateIsLiteral(state))
      return DecodeNormal(decoder, probs);
    else
      return DecodeMatched(decoder, probs, matchByte);
  }

  private int[] GetSubCoder(int position, byte prevByte) {
    int index = ((position & ((1 << this._lp) - 1)) << this._lc) + (prevByte >> (8 - this._lc));
    return _coders[index];
  }

  private static byte DecodeNormal(RangeDecoder decoder, int[] probs) {
    int context = 1;
    for (int i = 0; i < 8; ++i)
      context = (context << 1) | decoder.DecodeBit(ref probs[context]);

    return (byte)(context - 0x100);
  }

  private static byte DecodeMatched(RangeDecoder decoder, int[] probs, byte matchByte) {
    int context = 1;
    bool mismatchFound = false;

    for (int i = 7; i >= 0; --i) {
      int matchBit = (matchByte >> i) & 1;
      int bit;

      if (mismatchFound)
        bit = decoder.DecodeBit(ref probs[context]);
      else {
        int offset = 0x100 + (matchBit << 8);
        bit = decoder.DecodeBit(ref probs[offset + context]);
        if (bit != matchBit)
          mismatchFound = true;
      }

      context = (context << 1) | bit;
    }

    return (byte)(context - 0x100);
  }
}
