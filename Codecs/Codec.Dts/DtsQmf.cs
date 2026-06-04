#pragma warning disable CS1591

namespace Codec.Dts;

/// <summary>
/// 32-band cosine-modulated QMF synthesis filterbank for the DCA core, reconstructing 256 PCM
/// samples per channel per block from 8 sub-subframe vectors of 32 subband samples each. This is a
/// faithful port of FFmpeg's <c>dca_qmf_32_subbands</c> + <c>synth_filter_float</c>
/// (<c>libavcodec/dcadsp.c</c>, <c>synth_filter.c</c>): the per-subband sign flip
/// <c>((i-1)&amp;2)</c>, a direct (matrix-multiply) 64→32 <c>imdct_half</c>, and the 512-tap
/// polyphase window/overlap stage driven by the perfect- or non-perfect-reconstruction prototype
/// (<see cref="DtsTables.Fir32Perfect"/> / <see cref="DtsTables.Fir32NonPerfect"/>). The direct
/// IMDCT replaces FFmpeg's FFT path; the task permits a direct matrix multiply for the transform.
/// </summary>
public sealed class DtsQmf {

  // Persistent synthesis state per channel: the 512-sample circular synth buffer (synth_buf_ptr,
  // stored doubled so the window can read 512 contiguous samples) and the 32-sample carry (synth_buf2).
  private readonly float[] _synthBuf = new float[512];
  private readonly float[] _synthBuf2 = new float[32];
  private int _synthBufOffset;

  // Pre-computed imdct_half cosine matrix: out[k] = sum_n in[n] * Cos[k][n], k,n in 0..31.
  // FFmpeg's MDCT-half of size 64 produces 32 outputs; the equivalent direct kernel is
  // cos(pi/64 * (2k+1) * (n + 0.5)) scaled to match the synth_filter window convention.
  private static readonly float[][] ImdctCos = BuildImdctCos();

  private static float[][] BuildImdctCos() {
    var m = new float[32][];
    for (var k = 0; k < 32; ++k) {
      m[k] = new float[32];
      for (var n = 0; n < 32; ++n)
        m[k][n] = (float)Math.Cos(Math.PI / 64.0 * (2 * k + 1) * (2 * n + 1));
    }
    return m;
  }

  /// <summary>
  /// Synthesises one block (8 sub-subframes × 32 subbands → 256 samples) for one channel into
  /// <paramref name="output"/> starting at <paramref name="outStart"/>. <paramref name="samplesIn"/>
  /// is indexed [subband][subindex]; <paramref name="subbandActivity"/> bounds the active subbands.
  /// </summary>
  public void Process(float[][] samplesIn, int subbandActivity, float[] output, int outStart,
                      bool perfectReconstruction, float scale) {
    var window = perfectReconstruction ? DtsTables.Fir32Perfect : DtsTables.Fir32NonPerfect;
    scale *= (float)Math.Sqrt(1.0 / 8.0);

    Span<float> raXin = stackalloc float[32];
    var outPos = outStart;
    for (var subindex = 0; subindex < 8; ++subindex) {
      for (var i = 0; i < 32; ++i) {
        if (i < subbandActivity) {
          var v = samplesIn[i][subindex];
          // FFmpeg flips the sign of subbands where ((i-1)&2) != 0.
          raXin[i] = ((i - 1) & 2) != 0 ? -v : v;
        } else {
          raXin[i] = 0f;
        }
      }
      this.SynthFilter(window, raXin, output, outPos, scale);
      outPos += 32;
    }
  }

  // Direct port of synth_filter_float: 64-point imdct_half into the synth buffer, then the
  // four-quadrant window/overlap that emits 32 output samples and updates the 32-sample carry.
  private void SynthFilter(float[] window, ReadOnlySpan<float> input, float[] output, int outPos, float scale) {
    var synthBuf = this._synthBuf;
    var off = this._synthBufOffset;

    // imdct_half(synth_buf + off, input): 32 transform outputs at positions [off .. off+31].
    for (var k = 0; k < 32; ++k) {
      var acc = 0f;
      var row = ImdctCos[k];
      for (var n = 0; n < 32; ++n)
        acc += input[n] * row[n];
      synthBuf[off + k] = acc;
    }

    // synth_buf = synth_buf_ptr + off in the reference; the window reads through synth_buf[+511],
    // wrapping past the 512-sample buffer end via the "- 512" branch. We index the underlying
    // buffer absolutely as synthBuf[off + X] (and off + X - 512 once off + X reaches the end).
    for (var i = 0; i < 16; ++i) {
      var a = this._synthBuf2[i];
      var b = this._synthBuf2[i + 16];
      var c = 0f;
      var d = 0f;
      int j;
      for (j = 0; j < 512 - off; j += 64) {
        a += window[i + j] * -synthBuf[off + 15 - i + j];
        b += window[i + j + 16] * synthBuf[off + i + j];
        c += window[i + j + 32] * synthBuf[off + 16 + i + j];
        d += window[i + j + 48] * synthBuf[off + 31 - i + j];
      }
      for (; j < 512; j += 64) {
        a += window[i + j] * -synthBuf[off + 15 - i + j - 512];
        b += window[i + j + 16] * synthBuf[off + i + j - 512];
        c += window[i + j + 32] * synthBuf[off + 16 + i + j - 512];
        d += window[i + j + 48] * synthBuf[off + 31 - i + j - 512];
      }
      output[outPos + i] = a * scale;
      output[outPos + i + 16] = b * scale;
      this._synthBuf2[i] = c;
      this._synthBuf2[i + 16] = d;
    }

    this._synthBufOffset = (off - 32) & 511;
  }
}
