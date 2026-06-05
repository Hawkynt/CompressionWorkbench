#pragma warning disable CS1591
namespace Codec.Siren;

/// <summary>
/// Siren / ITU-T G.722.1 (Siren7) audio <b>decoder</b>, a faithful port of FFmpeg's
/// <c>libavcodec/siren.c</c> (the <c>siren</c> decoder path; the MSN-Siren variant is not exposed).
/// Siren7 is a 16 kHz wide-band transform codec: each frame carries <see cref="FrameSize"/> = 320
/// MLT (modulated lapped transform) coefficients grouped into 14 regions of 20 coefficients.
/// <para>
/// Decode pipeline, matching the reference exactly:
/// <list type="number">
///   <item><b>Envelope</b> — an absolute power index for region 0 then a differential Huffman walk
///     (<c>differential_decoder_tree</c>) for the rest, mapping to per-region standard deviations.</item>
///   <item><b>Categorisation</b> — the 16-candidate rate-control search (<c>categorize_regions</c>)
///     assigning each region one of 8 quantiser categories plus a category-balance list, refined by
///     the 4-bit <c>rate_control</c> value.</item>
///   <item><b>Vector decode</b> — per-region SQVH (scalar-quantised vector Huffman) using the seven
///     <c>decoder_tree</c> tables and <c>mlt_quant</c> dequantisation, with deterministic
///     noise-fill (the <c>get_dw</c> PRNG) for the high categories 5/6/7.</item>
///   <item><b>IMLT</b> — a length-320 DCT-IV (the MLT core, scaled by <c>1/(22·32768)</c>) followed
///     by the sine-window 50% overlap-add with the previous frame (the reference's
///     <c>vector_fmul_window</c>), yielding 320 float PCM samples per frame.</item>
/// </list>
/// A frame whose bit budget over/underflows or whose envelope is out of range is concealed by
/// reusing the previous frame's coefficients, exactly as the reference does.
/// </para>
/// <para>
/// Scope: this is Siren7 / G.722.1 (16 kHz, 14 regions). G.722.1 Annex C (Siren14, 32 kHz, 28
/// regions) is <b>not</b> implemented — FFmpeg's <c>siren.c</c> has no 32 kHz / 28-region path to
/// port, and the ITU Annex C IMLT differs in size and window; see <see cref="DecodeFrame"/>.
/// </para>
/// </summary>
public static class SirenCodec {

  /// <summary>MLT coefficients per Siren7 frame (also the IMLT/DCT-IV length).</summary>
  public const int FrameSize = 320;

  /// <summary>Coefficients per region.</summary>
  public const int RegionSize = 20;

  /// <summary>Regions per Siren7 frame.</summary>
  public const int NumberOfRegions = 14;

  /// <summary>Siren7 sample rate (16 kHz, mono).</summary>
  public const int SampleRate = 16000;

  // libavcodec/siren.c siren_init constants for the (non-Microsoft) siren decoder.
  private const int EsfAdjustment = 7;
  private const int ScaleFactor = 22;
  private const int RateControlPossibilities = 16;

  /// <summary>
  /// Streaming Siren7 decoder state: the per-frame deviation/category scratch, the noise-fill PRNG
  /// registers and the IMLT overlap-add history. One instance decodes a whole stream frame by frame.
  /// </summary>
  public sealed class Decoder {
    internal readonly float[] StandardDeviation = new float[64];
    internal readonly int[] AbsoluteRegionPowerIndex = new int[32];
    internal readonly float[] DecoderStandardDeviation = new float[32];
    internal readonly int[] PowerCategories = new int[32];
    internal readonly int[] CategoryBalance = new int[32];
    internal readonly float[] BackupFrame = new float[FrameSize];
    internal readonly float[] ImdctPrev = new float[FrameSize];

    // libavcodec/siren.c: the four 16-bit get_dw registers, all initialised to 1.
    internal uint Dw1 = 1, Dw2 = 1, Dw3 = 1, Dw4 = 1;

    public Decoder() {
      // siren_init: standard_deviation[i] = sqrt(10^((i-24)*0.3010299957)).
      for (var i = 0; i < 64; ++i) {
        var regionPower = Math.Pow(10, (i - 24) * 0.3010299957);
        this.StandardDeviation[i] = (float)Math.Sqrt(regionPower);
      }
    }

