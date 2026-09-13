#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// Complex QMF analysis and synthesis filter banks for SBR
/// (ISO/IEC 14496-3 §4.6.18.4). The analysis bank splits the real-valued AAC-LC
/// core output into 32 complex subbands per QMF time slot; the synthesis bank
/// recombines 64 complex subbands into a real signal at twice the core rate.
/// <para>
/// Both banks use the shared 640-tap prototype window (<see cref="AacSbrTables.QmfWindow"/>)
/// and the direct modulation form from the standard (cosine/sine matrices, computed
/// at construction). This direct implementation trades speed for verifiability:
/// an analysis→synthesis round-trip of a band-limited signal reproduces a delayed,
/// scaled copy of the input (see the QMF identity test), which is the canonical
/// sanity check for the DSP chain.
/// </para>
/// <para>
/// Buffer bookkeeping mirrors the spec: the analysis bank keeps a 320-sample sliding
/// window; the synthesis bank keeps a 1280-sample <c>v</c> array. State is per
/// instance so successive frames overlap-add correctly.
/// </para>
/// </summary>
internal sealed class AacSbrQmf {

  // Analysis: 32 subbands. Modulation matrix M_ana[k][n] = cos( pi/64 * (2k+1) * (2n-1) )
  // and the matching sine part for the complex (low-power-off) form.
  private readonly float[,] _anaCos; // [32, 64]
  private readonly float[,] _anaSin; // [32, 64]
  private readonly float[] _anaBuf = new float[320]; // sliding input window x

  // Synthesis: 64 subbands -> real output at 2x. Modulation
  // M_syn[n][k] = cos( pi/128 * (2k+1) * (2n - 255) ) etc.
  private readonly float[,] _synCos; // [128, 64]
  private readonly float[,] _synSin; // [128, 64]
  private readonly float[] _synV = new float[1280];

  public AacSbrQmf() {
    this._anaCos = new float[32, 64];
    this._anaSin = new float[32, 64];
    for (var k = 0; k < 32; ++k)
      for (var n = 0; n < 64; ++n) {
        var ang = Math.PI / 64.0 * (2 * k + 1) * (2 * n - 1) / 2.0;
        // Spec analysis modulation: cos( (pi/128)(2k+1)(2n-1) ) with k=0..31, n=0..63.
        var a = Math.PI / 128.0 * (2 * k + 1) * (2 * n - 1);
        this._anaCos[k, n] = (float)Math.Cos(a);
        this._anaSin[k, n] = (float)Math.Sin(a);
        _ = ang;
      }

    this._synCos = new float[128, 64];
    this._synSin = new float[128, 64];
    for (var n = 0; n < 128; ++n)
      for (var k = 0; k < 64; ++k) {
        var a = Math.PI / 128.0 * (2 * k + 1) * (2 * n - 255);
        this._synCos[n, k] = (float)Math.Cos(a);
        this._synSin[n, k] = (float)Math.Sin(a);
      }
  }

  /// <summary>
  /// Analysis QMF: pushes 32 new real input samples and produces one time slot of
  /// 32 complex subbands (<paramref name="real"/>/<paramref name="imag"/>, length 32).
  /// </summary>
  public void Analysis(ReadOnlySpan<float> input32, Span<float> real, Span<float> imag) {
    // Shift window: x[0..287] = x[32..319]; x[288..319] = new (time-reversed per spec).
    Array.Copy(this._anaBuf, 32, this._anaBuf, 0, 288);
    for (var i = 0; i < 32; ++i)
      this._anaBuf[288 + i] = input32[31 - i];

    // u[n] = sum_j window[n + 64 j] * x[n + 64 j], n = 0..63  (windowed + 5-fold sum)
    Span<float> u = stackalloc float[64];
    for (var n = 0; n < 64; ++n) {
      float s = 0;
      for (var j = 0; j < 5; ++j)
        s += AacSbrTables.QmfWindow[n + 64 * j] * this._anaBuf[n + 64 * j];
      u[n] = s;
    }

    // Subband samples: W[k] = sum_n u[n] * exp(...) -> cos/sin modulation.
    for (var k = 0; k < 32; ++k) {
      float re = 0, im = 0;
      for (var n = 0; n < 64; ++n) {
        re += u[n] * this._anaCos[k, n];
        im += u[n] * this._anaSin[k, n];
      }
      real[k] = re;
      imag[k] = im;
    }
  }

  /// <summary>
  /// Synthesis QMF: takes one time slot of 64 complex subbands and appends 64 real
  /// output samples (twice the core rate per slot) to <paramref name="output64"/>.
  /// </summary>
  public void Synthesis(ReadOnlySpan<float> real, ReadOnlySpan<float> imag, Span<float> output64) {
    // Shift v buffer down by 128.
    Array.Copy(this._synV, 0, this._synV, 128, 1280 - 128);

    // v[n] = sum_k ( Sr[n,k]*real[k] - Si[n,k]*imag[k] ), n = 0..127
    for (var n = 0; n < 128; ++n) {
      float s = 0;
      for (var k = 0; k < 64; ++k)
        s += this._synCos[n, k] * real[k] - this._synSin[n, k] * imag[k];
      this._synV[n] = s;
    }

    // Build the 640-sample g vector from strided v and window, then 64 outputs.
    Span<float> g = stackalloc float[640];
    for (var n = 0; n < 5; ++n) {
      for (var i = 0; i < 64; ++i) {
        g[128 * n + i] = this._synV[256 * n + i];
        g[128 * n + 64 + i] = this._synV[256 * n + 192 + i];
      }
    }
    for (var i = 0; i < 640; ++i)
      g[i] *= AacSbrTables.QmfWindow[i];

    for (var i = 0; i < 64; ++i) {
      float s = 0;
      for (var n = 0; n < 10; ++n)
        s += g[64 * n + i];
      output64[i] = s;
    }
  }
}
