#pragma warning disable CS1591
namespace Codec.CriHca;

/// <summary>
/// Per-channel CRI HCA inverse MDCT with overlap-add, ported from VGAudio's
/// <c>Mdct.RunImdct</c> (the reference CRI's <c>clHCA</c> documentation points to).
/// The forward transform is a 128-point DCT-IV; this evaluates the DCT-IV directly
/// (the documented <c>Dct4Slow</c>, an exact O(n²) cosine sum — the mission permits the
/// direct form over the radix butterfly) with scale <c>sqrt(2/128)</c>, then performs
/// the windowed time-domain-aliasing-cancellation overlap against the previous
/// subframe using <see cref="HcaMdctWindow.Window"/>. Each subframe takes 128 spectral
/// coefficients and emits 128 PCM-domain samples; the 128-sample overlap carry persists
/// across subframes (and across frames within a channel).
/// </summary>
internal sealed class HcaImdct {
  private const int Size = 128;
  private const int Half = Size / 2;
  private static readonly double Scale = Math.Sqrt(2.0 / Size);

  private readonly double[] _previous = new double[Size];
  private readonly double[] _dctOut = new double[Size];

  /// <summary>Resets the overlap carry (called when a fresh stream begins).</summary>
  public void Reset() => Array.Clear(this._previous, 0, this._previous.Length);

  /// <summary>
  /// Transforms 128 spectral coefficients in <paramref name="input"/> into 128 PCM-domain
  /// samples in <paramref name="output"/>, applying the synthesis window and overlap-add.
  /// </summary>
  public void RunImdct(double[] input, double[] output) {
    var dctOut = this._dctOut;
    Dct4(input, dctOut);

    var window = HcaMdctWindow.Window;
    var prev = this._previous;
    for (var i = 0; i < Half; i++) {
      output[i] = window[i] * dctOut[i + Half] + prev[i];
      output[i + Half] = window[i + Half] * -dctOut[Size - 1 - i] - prev[i + Half];
      prev[i] = window[Size - 1 - i] * -dctOut[Half - i - 1];
      prev[i + Half] = window[Half - i - 1] * dctOut[i];
    }
  }

  // Direct DCT-IV (VGAudio Mdct.Dct4Slow): output[k] = Scale * Σ_n cos(π/N·(k+½)(n+½))·input[n].
  private static void Dct4(double[] input, double[] output) {
    for (var k = 0; k < Size; k++) {
      var sample = 0.0;
      for (var n = 0; n < Size; n++)
        sample += Math.Cos(Math.PI / Size * (k + 0.5) * (n + 0.5)) * input[n];
      output[k] = sample * Scale;
    }
  }
}
