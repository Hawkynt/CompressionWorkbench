#pragma warning disable CS1591
namespace Codec.Atrac3;

/// <summary>
/// Sony ATRAC3 (Adaptive TRansform Acoustic Coding 3) decoder — a faithful, decode-only
/// port of FFmpeg's <c>libavcodec/atrac3.c</c> together with the shared ATRAC routines in
/// <c>libavcodec/atrac.c</c>. ATRAC3 is the codec behind Sony OpenMG (.oma/.aa3/.at3) with
/// codec id 0 and RealAudio 8 ("atrc") streams.
/// <para>Each coded frame holds <c>block_align/channels</c> bytes per channel and always
/// decodes to exactly 1024 samples per channel. A channel sound unit carries gain-control
/// envelopes, tonal components, and band spectra (mantissas via canonical Huffman / constant
/// length coding at SNR-derived word lengths, scaled by the ATRAC scale-factor table). Each
/// of the four QMF bands runs a 512-point inverse MDCT, is windowed and gain-compensated
/// against the previous frame, and the bands are recombined by a three-stage inverse QMF.
/// Joint-stereo frames additionally reverse the per-band M/S matrixing and apply channel
/// weighting.</para>
/// <para>Decode-only — there is no encoder. Per-stream state (QMF delay buffers, previous
/// frame overlap, joint-stereo matrix/weighting history) is carried across frames exactly as
/// the reference does, so feeding a buffer of concatenated frames decodes identically to
/// feeding the frames one at a time.</para>
/// </summary>
public sealed class Atrac3Codec {

  private const int SamplesPerFrame = 1024;
  private const int MdctSize = 512;
  private const int JointStereo = 0x12;
  private const int Single = 0x2;
  private const int MaxJsPairs = 8 / 2;

  private const float MdctScale = 1.0f / 32768.0f;

  /// <summary>Coding mode = JOINT_STEREO (channel coupling) vs SINGLE (independent channels).</summary>
  public bool IsJointStereo => this._codingMode == JointStereo;

  /// <summary>Sample rate carried by the container (informational; does not change decoding).</summary>
  public int SampleRate { get; }

  /// <summary>Channel count (1–8).</summary>
  public int Channels { get; }

  /// <summary>Total coded bytes per frame across all channels (block align).</summary>
  public int BlockAlign { get; }

  /// <summary>Whether the input is RM-scrambled (RealMedia "atrc") and needs descrambling.</summary>
  public bool Scrambled { get; }

  private readonly int _codingMode;

  // Static (shared) tables.
  private static readonly Atrac3Vlc[] SpectralVlc = BuildSpectralVlc();

  // Gain compensation context (ff_atrac_init_gain_compensation(gc, 4, 3)).
  private const int Id2ExpOffset = 4;
  private const int LocScale = 3;
  private const int LocSize = 1 << LocScale;
  private static readonly float[] GainTab1 = BuildGainTab1();
  private static readonly float[] GainTab2 = BuildGainTab2();

  // Per-stream state.
  private readonly ChannelUnit[] _units;
  private readonly int[][] _matrixPrev = new int[MaxJsPairs][];
  private readonly int[][] _matrixNow = new int[MaxJsPairs][];
  private readonly int[][] _matrixNext = new int[MaxJsPairs][];
  private readonly int[][] _weightingDelay = new int[MaxJsPairs][];
  private readonly float[] _tempBuf = new float[1070];
  private readonly byte[] _descrambleBuf;

