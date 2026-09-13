#pragma warning disable CS1591

namespace Codec.WmaPro;

/// <summary>
/// Inverse MDCT for the WMA Pro decoder, standing in for FFmpeg's
/// <c>av_tx_init(AV_TX_FLOAT_MDCT, inv, n, scale, 0)</c>. Given <c>n</c> frequency
/// coefficients it produces <c>n</c> time-domain samples — the "half" inverse MDCT the
/// reference applies per subframe — via the same definition the av_tx naive inverse
/// implements (<c>tx_template.c</c> <c>ff_tx_mdct_naive_inv</c>):
/// <code>
///   half  = n / 2
///   phase = pi / (4 * n)
///   for i in [0, half):
///     i_d = phase * (2*n - 2*i - 1)
///     i_u = phase * (3*n + 2*i + 1)
///     sum_d = Σ_j src[j] * cos((2j+1) * i_d)
///     sum_u = Σ_j src[j] * cos((2j+1) * i_u)     (j over [0, n))
///     dst[i]        =  scale * sum_d
///     dst[i + half] = -scale * sum_u
/// </code>
/// A direct O(n²) evaluation is used: WMA Pro subframes top out at 2048 points and
/// decoding is not the workbench's hot path, so a faithful, obviously-correct transform
/// is preferred over a hand-ported split-radix FFT.
/// </summary>
internal sealed class WmaProMdct {

  private readonly int _n;       // number of input coefficients == number of outputs
  private readonly int _half;
  private readonly float _scale;
  private readonly double _phase;

  public WmaProMdct(int n, float scale) {
    this._n = n;
    this._half = n / 2;
    this._scale = scale;
    this._phase = Math.PI / (4.0 * n);
  }

  /// <summary>
  /// Transforms <see cref="_n"/> input coefficients (<paramref name="input"/>) into
  /// <see cref="_n"/> output samples written to <paramref name="output"/>.
  /// </summary>
  public void Inverse(float[] input, float[] output) {
    var n = this._n;
    var half = this._half;
    for (var i = 0; i < half; ++i) {
      var iD = this._phase * (2 * n - 2 * i - 1);
      var iU = this._phase * (3 * n + 2 * i + 1);
      double sumD = 0;
      double sumU = 0;
      for (var j = 0; j < n; ++j) {
        var a = 2 * j + 1;
        var val = input[j];
        sumD += Math.Cos(a * iD) * val;
        sumU += Math.Cos(a * iU) * val;
      }
      output[i] = (float)(this._scale * sumD);
      output[i + half] = (float)(-this._scale * sumU);
    }
  }
}