    /// <summary>
    /// Decodes one Siren7 frame from <paramref name="frame"/> into <paramref name="output"/>
    /// (<see cref="FrameSize"/> float samples, roughly in [-1, 1]). Returns <c>false</c> when the
    /// packet is too short to initialise the bit reader; concealment of an internally-detected bad
    /// frame still returns <c>true</c> (the previous frame is reused).
    /// </summary>
    public bool DecodeFrame(ReadOnlySpan<byte> frame, Span<float> output) {
      if (frame.Length < 1)
        return false;

      var gb = new SirenBitReader(frame);
      // Non-Microsoft siren: sample_rate_bits = 0, checksum_bits = 0, esf_adjustment = 7.
      var imdctIn = new float[FrameSize];
      var validCoefs = RegionSize * NumberOfRegions;

      var frameError = false;
      var envelopeOk = DecodeEnvelope(this, gb);
      if (!envelopeOk)
        frameError = true;

      var rateControl = 0;
      if (!frameError) {
        rateControl = gb.GetBits(4);
        var categorized = CategorizeRegions(NumberOfRegions, gb.BitsLeft,
          this.AbsoluteRegionPowerIndex, this.PowerCategories, this.CategoryBalance);
        if (!categorized)
          frameError = true;
      }

      if (!frameError) {
        for (var i = 0; i < rateControl; ++i)
          this.PowerCategories[this.CategoryBalance[i]]++;

        var vectorOk = DecodeVector(this, gb, NumberOfRegions, this.DecoderStandardDeviation,
          this.PowerCategories, imdctIn, ScaleFactor);
        if (!vectorOk)
          frameError = true;
      }

      // Trailing-bit / padding consistency checks (reference: leftover bits must all be set).
      if (!frameError) {
        if (gb.BitsLeft > 0) {
          while (gb.BitsLeft > 0)
            frameError |= gb.GetBit() == 0;
        } else if (gb.BitsLeft < 0 && rateControl + 1 < RateControlPossibilities) {
          frameError = true;
        }

        for (var i = 0; i < NumberOfRegions; ++i)
          if (this.AbsoluteRegionPowerIndex[i] > 33 || this.AbsoluteRegionPowerIndex[i] < -31)
            frameError = true;
      }

      if (frameError) {
        Array.Copy(this.BackupFrame, imdctIn, validCoefs);
        Array.Clear(this.BackupFrame, 0, validCoefs);
      } else {
        Array.Copy(imdctIn, this.BackupFrame, validCoefs);
      }

      // siren_decode: negate even-indexed coefficients before the inverse transform.
      for (var i = 0; i < FrameSize; i += 2)
        imdctIn[i] *= -1;

      Imlt(this, imdctIn, output);
      return true;
    }

    /// <summary>Resets the overlap-add history and concealment state (FFmpeg <c>siren_flush</c>).</summary>
    public void Flush() {
      Array.Clear(this.BackupFrame);
      Array.Clear(this.ImdctPrev);
      this.Dw1 = this.Dw2 = this.Dw3 = this.Dw4 = 1;
    }
  }

  // libavcodec/siren.c: decode_envelope. Returns false on an out-of-bits error.
  private static bool DecodeEnvelope(Decoder s, SirenBitReader gb) {
    var ai = s.AbsoluteRegionPowerIndex;
    var dsd = s.DecoderStandardDeviation;

    ai[0] = gb.GetBits(5) - EsfAdjustment;
    ai[0] = Math.Clamp(ai[0], -24, 39);
    dsd[0] = s.StandardDeviation[ai[0] + 24];

    for (var i = 1; i < NumberOfRegions; ++i) {
      var index = 0;
      do {
        // checksum_bits is 0 for siren7.
        if (gb.BitsLeft < 4 + NumberOfRegions - i)
          return false;
        index = SirenTables.DifferentialDecoderTree[i - 1][index][gb.GetBit()];
      } while (index > 0);

      ai[i] = Math.Clamp(ai[i - 1] - index - 12, -24, 39);
      dsd[i] = s.StandardDeviation[ai[i] + 24];
    }

    return true;
  }