  /// <summary>
  /// Constructs a decoder. <paramref name="codingMode"/> must be <c>0x12</c> (joint stereo)
  /// or <c>0x2</c> (single/independent). <paramref name="blockAlign"/> is the total coded
  /// bytes per frame across all channels and must satisfy
  /// <c>blockAlign / channels ∈ {96, 152, 192}</c> for a WAV/OMA stream.
  /// </summary>
  public Atrac3Codec(int sampleRate, int channels, int blockAlign, int codingMode, bool scrambled) {
    if (channels is < 1 or > 8)
      throw new ArgumentOutOfRangeException(nameof(channels), channels, "ATRAC3 supports 1–8 channels.");
    if (codingMode != JointStereo && codingMode != Single)
      throw new ArgumentException($"Unknown ATRAC3 coding mode 0x{codingMode:X}.", nameof(codingMode));
    if (codingMode == JointStereo && (channels % 2) != 0)
      throw new ArgumentException("Joint-stereo ATRAC3 requires an even channel count.", nameof(channels));
    if (blockAlign <= 0 || blockAlign > 4096)
      throw new ArgumentOutOfRangeException(nameof(blockAlign), blockAlign, "ATRAC3 block align out of range.");

    this.SampleRate = sampleRate;
    this.Channels = channels;
    this.BlockAlign = blockAlign;
    this._codingMode = codingMode;
    this.Scrambled = scrambled;

    this._units = new ChannelUnit[channels];
    for (var i = 0; i < channels; ++i)
      this._units[i] = new ChannelUnit();

    for (var p = 0; p < MaxJsPairs; ++p) {
      this._weightingDelay[p] = [0, 7, 0, 7, 0, 7];
      this._matrixPrev[p] = [3, 3, 3, 3];
      this._matrixNow[p] = [3, 3, 3, 3];
      this._matrixNext[p] = [3, 3, 3, 3];
    }

    this._descrambleBuf = new byte[((blockAlign + 3) & ~3) + 64];
  }

  /// <summary>
  /// Derives an <see cref="Atrac3Codec"/> from a Sony OpenMG (OMA) EA3 header's 24-bit
  /// coding-parameters field (codec id 0). OMA ATRAC3 is always two-channel; the decoded
  /// block align is the full stereo frame size.
  /// </summary>
  public static Atrac3Codec FromOmaCodingParams(int codingParams) {
    var (blockAlign, jointStereo, sampleRate) = DecodeOmaParams(codingParams);
    return new Atrac3Codec(sampleRate, channels: 2, blockAlign,
      jointStereo ? JointStereo : Single, scrambled: false);
  }

  /// <summary>
  /// Decodes the OMA codec-parameters u24 (codec id 0) into block align, joint-stereo flag
  /// and sample rate exactly as FFmpeg's <c>libavformat/omadec.c</c>: the field packs (from
  /// MSB) a 3-bit sample-rate index at bits 13–15 (<c>ff_oma_srate_tab × 100</c>), the
  /// joint-stereo flag at bit 17, and a 10-bit frame size at bits 0–9 measured in 8-byte words
  /// (<c>block_align = (params &amp; 0x3FF) * 8</c>). Exposed for tests so the mapping is
  /// self-documenting.
  /// </summary>
  public static (int BlockAlign, bool JointStereo, int SampleRate) DecodeOmaParams(int codingParams) {
    var rateIndex = (codingParams >> 13) & 0x7;
    var sampleRate = rateIndex < OmaSampleRates.Length ? OmaSampleRates[rateIndex] : 0;
    var jointStereo = ((codingParams >> 17) & 1) != 0;
    var blockAlign = (codingParams & 0x3FF) * 8;
    return (blockAlign, jointStereo, sampleRate);
  }

  private static readonly int[] OmaSampleRates = [32000, 44100, 48000, 88200, 96000, 0, 0, 0];

  /// <summary>
  /// Decodes <paramref name="frame"/> (at least <see cref="BlockAlign"/> bytes) to interleaved
  /// signed-16-bit PCM, <c>1024 × channels</c> samples. State is carried for subsequent calls.
  /// </summary>
  public short[] Decode(ReadOnlySpan<byte> frame) {
    if (frame.Length < this.BlockAlign)
      throw new ArgumentException($"ATRAC3 frame too small ({frame.Length} < {this.BlockAlign}).", nameof(frame));

    var outSamples = new float[this.Channels][];
    for (var c = 0; c < this.Channels; ++c)
      outSamples[c] = new float[SamplesPerFrame];

    byte[] databuf;
    int dataOffset;
    if (this.Scrambled) {
      Descramble(frame, this._descrambleBuf, this.BlockAlign);
      databuf = this._descrambleBuf;
      dataOffset = 0;
    } else {
      databuf = frame[..this.BlockAlign].ToArray();
      dataOffset = 0;
    }

    this.DecodeFrame(databuf, dataOffset, outSamples);

    // Interleave to int16 with rounding/clipping (FFmpeg writes FLTP → S16 via the resampler;
    // we clamp the 1/32768-scaled floats back to the 16-bit range directly).
    var interleaved = new short[SamplesPerFrame * this.Channels];
    for (var n = 0; n < SamplesPerFrame; ++n)
      for (var c = 0; c < this.Channels; ++c) {
        var v = (int)MathF.Round(outSamples[c][n] * 32768.0f);
        interleaved[n * this.Channels + c] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
      }
    return interleaved;
  }

