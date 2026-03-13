using Compression.Core.Entropy.RangeCoding;

namespace Compression.Core.Dictionary.Lzma;

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
    var numCoders = 1 << (lc + lp);
    this._coders = new int[numCoders][];
    for (var i = 0; i < numCoders; ++i) {
      this._coders[i] = new int[0x300];
      this._coders[i].AsSpan().Fill(RangeEncoder.ProbInitValue);
    }
  }

  /// <summary>
  /// Resets all probabilities to the initial value.
  /// </summary>
  public void Reset() {
    foreach (var coder in this._coders)
      coder.AsSpan().Fill(RangeEncoder.ProbInitValue);
  }

  /// <summary>
  /// Decodes a literal byte.
  /// </summary>
  public byte Decode(RangeDecoder decoder, int state, byte matchByte, int position, byte prevByte) {
    var probs = this.GetSubCoder(position, prevByte);
    return LzmaConstants.StateIsLiteral(state) ? DecodeNormal(decoder, probs) : DecodeMatched(decoder, probs, matchByte);
  }

  private int[] GetSubCoder(int position, byte prevByte) {
    var index = ((position & ((1 << this._lp) - 1)) << this._lc) + (prevByte >> (8 - this._lc));
    return this._coders[index];
  }

  private static byte DecodeNormal(RangeDecoder decoder, int[] probs) {
    var context = 1;
    for (var i = 0; i < 8; ++i)
      context = (context << 1) | decoder.DecodeBit(ref probs[context]);

    return (byte)(context - 0x100);
  }

  private static byte DecodeMatched(RangeDecoder decoder, int[] probs, byte matchByte) {
    var context = 1;
    var mismatchFound = false;

    for (var i = 7; i >= 0; --i) {
      var matchBit = (matchByte >> i) & 1;
      int bit;

      if (mismatchFound)
        bit = decoder.DecodeBit(ref probs[context]);
      else {
        var offset = 0x100 + (matchBit << 8);
        bit = decoder.DecodeBit(ref probs[offset + context]);
        if (bit != matchBit)
          mismatchFound = true;
      }

      context = (context << 1) | bit;
    }

    return (byte)(context - 0x100);
  }
}