  // libavcodec/siren.c: categorize_regions. Returns false on AVERROR_INVALIDDATA.
  internal static bool CategorizeRegions(int numberOfRegions, int numberOfAvailableBits,
      int[] absoluteRegionPowerIndex, int[] powerCategories, int[] categoryBalance) {
    const int numRateControlPossibilities = 16;
    var expectedBits = SirenTables.ExpectedBitsTable;

    var maxRateCategories = new int[28];
    var minRateCategories = new int[28];
    var tempCategoryBalances = new int[64];

    var offset = -32;
    for (var delta = 32; numberOfRegions > 0 && delta > 0; delta /= 2) {
      var expectedNumberOfCodeBits = 0;
      for (var region = 0; region < numberOfRegions; ++region) {
        var i = (delta + offset - absoluteRegionPowerIndex[region]) >> 1;
        i = ClipUintP2(i, 3);
        powerCategories[region] = i;
        expectedNumberOfCodeBits += expectedBits[i];
      }
      if (expectedNumberOfCodeBits >= numberOfAvailableBits - 32)
        offset += delta;
    }

    var expected = 0;
    for (var region = 0; region < numberOfRegions; ++region) {
      var i = (offset - absoluteRegionPowerIndex[region]) >> 1;
      i = ClipUintP2(i, 3);
      maxRateCategories[region] = minRateCategories[region] = powerCategories[region] = i;
      expected += expectedBits[i];
    }

    var min = expected;
    var max = expected;
    var minRatePtr = numRateControlPossibilities;
    var maxRatePtr = numRateControlPossibilities;
    var rawMinIdx = 0;
    var rawMaxIdx = 0;

    for (var i = 0; i < numRateControlPossibilities - 1; ++i) {
      if (min + max > numberOfAvailableBits * 2) {
        var rawValue = -99;
        for (var region = numberOfRegions - 1; region >= 0; --region) {
          if (minRateCategories[region] < 7) {
            var temp = offset - absoluteRegionPowerIndex[region] - 2 * minRateCategories[region];
            if (temp > rawValue) {
              rawValue = temp;
              rawMinIdx = region;
            }
          }
        }
        if (rawValue == -99)
          return false;
        tempCategoryBalances[minRatePtr++] = rawMinIdx;
        min += expectedBits[minRateCategories[rawMinIdx] + 1] - expectedBits[minRateCategories[rawMinIdx]];
        minRateCategories[rawMinIdx]++;
      } else {
        var rawValue = 99;
        for (var region = 0; region < numberOfRegions; ++region) {
          if (maxRateCategories[region] > 0) {
            var temp = offset - absoluteRegionPowerIndex[region] - 2 * maxRateCategories[region];
            if (temp < rawValue) {
              rawValue = temp;
              rawMaxIdx = region;
            }
          }
        }
        if (rawValue == 99)
          return false;
        tempCategoryBalances[--maxRatePtr] = rawMaxIdx;
        max += expectedBits[maxRateCategories[rawMaxIdx] - 1] - expectedBits[maxRateCategories[rawMaxIdx]];
        maxRateCategories[rawMaxIdx]--;
      }
    }

    for (var region = 0; region < numberOfRegions; ++region)
      powerCategories[region] = maxRateCategories[region];

    for (var i = 0; i < numRateControlPossibilities - 1; ++i)
      categoryBalance[i] = tempCategoryBalances[maxRatePtr++];

    return true;
  }

  // libavcodec/siren.c: get_dw — the deterministic noise-fill PRNG.
  private static uint GetDw(Decoder s) {
    var ret = s.Dw1 + s.Dw4;
    if ((ret & 0x8000) != 0)
      ret++;
    ret &= 0xFFFF;

    s.Dw1 = s.Dw2;
    s.Dw2 = s.Dw3;
    s.Dw3 = s.Dw4;
    s.Dw4 = ret;

    return ret;
  }

  // libavcodec/siren.c: decode_vector. Returns false on AVERROR_INVALIDDATA.
  private static bool DecodeVector(Decoder s, SirenBitReader gb, int numberOfRegions,
      float[] decoderStandardDeviation, int[] powerCategories, float[] coefs, int scaleFactor) {
    var error = false;

    for (var region = 0; region < numberOfRegions; ++region) {
      var category = powerCategories[region];
      var coefBase = region * RegionSize;

      if (category is >= 0 and < 7) {
        var decoderTree = SirenTables.DecoderTables[category];
        var elements = decoderTree.Length;
        var ci = coefBase;

        for (var i = 0; i < SirenTables.NumberOfVectors[category]; ++i) {
          var index = 0;
          do {
            if (gb.BitsLeft <= 0) {
              error = true;
              break;
            }
            if (index + gb.ShowBit() >= elements) {
              error = true;
              break;
            }
            index = decoderTree[index + gb.GetBit()];
          } while ((index & 1) == 0);

          index >>= 1;

          if (!error) {
            for (var j = 0; j < SirenTables.VectorDimension[category]; ++j) {
              var decodedValue = SirenTables.MltQuant[category][index & ((1 << SirenTables.IndexTable[category]) - 1)];
              index >>= SirenTables.IndexTable[category];

              if (decodedValue != 0) {
                if (gb.BitsLeft <= 0) {
                  error = true;
                  break;
                }
                decodedValue *= gb.GetBit() == 0
                  ? -decoderStandardDeviation[region]
                  : decoderStandardDeviation[region];
              }

              coefs[ci++] = decodedValue * scaleFactor;
            }
          } else {
            error = true;
            break;
          }
        }

        if (error) {
          for (var j = region + 1; j < numberOfRegions; ++j)
            powerCategories[j] = 7;
          category = 7;
        }
      }

      // Noise-fill for the high (low-rate) categories.
      float noise;
      if (category is 5 or 6) {
        var count = 0;
        for (var j = 0; j < RegionSize; ++j)
          if (coefs[coefBase + j] != 0)
            ++count;
        noise = category == 5
          ? decoderStandardDeviation[region] * SirenTables.NoiseCategory5[count]
          : decoderStandardDeviation[region] * SirenTables.NoiseCategory6[count];
      } else if (category == 7) {
        noise = decoderStandardDeviation[region] * 0.70711f;
      } else {
        noise = 0;
      }

      if (category is 5 or 6 or 7) {
        var dw1 = GetDw(s);
        var dw2 = GetDw(s);
        var ci = coefBase;

        for (var j = 0; j < 10; ++j) {
          if (category == 7 || coefs[ci] == 0)
            coefs[ci] = (dw1 & 1) != 0 ? noise : -noise;
          ++ci;
          dw1 >>= 1;

          if (category == 7 || coefs[ci] == 0)
            coefs[ci] = (dw2 & 1) != 0 ? noise : -noise;
          ++ci;
          dw2 >>= 1;
        }
      }
    }

    return !error;
  }