  /// <summary>
  /// Decodes a stream of back-to-back frames. A ragged tail shorter than one frame is ignored.
  /// Output is <c>(payload.Length / BlockAlign) * 1024 * channels</c> interleaved int16 samples.
  /// </summary>
  public short[] DecodeStream(ReadOnlySpan<byte> payload) {
    var frames = payload.Length / this.BlockAlign;
    if (frames == 0)
      return [];
    var perFrame = SamplesPerFrame * this.Channels;
    var output = new short[frames * perFrame];
    for (var f = 0; f < frames; ++f) {
      var frame = this.Decode(payload.Slice(f * this.BlockAlign, this.BlockAlign));
      frame.CopyTo(output, f * perFrame);
    }
    return output;
  }

  // ── frame decode ──────────────────────────────────────────────────────────────

  private void DecodeFrame(byte[] databuf, int dataOffset, float[][] outSamples) {
    if (this._codingMode == JointStereo) {
      var jsBlockAlign = this.BlockAlign / this.Channels * 2;
      for (var ch = 0; ch < this.Channels; ch += 2) {
        var jsPair = ch / 2;
        var pairOffset = dataOffset + jsPair * jsBlockAlign;

        var gb = new Atrac3BitReader(databuf, pairOffset, jsBlockAlign);
        this.DecodeChannelSoundUnit(gb, this._units[ch], outSamples[ch], ch, JointStereo);

        // SU2 is stored in reverse byte order — build a reversed copy.
        var reversed = new byte[jsBlockAlign];
        for (var i = 0; i < jsBlockAlign; ++i)
          reversed[i] = pairOffset + jsBlockAlign - 1 - i < databuf.Length
            ? databuf[pairOffset + jsBlockAlign - 1 - i] : (byte)0;

        // Skip the 0xF8 sync codes.
        var ptr = 0;
        for (var i = 4; ptr < jsBlockAlign && reversed[ptr] == 0xF8; ++i, ++ptr) {
          if (i >= jsBlockAlign)
            return;
        }

        var gb2 = new Atrac3BitReader(reversed, ptr, jsBlockAlign - ptr);

        // Shift the weighting-delay history and read the new entries.
        var wd = this._weightingDelay[jsPair];
        wd[0] = wd[2]; wd[1] = wd[3]; wd[2] = wd[4]; wd[3] = wd[5];
        wd[4] = gb2.GetBit();
        wd[5] = gb2.GetBits(3);

        for (var i = 0; i < 4; ++i) {
          this._matrixPrev[jsPair][i] = this._matrixNow[jsPair][i];
          this._matrixNow[jsPair][i] = this._matrixNext[jsPair][i];
          this._matrixNext[jsPair][i] = gb2.GetBits(2);
        }

        this.DecodeChannelSoundUnit(gb2, this._units[ch + 1], outSamples[ch + 1], ch + 1, JointStereo);

        ReverseMatrixing(outSamples[ch], outSamples[ch + 1],
          this._matrixPrev[jsPair], this._matrixNow[jsPair]);
        ChannelWeighting(outSamples[ch], outSamples[ch + 1], this._weightingDelay[jsPair]);
      }
    } else {
      var perChannel = this.BlockAlign / this.Channels;
      for (var i = 0; i < this.Channels; ++i) {
        var gb = new Atrac3BitReader(databuf, dataOffset + i * perChannel, perChannel);
        this.DecodeChannelSoundUnit(gb, this._units[i], outSamples[i], i, this._codingMode);
      }
    }

    // Apply the three-stage iQMF synthesis filter per channel.
    for (var i = 0; i < this.Channels; ++i) {
      var u = this._units[i];
      var s = outSamples[i];
      Iqmf(s, 0, s, 256, 256, s, 0, u.DelayBuf1, this._tempBuf);
      Iqmf(s, 768, s, 512, 256, s, 512, u.DelayBuf2, this._tempBuf);
      Iqmf(s, 0, s, 512, 512, s, 0, u.DelayBuf3, this._tempBuf);
    }
  }

