#pragma warning disable CS1591

namespace Codec.Tta;

/// <summary>
/// TTA's order-8 adaptive hybrid predictor, a faithful port of ffmpeg's
/// <c>tta_filter_process_c</c> (<c>libavcodec/ttadsp.c</c>). Eight weighted
/// history terms predict the next value; after each step every weight is nudged
/// by <c>±dx</c> according to the sign of the previous residual, a sign-sign LMS
/// adaptation. <see cref="Decode"/> is the verbatim decoder path
/// (<c>out = residual + (pred &gt;&gt; shift)</c>); <see cref="Encode"/> is its
/// exact inverse (<c>residual = sample − (pred &gt;&gt; shift)</c>) sharing the
/// identical weight and history updates, so encoder and decoder stay bit-locked.
/// <para>
/// <c>shift</c> per depth follows ffmpeg's <c>ff_tta_filter_configs</c>
/// <c>{10, 9, 10, 12}</c>, indexed by <c>(bps + 7) / 8 − 1</c>
/// (8-bit → 10, 16-bit → 9, 24-bit → 10, 32-bit → 12); <c>round = 1 &lt;&lt; (shift − 1)</c>.
/// </para>
/// </summary>
internal sealed class TtaFilter {
  private const int Order = 8;

  private readonly int _shift;
  private readonly int _round;
  private readonly int[] _qm = new int[Order];
  private readonly int[] _dx = new int[Order];
  private readonly int[] _dl = new int[Order];
  private int _error;

  public TtaFilter(int shift) {
    this._shift = shift;
    this._round = 1 << (shift - 1);
  }

  /// <summary>The filter shift for a given bit depth (ffmpeg <c>ff_tta_filter_configs</c>).</summary>
  public static int ShiftForBitsPerSample(int bitsPerSample) => ((bitsPerSample + 7) / 8) switch {
    1 => 10,
    2 => 9,
    3 => 10,
    4 => 12,
    _ => throw new NotSupportedException($"Unsupported TTA bit depth: {bitsPerSample}."),
  };

  /// <summary>Inverse (decode): residual in, reconstructed sample out.</summary>
  public int Decode(int residual) {
    var prediction = this.Predict();
    var sample = unchecked(residual + (prediction >> this._shift));
    this._error = residual;
    this.UpdateHistory(sample);
    return sample;
  }

  /// <summary>Forward (encode): sample in, residual out (exact inverse of <see cref="Decode"/>).</summary>
  public int Encode(int sample) {
    var prediction = this.Predict();
    var residual = unchecked(sample - (prediction >> this._shift));
    this._error = residual;
    this.UpdateHistory(sample);
    return residual;
  }

  // Adapt the weights from the previous residual, accumulate the prediction,
  // and slide the dx/dl windows up by one — exactly the pre-output half of
  // tta_filter_process_c.
  private int Predict() {
    if (this._error < 0) {
      for (var i = 0; i < Order; ++i) this._qm[i] = unchecked(this._qm[i] - this._dx[i]);
    } else if (this._error > 0) {
      for (var i = 0; i < Order; ++i) this._qm[i] = unchecked(this._qm[i] + this._dx[i]);
    }

    long round = this._round;
    for (var i = 0; i < Order; ++i)
      round += unchecked((long)this._dl[i] * (uint)this._qm[i]);

    this._dx[0] = this._dx[1]; this._dx[1] = this._dx[2]; this._dx[2] = this._dx[3]; this._dx[3] = this._dx[4];
    this._dl[0] = this._dl[1]; this._dl[1] = this._dl[2]; this._dl[2] = this._dl[3]; this._dl[3] = this._dl[4];

    this._dx[4] = (this._dl[4] >> 30) | 1;
    this._dx[5] = ((this._dl[5] >> 30) | 2) & ~1;
    this._dx[6] = ((this._dl[6] >> 30) | 2) & ~1;
    this._dx[7] = ((this._dl[7] >> 30) | 4) & ~3;

    return unchecked((int)round);
  }

  // Re-seed the dl history from the freshly (de)coded sample — the post-output
  // half of tta_filter_process_c.
  private void UpdateHistory(int sample) {
    unchecked {
      this._dl[4] = -this._dl[5];
      this._dl[5] = -this._dl[6];
      this._dl[6] = sample - this._dl[7];
      this._dl[7] = sample;
      this._dl[5] += this._dl[6];
      this._dl[4] += this._dl[5];
    }
  }
}
