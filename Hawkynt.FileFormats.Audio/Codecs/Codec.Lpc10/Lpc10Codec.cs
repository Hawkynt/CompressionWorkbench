#pragma warning disable CS1591
namespace Codec.Lpc10;

/// <summary>
/// FS-1015 (LPC-10e) 2400 bit/s military speech vocoder — analysis (encode) and synthesis
/// (decode) for round-trip testing. The codec models speech as an all-pole vocal-tract filter
/// (order 10) excited either by a periodic pulse train (voiced) or white noise (unvoiced).
/// <para>
/// One frame is <see cref="Lpc10Tables.FrameSamples"/> (180) samples at
/// <see cref="Lpc10Tables.SampleRate"/> (8 kHz) coded into <see cref="Lpc10Tables.FrameBits"/>
/// (54) bits, packed into <see cref="Lpc10Tables.FrameBytes"/> (7) bytes (the low two bits of
/// the last byte are padding) → 44.4 frames/s × 54 bits = 2400 bit/s.
/// </para>
/// <para>
/// Pipeline, with the reference-faithful vs. simplified stages called out:
/// <list type="bullet">
///   <item><b>Pre-emphasis / de-emphasis</b> (faithful): a first-order high-pass on encode,
///     matching low-pass on decode.</item>
///   <item><b>Pitch &amp; voicing</b> (SIMPLIFIED): the reference uses the Gold–Rabiner pitch
///     tracker with onset detection and a dynamic-programming voicing smoother. Here pitch is
///     estimated with the Average Magnitude Difference Function (AMDF) over the LPC-residual,
///     and voicing from the AMDF contrast plus low-band energy. This preserves pitch
///     periodicity and the voiced/unvoiced decision, but is not the bit-exact reference
///     tracker.</item>
///   <item><b>LPC / reflection coefficients</b> (faithful in structure): autocorrelation →
///     Levinson–Durbin recursion yields the ten reflection coefficients, quantized with the
///     LPC-10 per-coefficient bit allocation (<see cref="Lpc10Tables.ReflectionCoefficientBits"/>).</item>
///   <item><b>RMS energy</b> (faithful in structure): log-quantized in 5 bits.</item>
///   <item><b>Synthesis</b> (faithful): pulse-train / noise excitation scaled to the decoded
///     RMS, run through the lattice synthesis filter built from the decoded reflection
///     coefficients, then de-emphasized.</item>
/// </list>
/// </para>
/// </summary>
public static class Lpc10Codec {

  private const double PreEmphasis = 0.9375; // 15/16, the reference first-order coefficient.

  /// <summary>
  /// Encodes 16-bit linear PCM (8 kHz mono) to packed LPC-10 frames. The PCM is processed in
  /// 180-sample frames; a trailing partial frame is zero-padded. The result is
  /// <c>frames × 7</c> bytes.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    var frameCount = (pcm.Length + Lpc10Tables.FrameSamples - 1) / Lpc10Tables.FrameSamples;
    if (frameCount == 0)
      return [];

    var output = new byte[frameCount * Lpc10Tables.FrameBytes];
    var frame = new double[Lpc10Tables.FrameSamples];
    var preState = 0.0;

    for (var f = 0; f < frameCount; ++f) {
      var baseIndex = f * Lpc10Tables.FrameSamples;
      var signalEnergy = 0.0;
      for (var i = 0; i < Lpc10Tables.FrameSamples; ++i) {
        var s = baseIndex + i < pcm.Length ? pcm[baseIndex + i] / 32768.0 : 0.0;
        signalEnergy += s * s;
        // First-order pre-emphasis high-pass (carries state across frames).
        var pre = s - PreEmphasis * preState;
        preState = s;
        frame[i] = pre;
      }
      // The coded RMS represents the ORIGINAL (pre-emphasis-free) signal level, so the
      // de-emphasized decoder output is normalized back to the true loudness.
      var signalRms = Math.Sqrt(signalEnergy / Lpc10Tables.FrameSamples);

      var bits = AnalyzeFrame(frame, signalRms);
      PackFrame(bits, output.AsSpan(f * Lpc10Tables.FrameBytes, Lpc10Tables.FrameBytes));
    }