  // ── channel sound unit ──────────────────────────────────────────────────────────

  private void DecodeChannelSoundUnit(Atrac3BitReader gb, ChannelUnit snd, float[] output,
      int channelNum, int codingMode) {
    var gain1 = snd.GainBlock[snd.GcBlkSwitch];
    var gain2 = snd.GainBlock[1 - snd.GcBlkSwitch];

    if (codingMode == JointStereo && (channelNum % 2) == 1)
      gb.GetBits(2);   // JS mono sound-unit id (should be 3)
    else
      gb.GetBits(6);   // sound-unit id (should be 0x28)

    snd.BandsCoded = gb.GetBits(2);

    DecodeGainControl(gb, gain2, snd.BandsCoded);
    snd.NumComponents = DecodeTonalComponents(gb, snd.Components, snd.BandsCoded);
    var numSubbands = DecodeSpectrum(gb, snd.Spectrum);

    var lastTonal = AddTonalComponents(snd.Spectrum, snd.NumComponents, snd.Components);

    var numBands = (Atrac3Tables.SubbandTab[numSubbands + 1] - 1) >> 8;
    if (lastTonal >= 0)
      numBands = Math.Max((lastTonal + 256) >> 8, numBands);

    for (var band = 0; band < 4; ++band) {
      if (band <= numBands)
        Imlt(snd.Spectrum, band * 256, snd.ImdctBuf, (band & 1) != 0);
      else
        Array.Clear(snd.ImdctBuf, 0, MdctSize);

      GainCompensation(snd.ImdctBuf, snd.PrevFrame, band * 256,
        gain1.GBlock[band], gain2.GBlock[band], 256, output, band * 256);
    }

    snd.GcBlkSwitch ^= 1;
  }

  // ── spectrum decode ──────────────────────────────────────────────────────────────

  private static void ReadQuantSpectralCoeffs(Atrac3BitReader gb, int selector, int codingFlag,
      int[] mantissas, int numCodes) {
    if (selector == 1)
      numCodes /= 2;

    if (codingFlag != 0) {
      // Constant-length coding.
      var numBits = Atrac3Tables.ClcLengthTab[selector];
      if (selector > 1) {
        for (var i = 0; i < numCodes; ++i)
          mantissas[i] = numBits != 0 ? gb.GetSignedBits(numBits) : 0;
      } else {
        for (var i = 0; i < numCodes; ++i) {
          var code = numBits != 0 ? gb.GetBits(numBits) : 0; // numBits is 4 here
          mantissas[i * 2] = Atrac3Tables.MantissaClcTab[code >> 2];
          mantissas[i * 2 + 1] = Atrac3Tables.MantissaClcTab[code & 3];
        }
      }
    } else {
      // Variable-length coding.
      var vlc = SpectralVlc[selector - 1];
      if (selector != 1) {
        for (var i = 0; i < numCodes; ++i)
          mantissas[i] = vlc.Decode(gb);
      } else {
        for (var i = 0; i < numCodes; ++i) {
          var huffSymb = vlc.Decode(gb); // already offset by -31 → 0..8
          mantissas[i * 2] = Atrac3Tables.MantissaVlcTab[huffSymb * 2];
          mantissas[i * 2 + 1] = Atrac3Tables.MantissaVlcTab[huffSymb * 2 + 1];
        }
      }
    }
  }

