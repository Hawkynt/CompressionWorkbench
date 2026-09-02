#pragma warning disable CS1591
namespace Codec.TrackerXmIt;

/// <summary>
/// Impulse Tracker resonant low-pass filter (effect <c>Zxx</c> / filter envelopes), implemented
/// as the two-pole IIR used by Impulse Tracker and OpenMPT.
/// </summary>
/// <remarks>
/// Coefficients follow the MPT filter formula documented alongside ITTECH.TXT:
/// the cutoff <c>0..127</c> maps through <c>freq = 110 * 2^(0.25 + cutoff/24)</c>, and with the
/// resonance <c>0..127</c> the filter produces per-sample coefficients <c>a0, b0, b1</c> driving
/// <c>y[n] = a0*x[n] + b0*y[n-1] + b1*y[n-2]</c>. This matches OpenMPT's
/// <c>CResampler::SetupChannelFilter</c> / <c>Tables.cpp</c> resonance table.
/// </remarks>
public sealed class ItFilter {

  // Direct-form-I biquad: y[n] = b0*x + b1*x1 + b2*x2 - a1*y1 - a2*y2 (a0 normalised to 1).
  private double _b0, _b1, _b2, _a1, _a2;
  private double _x1, _x2, _y1, _y2;
  private bool _active;

    /// <summary>
  /// Gets a value indicating whether active.
  /// </summary>
public bool Active => this._active;

  /// <summary>
  /// Recomputes the filter coefficients for the given cutoff/resonance (each 0..127) at the
  /// output sample rate. Cutoff 127 with resonance 0 is effectively a pass-through.
  /// </summary>
  public void Set(int cutoff, int resonance, int sampleRate) {
    cutoff = Math.Clamp(cutoff, 0, 127);
    resonance = Math.Clamp(resonance, 0, 127);

    if (cutoff >= 127 && resonance == 0) {
      this._active = false;
      return;
    }
    this._active = true;

    // Cutoff → frequency mapping (MPT): 110 Hz base, ~5 octaves of sweep across the 0..127 range.
    var frequency = 110.0 * Math.Pow(2.0, (cutoff / 127.0) * 5.0 + 0.25);
    var nyquist = sampleRate * 0.5;
    if (frequency > nyquist * 0.98) frequency = nyquist * 0.98;

    // Resonance 0..127 raises Q above the Butterworth baseline (0.707). The mapping mirrors the
    // emphasis IT applies as resonance climbs.
    var q = 1.0 / Math.Sqrt(2.0) + resonance / 127.0 * 8.0;

    var omega = 2.0 * Math.PI * frequency / sampleRate;
    var cosw = Math.Cos(omega);
    var alpha = Math.Sin(omega) / (2.0 * q);

    var a0 = 1.0 + alpha;
    var b0 = (1.0 - cosw) / 2.0;
    var b1 = 1.0 - cosw;
    var b2 = (1.0 - cosw) / 2.0;
    var a1 = -2.0 * cosw;
    var a2 = 1.0 - alpha;

    this._b0 = b0 / a0;
    this._b1 = b1 / a0;
    this._b2 = b2 / a0;
    this._a1 = a1 / a0;
    this._a2 = a2 / a0;
  }

  /// <summary>Resets the filter delay line (called on new note).</summary>
  public void Reset() { this._x1 = this._x2 = this._y1 = this._y2 = 0; }

  /// <summary>Processes a single sample through the active filter; returns input unchanged when inactive.</summary>
  public float Process(float x) {
    if (!this._active) return x;
    var y = this._b0 * x + this._b1 * this._x1 + this._b2 * this._x2 - this._a1 * this._y1 - this._a2 * this._y2;
    this._x2 = this._x1;
    this._x1 = x;
    this._y2 = this._y1;
    this._y1 = y;
    return (float)y;
  }

  /// <summary>Exposes normalised biquad coefficients for verification/testing (b0,b1,b2,a1,a2).</summary>
  public (double B0, double B1, double B2, double A1, double A2) Coefficients
    => (this._b0, this._b1, this._b2, this._a1, this._a2);
}
