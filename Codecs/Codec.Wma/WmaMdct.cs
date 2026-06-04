#pragma warning disable CS1591

namespace Codec.Wma;

/// <summary>
/// Inverse MDCT for the WMA decoder, standing in for FFmpeg's
/// <c>av_tx_init(AV_TX_FLOAT_MDCT, inv, n, scale, AV_TX_FULL_IMDCT)</c>. Given
/// <c>n</c> frequency coefficients it produces <c>2*n</c> time-domain samples via the
/// classic full inverse MDCT
/// <c>x[p] = scale * Σ_k X[k] · cos( (π / (2n)) · (2p + 1 + n) · (2k + 1) )</c>,
/// the same definition the reference decoder's transform satisfies. A direct
/// O(n²) evaluation is used: WMA blocks top out at 2048 points and decoding is not
/// the workbench's hot path, so a faithful, obviously-correct transform is preferred
/// over a hand-ported split-radix FFT.
/// </summary>
internal sealed class WmaMdct {

  private readonly int _n;        // number of input coefficients
  private readonly float _scale;
  private readonly float[] _cos;  // precomputed cos[(2p+1+n)*(2k+1)] folded over period

  public WmaMdct(int n, float scale) {
    this._n = n;
    this._scale = scale;
    // cos has period 4n in the integer argument m = (2p+1+n)*(2k+1); fold to [0,4n).
    var period = 4 * n;
    this._cos = new float[period];
    var w = Math.PI / (2.0 * n);
    for (var m = 0; m < period; ++m)
      this._cos[m] = (float)Math.Cos(w * m);
  }

  /// <summary>
  /// Transforms the configured number of input coefficients (<paramref name="input"/>)
  /// into <c>2*n</c> output samples written to <paramref name="output"/>.
  /// </summary>
  public void Inverse(float[] input, float[] output) {
    var n = this._n;
    var period = 4 * n;
    var two = 2 * n;
    for (var p = 0; p < two; ++p) {
      var baseArg = 2 * p + 1 + n;
      double sum = 0;
      for (var k = 0; k < n; ++k) {
        var m = (baseArg * (2 * k + 1)) % period;
        sum += input[k] * this._cos[m];
      }
      output[p] = (float)(sum * this._scale);
    }
  }
}