  private static int DecodeSpectrum(Atrac3BitReader gb, float[] output) {
    var subbandVlcIndex = new int[32];
    var sfIndex = new int[32];
    var mantissas = new int[128];

    var numSubbands = gb.GetBits(5);
    var codingMode = gb.GetBit();

    for (var i = 0; i <= numSubbands; ++i)
      subbandVlcIndex[i] = gb.GetBits(3);

    for (var i = 0; i <= numSubbands; ++i)
      if (subbandVlcIndex[i] != 0)
        sfIndex[i] = gb.GetBits(6);

    int i2;
    for (i2 = 0; i2 <= numSubbands; ++i2) {
      var first = Atrac3Tables.SubbandTab[i2];
      var last = Atrac3Tables.SubbandTab[i2 + 1];
      var subbandSize = last - first;

      if (subbandVlcIndex[i2] != 0) {
        ReadQuantSpectralCoeffs(gb, subbandVlcIndex[i2], codingMode, mantissas, subbandSize);
        var scaleFactor = Atrac3Tables.SfTable[sfIndex[i2]] *
          (float)Atrac3Tables.InvMaxQuant[subbandVlcIndex[i2]];
        for (var j = 0; first < last; ++first, ++j)
          output[first] = mantissas[j] * scaleFactor;
      } else {
        Array.Clear(output, first, subbandSize);
      }
    }

    var clearFrom = Atrac3Tables.SubbandTab[i2];
    Array.Clear(output, clearFrom, SamplesPerFrame - clearFrom);
    return numSubbands;
  }

  // ── tonal components ──────────────────────────────────────────────────────────────

  private static int DecodeTonalComponents(Atrac3BitReader gb, TonalComponent[] components, int numBands) {
    var bandFlags = new int[4];
    var mantissa = new int[8];
    var componentCount = 0;

    var nbComponents = gb.GetBits(5);
    if (nbComponents == 0)
      return 0;

    var codingModeSelector = gb.GetBits(2);
    if (codingModeSelector == 2)
      return -1;

    var codingMode = codingModeSelector & 1;

    for (var i = 0; i < nbComponents; ++i) {
      for (var b = 0; b <= numBands; ++b)
        bandFlags[b] = gb.GetBit();

      var codedValuesPerComponent = gb.GetBits(3);
      var quantStepIndex = gb.GetBits(3);
      if (quantStepIndex <= 1)
        return -1;

      if (codingModeSelector == 3)
        codingMode = gb.GetBit();

      for (var b = 0; b < (numBands + 1) * 4; ++b) {
        if (bandFlags[b >> 2] == 0)
          continue;

        var codedComponents = gb.GetBits(3);
        for (var c = 0; c < codedComponents; ++c) {
          if (componentCount >= 64)
            return -1;
          var cmp = components[componentCount];

          var sf = gb.GetBits(6);
          cmp.Pos = b * 64 + gb.GetBits(6);

          var maxCodedValues = SamplesPerFrame - cmp.Pos;
          var codedValues = Math.Min(maxCodedValues, codedValuesPerComponent + 1);

          var scaleFactor = Atrac3Tables.SfTable[sf] *
            (float)Atrac3Tables.InvMaxQuant[quantStepIndex];

          ReadQuantSpectralCoeffs(gb, quantStepIndex, codingMode, mantissa, codedValues);

          cmp.NumCoefs = codedValues;
          for (var m = 0; m < codedValues; ++m)
            cmp.Coef[m] = mantissa[m] * scaleFactor;

          ++componentCount;
        }
      }
    }

    return componentCount;
  }

  private static int AddTonalComponents(float[] spectrum, int numComponents, TonalComponent[] components) {
    var lastPos = -1;
    for (var i = 0; i < numComponents; ++i) {
      var cmp = components[i];
      lastPos = Math.Max(cmp.Pos + cmp.NumCoefs, lastPos);
      for (var j = 0; j < cmp.NumCoefs; ++j)
        spectrum[cmp.Pos + j] += cmp.Coef[j];
    }
    return lastPos;
  }

  // ── gain control ──────────────────────────────────────────────────────────────

