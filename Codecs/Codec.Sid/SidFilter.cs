#pragma warning disable CS1591
namespace Codec.Sid;

/// <summary>
/// The SID multimode filter: a state-variable 2-pole filter producing low-pass, band-pass
/// and high-pass outputs that are summed according to the mode bits ($D418 high nibble and
/// the FC/RES registers). The 11-bit cutoff register maps to a corner frequency through a
/// model-specific curve:
/// <list type="bullet">
///   <item><b>6581</b>: a nonlinear, S-shaped curve with a DC offset — approximated here
///     with a base offset plus a tanh-shaped mapping (constants documented inline). This is
///     an approximation of the documented 6581 curve, not a per-chip measurement.</item>
///   <item><b>8580</b>: a near-linear curve.</item>
/// </list>
/// Resonance maps the register's high nibble to a Q value (model-dependent range).
/// </summary>
public sealed class SidFilter {

  private readonly SidModel _model;
  private readonly double _clockHz;

  private double _lp;
  private double _bp;

  private double _cutoff;     // normalized filter coefficient
  private double _resonance;  // 1/Q damping factor

  private bool _lowPass;
  private bool _bandPass;
  private bool _highPass;

  /// <summary>
  /// Initializes a new instance of <see cref="SidFilter"/>.
  /// </summary>
public SidFilter(SidModel model, double clockHz) {
    this._model = model;
    this._clockHz = clockHz;
    this.SetCutoff(0);
    this.SetResonance(0);
  }

  /// <summary>Sets the filter cutoff from the 11-bit FC register value (0..2047).</summary>
  public void SetCutoff(int fc11) {
    fc11 &= 0x7FF;
    var freqHz = this._model == SidModel.Mos6581
      ? Cutoff6581(fc11)
      : Cutoff8580(fc11);

    // Map a corner frequency to the state-variable coefficient (2*sin(pi*f/fs)), stepping
    // the filter once per SID clock. Clamp to keep the SVF stable.
    var fs = this._clockHz;
    var coeff = 2.0 * Math.Sin(Math.PI * Math.Min(freqHz, fs * 0.49) / fs);
    this._cutoff = Math.Clamp(coeff, 0.0, 1.0);
  }

  /// <summary>
  /// 6581 cutoff curve: a base offset (~30 Hz at FC=0) plus an S-shaped rise toward ~12 kHz,
  /// approximated with a tanh-warped mapping. The 6581 famously never reaches the full
  /// nominal range and bows in the middle; the warp constant reproduces that bow.
  /// </summary>
  private static double Cutoff6581(int fc11) {
    const double baseHz = 30.0;
    const double maxHz = 12000.0;
    var x = fc11 / 2047.0;                 // 0..1
    // tanh warp pulls the mid-range down (the 6581 "bow").
    var warped = (Math.Tanh(3.0 * (x - 0.5)) + Math.Tanh(1.5)) / (2.0 * Math.Tanh(1.5));
    return baseHz + (maxHz - baseHz) * warped;
  }

  /// <summary>8580 cutoff curve: near-linear from ~0 Hz to ~12.5 kHz.</summary>
  private static double Cutoff8580(int fc11) {
    const double maxHz = 12500.0;
    return maxHz * (fc11 / 2047.0);
  }

  /// <summary>Sets resonance from the RES register high nibble (0..15).</summary>
  public void SetResonance(int res4) {
    res4 &= 0x0F;
    // 6581 resonance is shallower than the 8580; map nibble to a damping factor (1/Q).
    var q = this._model == SidModel.Mos6581
      ? 0.707 + res4 / 15.0 * 1.5    // Q up to ~2.2
      : 0.707 + res4 / 15.0 * 3.3;   // Q up to ~4.0
    this._resonance = 1.0 / q;
  }

  /// <summary>Selects which filter outputs are summed (low nibble of mode = HP/BP/LP bits).</summary>
  public void SetMode(bool lowPass, bool bandPass, bool highPass) {
    this._lowPass = lowPass;
    this._bandPass = bandPass;
    this._highPass = highPass;
  }

  /// <summary>Runs one filter step on <paramref name="input"/> and returns the summed selected outputs.</summary>
  public double Process(double input) {
    // Chamberlin state-variable filter, stepped at the SID clock rate.
    var hp = input - this._lp - this._resonance * this._bp;
    this._bp += this._cutoff * hp;
    this._lp += this._cutoff * this._bp;

    var output = 0.0;
    if (this._lowPass) output += this._lp;
    if (this._bandPass) output += this._bp;
    if (this._highPass) output += hp;
    return output;
  }
}