    return output;
  }

  /// <summary>
  /// Decodes packed LPC-10 frames back to 16-bit linear PCM. Each 7-byte frame yields
  /// <see cref="Lpc10Tables.FrameSamples"/> (180) samples, so the output length is
  /// <c>frames × 180</c>.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data) {
    var frameCount = data.Length / Lpc10Tables.FrameBytes;
    if (frameCount == 0)
      return [];

    var output = new short[frameCount * Lpc10Tables.FrameSamples];
    var synth = new SynthesisState();

    for (var f = 0; f < frameCount; ++f) {
      var bits = UnpackFrame(data.Slice(f * Lpc10Tables.FrameBytes, Lpc10Tables.FrameBytes));
      synth.SynthesizeFrame(bits, output.AsSpan(f * Lpc10Tables.FrameSamples, Lpc10Tables.FrameSamples));
    }

    return output;
  }

  // ── Analysis ──────────────────────────────────────────────────────────────────────

  /// <summary>Per-frame coded parameters (decoded values are quantized round-trips of these).</summary>
  private readonly record struct FrameBits(int PitchIndex, int RmsIndex, bool Voiced, int[] ReflectionCodes);

  private static FrameBits AnalyzeFrame(double[] frame, double signalRms) {
    var n = frame.Length;

    var rmsIndex = QuantizeRms(signalRms);

    // Autocorrelation → Levinson–Durbin → reflection coefficients.
    var autocorr = new double[Lpc10Tables.Order + 1];
    for (var lag = 0; lag <= Lpc10Tables.Order; ++lag) {
      var sum = 0.0;
      for (var i = lag; i < n; ++i)
        sum += frame[i] * frame[i - lag];
      autocorr[lag] = sum;
    }

    var reflection = LevinsonDurbin(autocorr, out var residual);
    var reflectionCodes = new int[Lpc10Tables.Order];
    for (var i = 0; i < Lpc10Tables.Order; ++i)
      reflectionCodes[i] = QuantizeReflection(reflection[i], i);

    // Pitch / voicing from the AMDF of the LPC residual approximation. We reuse the frame
    // signal (post pre-emphasis) for the AMDF; the residual energy ratio drives voicing.
    var (pitchLag, voiced) = EstimatePitch(frame, autocorr[0], residual);
    var pitchIndex = voiced ? Lpc10Tables.PitchLagToIndex(pitchLag) : 0;

    return new FrameBits(pitchIndex, rmsIndex, voiced, reflectionCodes);
  }

  /// <summary>
  /// Levinson–Durbin recursion: turns the autocorrelation sequence into reflection
  /// coefficients (returned) and leaves the final prediction-error energy in
  /// <paramref name="residualEnergy"/>.
  /// </summary>
  private static double[] LevinsonDurbin(double[] autocorr, out double residualEnergy) {
    var order = Lpc10Tables.Order;
    var reflection = new double[order];
    var lpc = new double[order + 1];
    var error = autocorr[0];

    if (error <= 0) {
      residualEnergy = 0;
      return reflection;
    }

    for (var i = 0; i < order; ++i) {
      var acc = autocorr[i + 1];
      for (var j = 0; j < i; ++j)
        acc -= lpc[j + 1] * autocorr[i - j];
      var k = error > 0 ? acc / error : 0.0;
      k = Math.Clamp(k, -0.999, 0.999);
      reflection[i] = k;

      lpc[i + 1] = k;
      for (var j = 0; j < i / 2; ++j) {
        var tmp = lpc[j + 1];
        lpc[j + 1] = tmp - k * lpc[i - j];
        lpc[i - j] -= k * tmp;
      }
      if ((i & 1) != 0)
        lpc[i / 2 + 1] -= k * lpc[i / 2 + 1];

      error *= 1 - k * k;
      if (error <= 0) {
        error = 0;
        break;
      }
    }

    residualEnergy = error;
    return reflection;
  }

  /// <summary>
  /// SIMPLIFIED pitch tracker: AMDF over the frame finds the minimum-difference lag in the
  /// LPC-10 pitch range; the contrast between the AMDF minimum and its mean, together with the
  /// frame energy, decides voicing. The reference Gold–Rabiner tracker (onset detection,
  /// dynamic-programming voicing) is approximated here while preserving periodicity and the
  /// voiced/unvoiced decision.
  /// </summary>
  private static (int Lag, bool Voiced) EstimatePitch(double[] frame, double energy, double residual) {
    var n = frame.Length;
    if (energy < 1e-6)
      return (0, false); // silence → unvoiced

    var amdf = new double[Lpc10Tables.MaxPitchLag + 1];
    var bestLag = 0;
    var bestAmdf = double.MaxValue;
    var amdfSum = 0.0;
    var amdfCount = 0;

    for (var lag = Lpc10Tables.MinPitchLag; lag <= Lpc10Tables.MaxPitchLag; ++lag) {
      var diff = 0.0;
      var count = 0;
      for (var i = lag; i < n; ++i) {
        diff += Math.Abs(frame[i] - frame[i - lag]);
        ++count;
      }
      if (count == 0)
        continue;
      var value = diff / count;
      amdf[lag] = value;
      amdfSum += value;
      ++amdfCount;
      if (value < bestAmdf) {
        bestAmdf = value;
        bestLag = lag;
      }
    }

    if (amdfCount == 0 || bestLag == 0)
      return (0, false);

    // Octave-correction: AMDF valleys also appear at integer multiples of the true period.
    // If a sub-multiple of the best lag is itself a near-equally-deep valley, prefer it so we
    // don't lock onto a pitch an octave (or more) too low.
    for (var divisor = 2; bestLag / divisor >= Lpc10Tables.MinPitchLag; ++divisor) {
      var candidate = bestLag / divisor;
      if (amdf[candidate] > 0 && amdf[candidate] < bestAmdf * 1.15)
        bestLag = candidate;
    }

    var amdfMean = amdfSum / amdfCount;
    // Strong periodicity (deep AMDF valley) plus a low residual-to-energy ratio → voiced.
    var contrast = amdfMean > 1e-9 ? bestAmdf / amdfMean : 1.0;
    var predictionGain = energy > 1e-9 ? residual / energy : 1.0;
    var voiced = contrast < 0.85 && predictionGain < 0.7;
    return voiced ? (bestLag, true) : (0, false);
  }

  // ── Quantization ────────────────────────────────────────────────────────────────────

  private static int QuantizeReflection(double rc, int index) {
    var bits = Lpc10Tables.ReflectionCoefficientBits[index];
    var levels = 1 << bits;
    var step = Lpc10Tables.ReflectionCoefficientQuantStep[index];
    var bias = Lpc10Tables.ReflectionCoefficientQuantBias[index];
    var code = (int)Math.Round((rc - bias) / step);
    return Math.Clamp(code, 0, levels - 1);
  }

  private static double DequantizeReflection(int code, int index) {
    var step = Lpc10Tables.ReflectionCoefficientQuantStep[index];
    var bias = Lpc10Tables.ReflectionCoefficientQuantBias[index];
    return bias + code * step;
  }

  // RMS is log-quantized: code = round(log2(rms)·scale + offset) clamped to 5 bits.
  private const double RmsLogScale = 2.0;
  private const double RmsLogOffset = 28.0;

  private static int QuantizeRms(double rms) {
    if (rms < 1e-6)
      return 0;
    var levels = 1 << Lpc10Tables.RmsBits;
    var code = (int)Math.Round(Math.Log2(rms) * RmsLogScale + RmsLogOffset);
    return Math.Clamp(code, 0, levels - 1);
  }

  private static double DequantizeRms(int code) {
    if (code <= 0)
      return 0;
    return Math.Pow(2.0, (code - RmsLogOffset) / RmsLogScale);
  }

  // ── Bit packing ───────────────────────────────────────────────────────────────────────

  private static void PackFrame(FrameBits bits, Span<byte> dest) {
    Span<bool> bitstream = stackalloc bool[Lpc10Tables.FrameBits];
    var pos = 0;

    WriteBits(bitstream, ref pos, bits.PitchIndex, Lpc10Tables.PitchBits);
    WriteBits(bitstream, ref pos, bits.RmsIndex, Lpc10Tables.RmsBits);
    // Two voicing bits (begin/end of frame); we use a single decision replicated.
    WriteBits(bitstream, ref pos, bits.Voiced ? 1 : 0, 1);
    WriteBits(bitstream, ref pos, bits.Voiced ? 1 : 0, 1);
    for (var i = 0; i < Lpc10Tables.Order; ++i)
      WriteBits(bitstream, ref pos, bits.ReflectionCodes[i], Lpc10Tables.ReflectionCoefficientBits[i]);

    dest.Clear();
    for (var i = 0; i < Lpc10Tables.FrameBits; ++i)
      if (bitstream[i])
        dest[i >> 3] |= (byte)(1 << (7 - (i & 7)));
  }

  private static FrameBits UnpackFrame(ReadOnlySpan<byte> src) {
    Span<bool> bitstream = stackalloc bool[Lpc10Tables.FrameBits];
    for (var i = 0; i < Lpc10Tables.FrameBits; ++i)
      bitstream[i] = ((src[i >> 3] >> (7 - (i & 7))) & 1) != 0;

    var pos = 0;
    var pitchIndex = ReadBits(bitstream, ref pos, Lpc10Tables.PitchBits);
    var rmsIndex = ReadBits(bitstream, ref pos, Lpc10Tables.RmsBits);
    var v0 = ReadBits(bitstream, ref pos, 1);
    var v1 = ReadBits(bitstream, ref pos, 1);
    var voiced = (v0 + v1) >= 1;
    var codes = new int[Lpc10Tables.Order];
    for (var i = 0; i < Lpc10Tables.Order; ++i)
      codes[i] = ReadBits(bitstream, ref pos, Lpc10Tables.ReflectionCoefficientBits[i]);

    return new FrameBits(pitchIndex, rmsIndex, voiced, codes);
  }

  private static void WriteBits(Span<bool> stream, ref int pos, int value, int count) {
    for (var i = count - 1; i >= 0; --i)
      stream[pos++] = ((value >> i) & 1) != 0;
  }

  private static int ReadBits(ReadOnlySpan<bool> stream, ref int pos, int count) {
    var value = 0;
    for (var i = 0; i < count; ++i)
      value = (value << 1) | (stream[pos++] ? 1 : 0);
    return value;
  }

  // ── Synthesis ─────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Lattice synthesis filter + excitation generator. State (filter memory, pitch phase,
  /// noise generator) carries across frames for continuity.
  /// </summary>
  private sealed class SynthesisState {
    private readonly double[] _filterMemory = new double[Lpc10Tables.Order + 1];
    private int _pitchPhase;
    private uint _noiseState = 0x1234_5678;
    private double _deEmphState;

    public void SynthesizeFrame(FrameBits bits, Span<short> output) {
      var rms = DequantizeRms(bits.RmsIndex);
      var reflection = new double[Lpc10Tables.Order];
      for (var i = 0; i < Lpc10Tables.Order; ++i)
        reflection[i] = DequantizeReflection(bits.ReflectionCodes[i], i);

      var lag = Lpc10Tables.PitchIndexToLag(bits.PitchIndex);
      var voiced = bits.Voiced && lag > 0;

      // Unit-level excitation: voiced is a sparse unit pulse train (one pulse per period),
      // unvoiced is unit-variance white noise. The synthesis-filter gain is unknown a priori,
      // so we run the filter first and then normalize the de-emphasized frame to the decoded
      // RMS — this keeps the energy envelope faithful regardless of the filter's gain.
      Span<double> scratch = stackalloc double[Lpc10Tables.FrameSamples];
      for (var i = 0; i < Lpc10Tables.FrameSamples; ++i) {
        double excitation;
        if (voiced) {
          if (_pitchPhase <= 0) {
            excitation = 1.0;
            _pitchPhase = lag;
          } else
            excitation = 0.0;
          --_pitchPhase;
        } else
          excitation = NextNoise();

        var sample = LatticeSynthesize(reflection, excitation);

        // First-order de-emphasis (inverse of the encoder's pre-emphasis); the integrator
        // state carries across frames for continuity.
        sample += PreEmphasis * _deEmphState;
        _deEmphState = sample;
        scratch[i] = sample;
      }

      // Normalize to the decoded RMS energy envelope.
      var frameEnergy = 0.0;
      for (var i = 0; i < scratch.Length; ++i)
        frameEnergy += scratch[i] * scratch[i];
      var frameRms = Math.Sqrt(frameEnergy / scratch.Length);
      var gain = frameRms > 1e-9 ? rms / frameRms : 0.0;

      for (var i = 0; i < Lpc10Tables.FrameSamples; ++i)
        output[i] = (short)Math.Clamp(scratch[i] * gain * 32768.0, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// One sample through the all-pole lattice synthesis filter parameterized by reflection
    /// coefficients. <paramref name="excitation"/> drives the highest-order stage; the
    /// backward-prediction memory is fed forward stage by stage.
    /// </summary>
    private double LatticeSynthesize(double[] reflection, double excitation) {
      var order = Lpc10Tables.Order;
      var f = excitation;
      // Walk stages from highest to lowest, updating forward/backward lattice variables.
      for (var i = order - 1; i >= 0; --i) {
        f -= reflection[i] * _filterMemory[i];
        _filterMemory[i + 1] = _filterMemory[i] + reflection[i] * f;
      }
      _filterMemory[0] = f;
      return f;
    }

    /// <summary>White noise in [-1, 1) from a fast xorshift generator (deterministic).</summary>
    private double NextNoise() {
      _noiseState ^= _noiseState << 13;
      _noiseState ^= _noiseState >> 17;
      _noiseState ^= _noiseState << 5;
      return (int)_noiseState / 2147483648.0;
    }
  }
}