  private static void DecodeGainControl(Atrac3BitReader gb, GainBlock block, int numBands) {
    var gain = block.GBlock;
    int b;
    for (b = 0; b <= numBands; ++b) {
      var g = gain[b];
      g.NumPoints = gb.GetBits(3);
      for (var j = 0; j < g.NumPoints; ++j) {
        g.LevCode[j] = gb.GetBits(4);
        g.LocCode[j] = gb.GetBits(5);
      }
    }
    for (; b < 4; ++b)
      gain[b].NumPoints = 0;
  }

  private static void GainCompensation(float[] inBuf, float[] prev, int prevOffset,
      AtracGainInfo gcNow, AtracGainInfo gcNext, int numSamples, float[] outBuf, int outOffset) {
    var gcScale = gcNext.NumPoints != 0 ? GainTab1[gcNext.LevCode[0]] : 1.0f;

    if (gcNow.NumPoints == 0) {
      for (var pos = 0; pos < numSamples; ++pos)
        outBuf[outOffset + pos] = inBuf[pos] * gcScale + prev[prevOffset + pos];
    } else {
      var pos = 0;
      for (var i = 0; i < gcNow.NumPoints; ++i) {
        var lastpos = gcNow.LocCode[i] << LocScale;
        var lev = GainTab1[gcNow.LevCode[i]];
        var nextLev = i + 1 < gcNow.NumPoints ? gcNow.LevCode[i + 1] : Id2ExpOffset;
        var gainInc = GainTab2[nextLev - gcNow.LevCode[i] + 15];

        for (; pos < lastpos; ++pos)
          outBuf[outOffset + pos] = (inBuf[pos] * gcScale + prev[prevOffset + pos]) * lev;
        for (; pos < lastpos + LocSize; ++pos) {
          outBuf[outOffset + pos] = (inBuf[pos] * gcScale + prev[prevOffset + pos]) * lev;
          lev *= gainInc;
        }
      }
      for (; pos < numSamples; ++pos)
        outBuf[outOffset + pos] = inBuf[pos] * gcScale + prev[prevOffset + pos];
    }

    // Save the overlapping part into the previous-frame buffer.
    Array.Copy(inBuf, numSamples, prev, prevOffset, numSamples);
  }

  // ── IMDCT ──────────────────────────────────────────────────────────────

  /// <summary>
  /// 512-point inverse MDCT (no overlapping; FFmpeg's <c>imlt</c>) with the odd-band reversal
  /// caused by the QMF reverse spectra, followed by windowing. Direct O(n²) full IMDCT — the
  /// transform <c>av_tx_init(AV_TX_FLOAT_MDCT, ..., 256, scale, AV_TX_FULL_IMDCT)</c> maps to
  /// <c>out[n] = scale·Σ_k in[k]·cos((π/N)(n+½+N/2)(k+½))</c> for N=256, n∈[0,512).
  /// </summary>
  private static void Imlt(float[] input, int inputOffset, float[] output, bool oddBand) {
    const int n = 256;

    // Working copy so the spectrum buffer is not mutated by the odd-band swap.
    var coeffs = new float[n];
    Array.Copy(input, inputOffset, coeffs, 0, n);
    if (oddBand)
      for (var i = 0; i < 128; ++i)
        (coeffs[i], coeffs[255 - i]) = (coeffs[255 - i], coeffs[i]);

    var factor = Math.PI / n;
    for (var p = 0; p < MdctSize; ++p) {
      double sum = 0.0;
      var a = factor * (p + 0.5 + n / 2.0);
      for (var k = 0; k < n; ++k)
        sum += coeffs[k] * Math.Cos(a * (k + 0.5));
      output[p] = (float)(MdctScale * sum) * Atrac3Tables.MdctWindow[p];
    }
  }

  // ── joint stereo ──────────────────────────────────────────────────────────────

  private static float Interpolate(float old, float fresh, int nsample)
    => old + nsample * 0.125f * (fresh - old);

