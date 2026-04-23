#pragma warning disable CS1591

namespace Codec.Opus;

/// <summary>
/// Rational-rate resampler used to bring SILK's 8 / 12 / 16 / 24 kHz output up
/// to CELT's native 48 kHz so hybrid / SILK-only configs can emit to the unified
/// output rate.
/// <para>
/// <b>Status:</b> simple linear interpolation stub — replace with libopus's
/// Kaiser-windowed sinc ("speex_resampler") in a follow-up wave when SILK is
/// wired up. Kept as a separate class so the API surface is stable while the
/// internals are upgraded.
/// </para>
/// </summary>
public sealed class OpusResampler {

  /// <summary>Creates a resampler converting <paramref name="inputRate"/> → <paramref name="outputRate"/>.</summary>
  public OpusResampler(int inputRate, int outputRate, int channels) {
    if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
    if (outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate));
    if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
    this.InputRate = inputRate;
    this.OutputRate = outputRate;
    this.Channels = channels;
  }

  public int InputRate { get; }
  public int OutputRate { get; }
  public int Channels { get; }

  /// <summary>Resamples <paramref name="input"/> (interleaved float) into <paramref name="output"/>.</summary>
  public int Resample(ReadOnlySpan<float> input, Span<float> output) {
    if (this.InputRate == this.OutputRate) {
      var n = Math.Min(input.Length, output.Length);
      input[..n].CopyTo(output);
      return n;
    }

    var ratio = (double)this.OutputRate / this.InputRate;
    var inFrames = input.Length / this.Channels;
    var outFrames = (int)(inFrames * ratio);
    var maxOutFrames = output.Length / this.Channels;
    if (outFrames > maxOutFrames) outFrames = maxOutFrames;

    for (var i = 0; i < outFrames; i++) {
      var srcPos = i / ratio;
      var idx = (int)srcPos;
      var frac = (float)(srcPos - idx);
      var next = Math.Min(idx + 1, inFrames - 1);
      for (var c = 0; c < this.Channels; c++) {
        var a = input[idx * this.Channels + c];
        var b = input[next * this.Channels + c];
        output[i * this.Channels + c] = a + (b - a) * frac;
      }
    }
    return outFrames * this.Channels;
  }
}