  // ── Inverse MLT: length-320 DCT-IV core + sine-window 50% overlap-add. ───────────
  //
  // FFmpeg performs this with av_tx (AV_TX_FLOAT_MDCT, len = FRAME_SIZE, scale = 1/(22·32768))
  // feeding s->fdsp->vector_fmul_window over FRAME_SIZE/2. The MLT is a windowed DCT-IV, so the
  // transform core is the DCT-IV the av_tx kernel computes; we evaluate it directly (O(N²), fine
  // for N = 320) with the same scale, then apply the identical sine window + overlap-add.
  private static readonly float[] Window = BuildWindow();

  private static float[] BuildWindow() {
    var window = new float[FrameSize];
    for (var i = 0; i < FrameSize; ++i) {
      var angle = (i + 0.5f) * (Math.PI / 2.0) / 320.0;
      window[i] = (float)Math.Sin(angle);
    }
    return window;
  }

  private static void Imlt(Decoder s, float[] imdctIn, Span<float> output) {
    const double scale = 1.0 / (22.0 * 32768.0);
    var half = FrameSize / 2; // 160

    // DCT-IV: out[n] = scale · Σ_k in[k] · cos( (π/N)·(n+½)·(k+½) ), N = FrameSize.
    var imdctOut = new float[FrameSize];
    var factor = Math.PI / FrameSize;
    for (var n = 0; n < FrameSize; ++n) {
      double sum = 0.0;
      var a = factor * (n + 0.5);
      for (var k = 0; k < FrameSize; ++k)
        sum += imdctIn[k] * Math.Cos(a * (k + 0.5));
      imdctOut[n] = (float)(sum * scale);
    }

    // vector_fmul_window: combine the previous frame's second half with this frame's first half
    // under the sine window (TDAC overlap-add), producing FRAME_SIZE output samples.
    var prev = s.ImdctPrev;
    for (var i = 0; i < half; ++i) {
      var j = FrameSize - 1 - i;
      var s0 = prev[half + i];
      var s1 = imdctOut[i];
      var w0 = Window[i];
      var w1 = Window[j];
      output[i] = s0 * w1 - s1 * w0;
      output[j] = s0 * w0 + s1 * w1;
    }

    Array.Copy(imdctOut, prev, FrameSize);
  }

  // libavutil av_clip_uintp2: clamp to [0, 2^p - 1].
  private static int ClipUintP2(int a, int p) {
    var max = (1 << p) - 1;
    return a < 0 ? 0 : a > max ? max : a;
  }

  /// <summary>
  /// Decodes a raw Siren7 stream — a concatenation of fixed-size frames of
  /// <paramref name="frameBytes"/> each — to 16-bit linear PCM at 16 kHz mono. A trailing fragment
  /// shorter than one frame is ignored. The float IMLT output is scaled by 32768 and clipped to
  /// 16-bit, the conventional float→PCM conversion.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, int frameBytes) {
    if (frameBytes <= 0)
      return [];

    var frames = data.Length / frameBytes;
    if (frames == 0)
      return [];

    var decoder = new Decoder();
    var pcm = new short[frames * FrameSize];
    Span<float> frameOut = stackalloc float[FrameSize];

    for (var f = 0; f < frames; ++f) {
      decoder.DecodeFrame(data.Slice(f * frameBytes, frameBytes), frameOut);
      for (var i = 0; i < FrameSize; ++i) {
        var v = (int)MathF.Round(frameOut[i] * 32768f);
        pcm[f * FrameSize + i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
      }
    }

    return pcm;
  }
}