  private static void ReverseMatrixing(float[] su1, float[] su2, int[] prevCode, int[] currCode) {
    for (int i = 0, band = 0; band < 4 * 256; band += 256, ++i) {
      var s1 = prevCode[i];
      var s2 = currCode[i];
      var nsample = band;

      if (s1 != s2) {
        var mc1L = (float)Atrac3Tables.MatrixCoeffs[s1 * 2];
        var mc1R = (float)Atrac3Tables.MatrixCoeffs[s1 * 2 + 1];
        var mc2L = (float)Atrac3Tables.MatrixCoeffs[s2 * 2];
        var mc2R = (float)Atrac3Tables.MatrixCoeffs[s2 * 2 + 1];
        for (; nsample < band + 8; ++nsample) {
          var c1 = su1[nsample];
          var c2 = su2[nsample];
          c2 = c1 * Interpolate(mc1L, mc2L, nsample - band) +
               c2 * Interpolate(mc1R, mc2R, nsample - band);
          su1[nsample] = c2;
          su2[nsample] = c1 * 2.0f - c2;
        }
      }

      switch (s2) {
        case 0:
          for (; nsample < band + 256; ++nsample) {
            var c1 = su1[nsample];
            var c2 = su2[nsample];
            su1[nsample] = c2 * 2.0f;
            su2[nsample] = (c1 - c2) * 2.0f;
          }
          break;
        case 1:
          for (; nsample < band + 256; ++nsample) {
            var c1 = su1[nsample];
            var c2 = su2[nsample];
            su1[nsample] = (c1 + c2) * 2.0f;
            su2[nsample] = c2 * -2.0f;
          }
          break;
        default: // 2, 3
          for (; nsample < band + 256; ++nsample) {
            var c1 = su1[nsample];
            var c2 = su2[nsample];
            su1[nsample] = c1 + c2;
            su2[nsample] = c1 - c2;
          }
          break;
      }
    }
  }

  private static void GetChannelWeights(int index, int flag, out float w0, out float w1) {
    if (index == 7) {
      w0 = 1.0f;
      w1 = 1.0f;
    } else {
      w0 = (index & 7) / 7.0f;
      w1 = (float)Math.Sqrt(2 - w0 * w0);
      if (flag != 0)
        (w0, w1) = (w1, w0);
    }
  }

  private static void ChannelWeighting(float[] su1, float[] su2, int[] p3) {
    if (p3[1] == 7 && p3[3] == 7)
      return;

    GetChannelWeights(p3[1], p3[0], out var w00, out var w01);
    GetChannelWeights(p3[3], p3[2], out var w10, out var w11);

    for (var band = 256; band < 4 * 256; band += 256) {
      var nsample = band;
      for (; nsample < band + 8; ++nsample) {
        su1[nsample] *= Interpolate(w00, w01, nsample - band);
        su2[nsample] *= Interpolate(w10, w11, nsample - band);
      }
      for (; nsample < band + 256; ++nsample) {
        su1[nsample] *= w01;
        su2[nsample] *= w11;
      }
    }
  }

  // ── inverse QMF ──────────────────────────────────────────────────────────────

  private static void Iqmf(float[] inlo, int inloOff, float[] inhi, int inhiOff,
      int nIn, float[] pOut, int pOutOff, float[] delayBuf, float[] temp) {
    Array.Copy(delayBuf, 0, temp, 0, 46);

    // p3 = temp + 46.
    for (var i = 0; i < nIn; i += 2) {
      var lo0 = inlo[inloOff + i];
      var hi0 = inhi[inhiOff + i];
      var lo1 = inlo[inloOff + i + 1];
      var hi1 = inhi[inhiOff + i + 1];
      temp[46 + 2 * i + 0] = lo0 + hi0;
      temp[46 + 2 * i + 1] = lo0 - hi0;
      temp[46 + 2 * i + 2] = lo1 + hi1;
      temp[46 + 2 * i + 3] = lo1 - hi1;
    }

    var p1 = 0;
    var outPos = pOutOff;
    for (var j = nIn; j != 0; --j) {
      float s1 = 0.0f, s2 = 0.0f;
      for (var i = 0; i < 48; i += 2) {
        s1 += temp[p1 + i] * Atrac3Tables.QmfWindow[i];
        s2 += temp[p1 + i + 1] * Atrac3Tables.QmfWindow[i + 1];
      }
      pOut[outPos] = s2;
      pOut[outPos + 1] = s1;
      p1 += 2;
      outPos += 2;
    }

    Array.Copy(temp, nIn * 2, delayBuf, 0, 46);
  }

