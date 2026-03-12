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
    var numCoders = 1 << (lc + lp);
    this._coders = new int[numCoders][];
    for (var i = 0; i < numCoders; ++i) {
      this._coders[i] = new int[0x300]; // 3 * 256 probs
      Array.Fill(this._coders[i], RangeEncoder.ProbInitValue);
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
  /// Encodes a literal byte.
  /// </summary>
  public void Encode(RangeEncoder encoder, int state, byte curByte, byte matchByte, int position, byte prevByte) {
    var probs = this.GetSubCoder(position, prevByte);

    if (LzmaConstants.StateIsLiteral(state))
      EncodeNormal(encoder, probs, curByte);
    else
      EncodeMatched(encoder, probs, curByte, matchByte);
  }

  private int[] GetSubCoder(int position, byte prevByte) {
    var index = ((position & ((1 << this._lp) - 1)) << this._lc) + (prevByte >> (8 - this._lc));
    return this._coders[index];
  }

  private static void EncodeNormal(RangeEncoder encoder, int[] probs, byte symbol) {
    var context = 1;
    for (var i = 7; i >= 0; --i) {
      var bit = (symbol >> i) & 1;
      encoder.EncodeBit(ref probs[context], bit);
      context = (context << 1) | bit;
    }
  }

  private static void EncodeMatched(RangeEncoder encoder, int[] probs, byte symbol, byte matchByte) {
    var context = 1;
    var mismatchFound = false;

    for (var i = 7; i >= 0; --i) {
      var bit = (symbol >> i) & 1;
      var matchBit = (matchByte >> i) & 1;

      if (mismatchFound)
        encoder.EncodeBit(ref probs[context], bit);
      else {
        var offset = 0x100 + (matchBit << 8);
        encoder.EncodeBit(ref probs[offset + context], bit);
        if (bit != matchBit)
          mismatchFound = true;
      }

      context = (context << 1) | bit;
    }
  }
}