  // ── descrambling ──────────────────────────────────────────────────────────────

  /// <summary>
  /// RM-container descrambling (FFmpeg <c>decode_bytes</c>): XOR each 32-bit big-endian word
  /// with the key 0x537F6103. We always read 4-byte-aligned input (offset 0), so no key
  /// rotation is needed.
  /// </summary>
  private static void Descramble(ReadOnlySpan<byte> input, byte[] outBuf, int bytes) {
    const uint key = 0x537F6103u;
    var words = (bytes + 3) / 4;
    for (var i = 0; i < words; ++i) {
      var b0 = i * 4 + 0 < input.Length ? input[i * 4 + 0] : (byte)0;
      var b1 = i * 4 + 1 < input.Length ? input[i * 4 + 1] : (byte)0;
      var b2 = i * 4 + 2 < input.Length ? input[i * 4 + 2] : (byte)0;
      var b3 = i * 4 + 3 < input.Length ? input[i * 4 + 3] : (byte)0;
      var word = ((uint)b0 << 24) | ((uint)b1 << 16) | ((uint)b2 << 8) | b3;
      word ^= key;
      outBuf[i * 4 + 0] = (byte)(word >> 24);
      outBuf[i * 4 + 1] = (byte)(word >> 16);
      outBuf[i * 4 + 2] = (byte)(word >> 8);
      outBuf[i * 4 + 3] = (byte)word;
    }
  }

  // ── static table builders ──────────────────────────────────────────────────────────────

  private static Atrac3Vlc[] BuildSpectralVlc() {
    var vlcs = new Atrac3Vlc[7];
    var offset = 0;
    for (var i = 0; i < 7; ++i) {
      var size = Atrac3Tables.HuffTabSizes[i];
      vlcs[i] = new Atrac3Vlc(Atrac3Tables.HuffTabs, offset, size, Atrac3Tables.HuffSymbolOffset);
      offset += size;
    }
    return vlcs;
  }

  private static float[] BuildGainTab1() {
    var t = new float[16];
    for (var i = 0; i < 16; ++i)
      t[i] = (float)Math.Pow(2.0, Id2ExpOffset - i);
    return t;
  }

  private static float[] BuildGainTab2() {
    var t = new float[31];
    for (var i = -15; i < 16; ++i)
      t[i + 15] = (float)Math.Pow(2.0, -1.0 / LocSize * i);
    return t;
  }

  // ── per-stream state types ──────────────────────────────────────────────────────────────

  private sealed class AtracGainInfo {
    public int NumPoints;
    public readonly int[] LevCode = new int[7];
    public readonly int[] LocCode = new int[7];
  }

  private sealed class GainBlock {
    public readonly AtracGainInfo[] GBlock = [new(), new(), new(), new()];
  }

  private sealed class TonalComponent {
    public int Pos;
    public int NumCoefs;
    public readonly float[] Coef = new float[8];
  }

  private sealed class ChannelUnit {
    public int BandsCoded;
    public int NumComponents;
    public int GcBlkSwitch;
    public readonly float[] PrevFrame = new float[SamplesPerFrame];
    public readonly TonalComponent[] Components = BuildComponents();
    public readonly GainBlock[] GainBlock = [new(), new()];
    public readonly float[] Spectrum = new float[SamplesPerFrame];
    public readonly float[] ImdctBuf = new float[SamplesPerFrame];
    public readonly float[] DelayBuf1 = new float[46];
    public readonly float[] DelayBuf2 = new float[46];
    public readonly float[] DelayBuf3 = new float[46];

    private static TonalComponent[] BuildComponents() {
      var c = new TonalComponent[64];
      for (var i = 0; i < 64; ++i)
        c[i] = new TonalComponent();
      return c;
    }
  }
}
