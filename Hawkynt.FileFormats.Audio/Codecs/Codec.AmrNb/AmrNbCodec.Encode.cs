namespace Codec.AmrNb;

/// <summary>Controls the managed AMR-NB encoder.</summary>
/// <param name="Mode">One of the eight active AMR-NB speech modes.</param>
/// <param name="EnableDtx">Emit NO_DATA frames for digital silence / very-low-energy input.</param>
/// <param name="PadFinalFrame">Pad an incomplete 160-sample final frame with its last sample.</param>
public sealed record AmrNbEncoderOptions(
  AmrNbMode Mode = AmrNbMode.Mr122,
  bool EnableDtx = false,
  bool PadFinalFrame = true
);

/// <summary>
/// Represents an amr nb codec.
/// </summary>
public static partial class AmrNbCodec {
  private const float LsfResidualHz = SampleRate / 32768f;
  private const float Mr122PredictionFactor = 0.65f;
  private const int DefaultPitchLag = 40;

  private static readonly float[] DefaultLsf = [
    300f, 600f, 900f, 1200f, 1550f, 1900f, 2250f, 2600f, 3050f, 3500f,
  ];

  private sealed class EncoderState {
    public readonly int[] PreviousLsfResidual = new int[AmrNbData.LpOrder];
    public readonly short[] SampleHistory = new short[AmrNbData.PitchDelayMax];
    public readonly float[] PredictionError = [-14f, -14f, -14f, -14f];
    public int PreviousPitchLag = DefaultPitchLag;

    public void PushFrame(ReadOnlySpan<short> frame)
      => frame[^AmrNbData.PitchDelayMax..].CopyTo(this.SampleHistory);
  }

  /// <summary>
  /// Encodes mono 8 kHz PCM16 to the AMR-NB IF1/storage byte layout consumed by <see cref="Decode"/>.
  /// The encoder performs signal-derived LPC/LSF analysis, searches the normative split-LSF
  /// codebooks, inverts the decoder's mode-specific fractional-pitch mapping, performs algebraic
  /// fixed-codebook analysis by synthesis, and selects the standard gain VQ tables while carrying
  /// the gain/LSF/pitch prediction state between frames.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm, AmrNbEncoderOptions? options = null) {
    options ??= new AmrNbEncoderOptions();
    if (options.Mode is < AmrNbMode.Mr475 or > AmrNbMode.Mr122)
      throw new ArgumentOutOfRangeException(nameof(options), "AMR-NB encoding requires one of the eight active speech modes (MR475..MR122).");
    if (pcm.IsEmpty)
      return [];
    if (!options.PadFinalFrame && pcm.Length % SamplesPerFrame != 0)
      throw new ArgumentException($"AMR-NB PCM must contain whole {SamplesPerFrame}-sample frames when padding is disabled.", nameof(pcm));

    var frameCount = (pcm.Length + SamplesPerFrame - 1) / SamplesPerFrame;
    using var output = new MemoryStream(frameCount * (1 + AmrNbData.PayloadBytes[(int)options.Mode]));
    var state = new EncoderState();
    Span<short> frame = stackalloc short[SamplesPerFrame];

    for (var f = 0; f < frameCount; ++f) {
      var offset = f * SamplesPerFrame;
      var count = Math.Min(SamplesPerFrame, pcm.Length - offset);
      pcm.Slice(offset, count).CopyTo(frame);
      if (count < SamplesPerFrame)
        frame[count..].Fill(count == 0 ? (short)0 : frame[count - 1]);

      if (options.EnableDtx && RootMeanSquare(frame) < 8f) {
        output.WriteByte((byte)(((int)AmrNbMode.NoData << 3) | 0x04));
        state.PushFrame(frame);
        continue;
      }

      var words = BuildParameters(frame, options.Mode, state);
      var payload = Pack(words, options.Mode);
      output.WriteByte((byte)(((int)options.Mode << 3) | 0x04));
      output.Write(payload);
      state.PushFrame(frame);
    }

    return output.ToArray();
  }

  private static int[] BuildParameters(ReadOnlySpan<short> frame, AmrNbMode mode, EncoderState state) {
    var words = new int[AmrNbFrame.WordCount];
    var widths = GetFieldWidths(mode);
    QuantizeLsf(frame, mode, state, words, widths);

    Span<float> targetPitchGain = stackalloc float[AmrNbData.SubframeCount];
    Span<float> targetFixedGain = stackalloc float[AmrNbData.SubframeCount];
    Span<float> innovationEnergy = stackalloc float[AmrNbData.SubframeCount];
    Span<float> residual = stackalloc float[AmrNbData.SubframeSize];

    for (var sub = 0; sub < AmrNbData.SubframeCount; ++sub) {
      var baseWord = 5 + sub * 13;
      var subStart = sub * AmrNbData.SubframeSize;
      var targetLag = FindPitchLag(frame, state.SampleHistory, subStart, state.PreviousPitchLag);
      var pitchIndex = SelectPitchIndex(mode, sub, widths[baseWord], targetLag, state.PreviousPitchLag,
        out var decodedLag, out _);
      words[baseWord] = pitchIndex;
      state.PreviousPitchLag = decodedLag;

      var pitchGain = EstimatePitchGain(frame, state.SampleHistory, subStart, decodedLag);
      targetPitchGain[sub] = pitchGain;
      for (var i = 0; i < AmrNbData.SubframeSize; ++i) {
        var source = frame[subStart + i];
        var delayed = GetSignalSample(frame, state.SampleHistory, subStart + i - decodedLag);
        residual[i] = source - pitchGain * delayed;
      }

      EncodeInnovation(residual, mode, sub, baseWord, words, widths);
      innovationEnergy[sub] = InnovationMeanEnergy(mode, sub, words);
      targetFixedGain[sub] = MathF.Max(1f, RootMeanSquare(residual) * 0.5f);
    }

    QuantizeGains(mode, words, widths, targetPitchGain, targetFixedGain, innovationEnergy, state);
    return words;
  }

  // ---------------------------------------------------------------------------------------------
  // LPC / LSF analysis and normative split-codebook search
  // ---------------------------------------------------------------------------------------------

  private static void QuantizeLsf(
    ReadOnlySpan<short> frame,
    AmrNbMode mode,
    EncoderState state,
    Span<int> words,
    ReadOnlySpan<int> widths) {
    Span<float> endLsf = stackalloc float[AmrNbData.LpOrder];
    AnalyzeLsf(frame, endLsf);

    if (mode == AmrNbMode.Mr122) {
      Span<float> midLsf = stackalloc float[AmrNbData.LpOrder];
      AnalyzeLsf(frame[..(SamplesPerFrame / 2)], midLsf);
      QuantizeMr122Lsf(midLsf, endLsf, state, words, widths);
      return;
    }

    Span<double> target = stackalloc double[AmrNbData.LpOrder];
    for (var i = 0; i < target.Length; ++i)
      target[i] = (endLsf[i] - AmrNbTables.Lsf3Mean[i]) / LsfResidualHz
                  - state.PreviousLsfResidual[i] * AmrNbTables.PredFac[i];

    var table1 = mode == AmrNbMode.Mr795 ? AmrNbTables.Lsf3_1Mode7k95 : AmrNbTables.Lsf3_1;
    var table3 = mode <= AmrNbMode.Mr515 ? AmrNbTables.Lsf3_3Mode5k15 : AmrNbTables.Lsf3_3;

    var i1 = FindNearestSplit(table1, target, 0, 3, widths[0], 1);
    var stride2 = mode <= AmrNbMode.Mr515 ? 2 : 1;
    var i2 = FindNearestSplit(AmrNbTables.Lsf3_2, target, 3, 3, widths[1], stride2);
    var i3 = FindNearestSplit(table3, target, 6, 4, widths[2], 1);

    words[0] = i1;
    words[1] = i2;
    words[2] = i3;

    CopySplit(table1[i1], state.PreviousLsfResidual, 0, 3);
    CopySplit(AmrNbTables.Lsf3_2[i2 * stride2], state.PreviousLsfResidual, 3, 3);
    CopySplit(table3[i3], state.PreviousLsfResidual, 6, 4);
  }

  private static void QuantizeMr122Lsf(
    ReadOnlySpan<float> midLsf,
    ReadOnlySpan<float> endLsf,
    EncoderState state,
    Span<int> words,
    ReadOnlySpan<int> widths) {
    Span<double> midTarget = stackalloc double[AmrNbData.LpOrder];
    Span<double> endTarget = stackalloc double[AmrNbData.LpOrder];
    for (var i = 0; i < AmrNbData.LpOrder; ++i) {
      var predictedHz = AmrNbTables.Lsf5Mean[i]
                        + state.PreviousLsfResidual[i] * LsfResidualHz * Mr122PredictionFactor;
      midTarget[i] = (midLsf[i] - predictedHz) / LsfResidualHz;
      endTarget[i] = (endLsf[i] - predictedHz) / LsfResidualHz;
    }

    ReadOnlySpan<int[][]> tables = [
      AmrNbTables.Lsf5_1,
      AmrNbTables.Lsf5_2,
      AmrNbTables.Lsf5_3,
      AmrNbTables.Lsf5_4,
      AmrNbTables.Lsf5_5,
    ];

    for (var split = 0; split < 5; ++split) {
      var offset = split * 2;
      var signed = split == 2;
      var index = FindNearestMr122Split(
        tables[split], midTarget, endTarget, offset, widths[split], signed,
        out var sign, out _, out _, out var end0, out var end1);
      words[split] = signed ? (index << 1) | sign : index;
      state.PreviousLsfResidual[offset] = end0;
      state.PreviousLsfResidual[offset + 1] = end1;
    }
  }

  private static int FindNearestSplit(
    int[][] table,
    ReadOnlySpan<double> target,
    int targetOffset,
    int dimensions,
    int wordBits,
    int tableStride) {
    var candidates = Math.Min(CandidateCount(wordBits), (table.Length + tableStride - 1) / tableStride);
    var bestWord = 0;
    var bestError = double.PositiveInfinity;
    for (var word = 0; word < candidates; ++word) {
      var entry = table[word * tableStride];
      double error = 0;
      for (var d = 0; d < dimensions; ++d) {
        var delta = target[targetOffset + d] - entry[d];
        error += delta * delta;
      }
      if (error >= bestError)
        continue;
      bestError = error;
      bestWord = word;
    }
    return bestWord;
  }

  private static int FindNearestMr122Split(
    int[][] table,
    ReadOnlySpan<double> midTarget,
    ReadOnlySpan<double> endTarget,
    int offset,
    int wordBits,
    bool signed,
    out int bestSign,
    out int bestMid0,
    out int bestMid1,
    out int bestEnd0,
    out int bestEnd1) {
    var indexBits = signed ? Math.Max(0, wordBits - 1) : wordBits;
    var candidates = Math.Min(CandidateCount(indexBits), table.Length);
    var bestIndex = 0;
    bestSign = 0;
    var bestError = double.PositiveInfinity;

    for (var index = 0; index < candidates; ++index) {
      var entry = table[index];
      var signCount = signed ? 2 : 1;
      for (var sign = 0; sign < signCount; ++sign) {
        var factor = sign == 0 ? 1 : -1;
        var m0 = factor * entry[0];
        var m1 = factor * entry[1];
        var e0 = factor * entry[2];
        var e1 = factor * entry[3];
        var error = Square(midTarget[offset] - m0)
                    + Square(midTarget[offset + 1] - m1)
                    + Square(endTarget[offset] - e0)
                    + Square(endTarget[offset + 1] - e1);
        if (error >= bestError)
          continue;
        bestError = error;
        bestIndex = index;
        bestSign = sign;
      }
    }

    var best = table[bestIndex];
    var bestFactor = bestSign == 0 ? 1 : -1;
    bestMid0 = bestFactor * best[0];
    bestMid1 = bestFactor * best[1];
    bestEnd0 = bestFactor * best[2];
    bestEnd1 = bestFactor * best[3];
    return bestIndex;
  }

  private static void CopySplit(ReadOnlySpan<int> source, Span<int> destination, int offset, int count) {
    for (var i = 0; i < count; ++i)
      destination[offset + i] = source[i];
  }

  private static void AnalyzeLsf(ReadOnlySpan<short> pcm, Span<float> lsf) {
    Span<double> windowed = stackalloc double[SamplesPerFrame];
    Span<double> correlation = stackalloc double[AmrNbData.LpOrder + 1];
    Span<double> lpc = stackalloc double[AmrNbData.LpOrder + 1];
    Span<double> previous = stackalloc double[AmrNbData.LpOrder + 1];

    if (pcm.IsEmpty) {
      DefaultLsf.CopyTo(lsf);
      return;
    }

    for (var i = 0; i < pcm.Length; ++i) {
      var window = pcm.Length == 1
        ? 1d
        : 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (pcm.Length - 1));
      windowed[i] = pcm[i] * window;
    }

    for (var lag = 0; lag <= AmrNbData.LpOrder; ++lag) {
      double sum = 0;
      for (var i = lag; i < pcm.Length; ++i)
        sum += windowed[i] * windowed[i - lag];
      correlation[lag] = sum;
    }

    if (correlation[0] < 1) {
      DefaultLsf.CopyTo(lsf);
      return;
    }

    lpc[0] = 1;
    var error = correlation[0] * 1.0001;
    for (var order = 1; order <= AmrNbData.LpOrder; ++order) {
      var reflectionNumerator = correlation[order];
      for (var j = 1; j < order; ++j)
        reflectionNumerator += lpc[j] * correlation[order - j];
      var reflection = Math.Clamp(-reflectionNumerator / Math.Max(error, 1e-9), -0.98, 0.98);
      lpc.CopyTo(previous);
      lpc[order] = reflection;
      for (var j = 1; j < order; ++j)
        lpc[j] = previous[j] + reflection * previous[order - j];
      error *= Math.Max(1e-6, 1 - reflection * reflection);
    }

    Span<double> symmetricRoots = stackalloc double[AmrNbData.LpOrder / 2];
    Span<double> antisymmetricRoots = stackalloc double[AmrNbData.LpOrder / 2];
    if (!FindLspRoots(lpc, symmetric: true, symmetricRoots)
        || !FindLspRoots(lpc, symmetric: false, antisymmetricRoots)) {
      DefaultLsf.CopyTo(lsf);
      return;
    }

    Span<double> roots = stackalloc double[AmrNbData.LpOrder];
    var p = 0;
    var q = 0;
    for (var i = 0; i < roots.Length; ++i) {
      if (q >= antisymmetricRoots.Length
          || (p < symmetricRoots.Length && symmetricRoots[p] <= antisymmetricRoots[q]))
        roots[i] = symmetricRoots[p++];
      else
        roots[i] = antisymmetricRoots[q++];
    }

    var previousHz = 50f;
    for (var i = 0; i < lsf.Length; ++i) {
      var hz = (float)(roots[i] * SampleRate / (2 * Math.PI));
      var upper = 3900f - (lsf.Length - 1 - i) * 50f;
      hz = Math.Clamp(hz, previousHz + 50f, upper);
      lsf[i] = hz;
      previousHz = hz;
    }
  }

  private static bool FindLspRoots(ReadOnlySpan<double> lpc, bool symmetric, Span<double> roots) {
    const int steps = 4096;
    const double epsilon = 1e-5;
    var previousX = epsilon;
    var previousY = EvaluateLspPolynomial(lpc, previousX, symmetric);
    var count = 0;

    for (var step = 1; step <= steps && count < roots.Length; ++step) {
      var x = epsilon + (Math.PI - 2 * epsilon) * step / steps;
      var y = EvaluateLspPolynomial(lpc, x, symmetric);
      if (Math.Sign(previousY) == Math.Sign(y) && Math.Abs(y) > 1e-12) {
        previousX = x;
        previousY = y;
        continue;
      }

      var lo = previousX;
      var hi = x;
      var loY = previousY;
      for (var iteration = 0; iteration < 36; ++iteration) {
        var mid = (lo + hi) * 0.5;
        var midY = EvaluateLspPolynomial(lpc, mid, symmetric);
        if (Math.Sign(loY) == Math.Sign(midY)) {
          lo = mid;
          loY = midY;
        } else {
          hi = mid;
        }
      }
      roots[count++] = (lo + hi) * 0.5;
      previousX = x;
      previousY = y;
    }

    return count == roots.Length;
  }

  private static double EvaluateLspPolynomial(ReadOnlySpan<double> lpc, double omega, bool symmetric) {
    double real = 0;
    double imaginary = 0;
    for (var k = 0; k < lpc.Length; ++k) {
      real += lpc[k] * Math.Cos(k * omega);
      imaginary -= lpc[k] * Math.Sin(k * omega);
    }
    var phase = (lpc.Length * omega) * 0.5;
    var rotatedReal = real * Math.Cos(phase) - imaginary * Math.Sin(phase);
    var rotatedImaginary = real * Math.Sin(phase) + imaginary * Math.Cos(phase);
    return symmetric ? rotatedReal : rotatedImaginary;
  }

  // ---------------------------------------------------------------------------------------------
  // Adaptive-codebook pitch search
  // ---------------------------------------------------------------------------------------------

  private static int FindPitchLag(
    ReadOnlySpan<short> frame,
    ReadOnlySpan<short> history,
    int subStart,
    int fallbackLag) {
    var bestLag = fallbackLag;
    var bestScore = double.NegativeInfinity;
    for (var lag = AmrNbData.PitchDelayMin; lag <= AmrNbData.PitchDelayMax; ++lag) {
      double correlation = 0;
      double currentEnergy = 1;
      double delayedEnergy = 1;
      for (var i = 0; i < AmrNbData.SubframeSize; ++i) {
        var current = (double)frame[subStart + i];
        var delayed = GetSignalSample(frame, history, subStart + i - lag);
        correlation += current * delayed;
        currentEnergy += current * current;
        delayedEnergy += delayed * delayed;
      }
      var score = correlation / Math.Sqrt(currentEnergy * delayedEnergy);
      if (score <= bestScore)
        continue;
      bestScore = score;
      bestLag = lag;
    }
    return bestLag;
  }

  private static int SelectPitchIndex(
    AmrNbMode mode,
    int subframe,
    int fieldBits,
    int targetLag,
    int previousLag,
    out int decodedLag,
    out int decodedFractionSixths) {
    var count = CandidateCount(fieldBits);
    var bestIndex = 0;
    decodedLag = previousLag;
    decodedFractionSixths = 0;
    var bestError = double.PositiveInfinity;

    for (var index = 0; index < count; ++index) {
      DecodePitchIndex(mode, subframe, index, previousLag, out var lag, out var fractionSixths);
      if (lag is < 18 or > AmrNbData.PitchDelayMax)
        continue;
      var error = Math.Abs((lag + fractionSixths / 6d) - targetLag) + Math.Abs(fractionSixths) * 1e-4;
      if (error >= bestError)
        continue;
      bestError = error;
      bestIndex = index;
      decodedLag = lag;
      decodedFractionSixths = fractionSixths;
    }

    return bestIndex;
  }

  private static void DecodePitchIndex(
    AmrNbMode mode,
    int subframe,
    int index,
    int previousLag,
    out int lag,
    out int fractionSixths) {
    if (mode == AmrNbMode.Mr122) {
      if (subframe is 0 or 2) {
        if (index < 463) {
          lag = (index + 107) * 10923 >> 16;
          fractionSixths = index - lag * 6 + 105;
        } else {
          lag = index - 368;
          fractionSixths = 0;
        }
      } else {
        lag = ((index + 5) * 10923 >> 16) - 1;
        fractionSixths = index - lag * 6 - 3;
        lag += Math.Clamp(previousLag - 5, 18, AmrNbData.PitchDelayMax - 9);
      }
      return;
    }

    var decoded = index;
    var thirdAsFirst = mode is not (AmrNbMode.Mr475 or AmrNbMode.Mr515);
    var resolution = mode <= AmrNbMode.Mr67 ? 4 : mode == AmrNbMode.Mr795 ? 5 : 6;
    if (subframe == 0 || (subframe == 2 && thirdAsFirst)) {
      decoded = decoded < 197 ? decoded + 59 : 3 * decoded - 335;
    } else if (resolution == 4) {
      var min = Math.Clamp(previousLag - 5, AmrNbData.PitchDelayMin, AmrNbData.PitchDelayMax - 9);
      decoded = decoded switch {
        < 4 => 3 * (decoded + min) + 1,
        < 12 => decoded + 3 * min + 7,
        _ => 3 * (decoded + min - 6) + 1,
      };
    } else {
      --decoded;
      decoded += 3 * (resolution == 5
        ? Math.Clamp(previousLag - 10, AmrNbData.PitchDelayMin, AmrNbData.PitchDelayMax - 19)
        : Math.Clamp(previousLag - 5, AmrNbData.PitchDelayMin, AmrNbData.PitchDelayMax - 9));
    }

    lag = decoded * 10923 >> 15;
    fractionSixths = 2 * (decoded - 3 * lag - 1);
  }

  private static float EstimatePitchGain(
    ReadOnlySpan<short> frame,
    ReadOnlySpan<short> history,
    int subStart,
    int lag) {
    double correlation = 0;
    double delayedEnergy = 1;
    for (var i = 0; i < AmrNbData.SubframeSize; ++i) {
      var current = frame[subStart + i];
      var delayed = GetSignalSample(frame, history, subStart + i - lag);
      correlation += current * delayed;
      delayedEnergy += delayed * delayed;
    }
    return (float)Math.Clamp(correlation / delayedEnergy, 0, 1.2);
  }

  private static float GetSignalSample(ReadOnlySpan<short> frame, ReadOnlySpan<short> history, int index) {
    if (index >= 0)
      return index < frame.Length ? frame[index] : 0;
    var historyIndex = history.Length + index;
    return historyIndex >= 0 ? history[historyIndex] : 0;
  }

  // ---------------------------------------------------------------------------------------------
  // Algebraic fixed-codebook analysis
  // ---------------------------------------------------------------------------------------------

  private static void EncodeInnovation(
    ReadOnlySpan<float> residual,
    AmrNbMode mode,
    int subframe,
    int baseWord,
    Span<int> words,
    ReadOnlySpan<int> widths) {
    if (mode == AmrNbMode.Mr122) {
      EncodeMr122Innovation(residual, baseWord, words);
      return;
    }
    if (mode == AmrNbMode.Mr102) {
      EncodeMr102Innovation(residual, baseWord, words, widths);
      return;
    }
    EncodeLowRateInnovation(residual, mode, subframe, baseWord, words, widths);
  }

  private static void EncodeLowRateInnovation(
    ReadOnlySpan<float> residual,
    AmrNbMode mode,
    int subframe,
    int baseWord,
    Span<int> words,
    ReadOnlySpan<int> widths) {
    var indexWord = baseWord + 3;
    var signWord = baseWord + 4;
    var candidates = CandidateCount(widths[indexWord]);
    Span<int> positions = stackalloc int[4];
    var bestIndex = 0;
    var bestSign = 0;
    var bestScore = double.NegativeInfinity;

    for (var index = 0; index < candidates; ++index) {
      var count = DecodeLowRatePositions(mode, subframe, index, positions);
      var signMask = 0;
      double correlation = 0;
      for (var i = 0; i < count; ++i) {
        var sample = residual[positions[i]];
        if (sample >= 0)
          signMask |= 1 << i;
        correlation += Math.Abs(sample);
      }
      var score = correlation * correlation / Math.Max(1, count);
      if (score <= bestScore)
        continue;
      bestScore = score;
      bestIndex = index;
      bestSign = signMask;
    }

    words[indexWord] = bestIndex;
    words[signWord] = bestSign & (int)FieldMask(widths[signWord]);
  }

  private static int DecodeLowRatePositions(AmrNbMode mode, int subframe, int fixedIndex, Span<int> positions) {
    if (mode <= AmrNbMode.Mr515) {
      var subset = ((fixedIndex >> 3) & 8) + (subframe << 1);
      positions[0] = (fixedIndex & 7) * 5 + AmrNbTables.TrackPosition[subset];
      positions[1] = ((fixedIndex >> 3) & 7) * 5 + AmrNbTables.TrackPosition[subset + 1];
      return 2;
    }
    if (mode == AmrNbMode.Mr59) {
      var subset = ((fixedIndex & 1) << 1) + 1;
      positions[0] = ((fixedIndex >> 1) & 7) * 5 + subset;
      subset = (fixedIndex >> 4) & 3;
      positions[1] = ((fixedIndex >> 6) & 7) * 5 + subset + (subset == 3 ? 1 : 0);
      return positions[0] == positions[1] ? 1 : 2;
    }
    if (mode == AmrNbMode.Mr67) {
      positions[0] = (fixedIndex & 7) * 5;
      var subset = (fixedIndex >> 2) & 2;
      positions[1] = ((fixedIndex >> 4) & 7) * 5 + subset + 1;
      subset = (fixedIndex >> 6) & 2;
      positions[2] = ((fixedIndex >> 8) & 7) * 5 + subset + 2;
      return 3;
    }

    positions[0] = AmrNbTables.GrayDecode[fixedIndex & 7];
    positions[1] = AmrNbTables.GrayDecode[(fixedIndex >> 3) & 7] + 1;
    positions[2] = AmrNbTables.GrayDecode[(fixedIndex >> 6) & 7] + 2;
    var finalSubset = (fixedIndex >> 9) & 1;
    positions[3] = AmrNbTables.GrayDecode[(fixedIndex >> 10) & 7] + finalSubset + 3;
    return 4;
  }

  private static void EncodeMr122Innovation(ReadOnlySpan<float> residual, int baseWord, Span<int> words) {
    for (var track = 0; track < 5; ++track) {
      var bestOdd = 0;
      var bestEven = 0;
      var bestScore = double.NegativeInfinity;
      for (var odd = 0; odd < 16; ++odd) {
        var pos1 = AmrNbTables.GrayDecode[odd & 7] + track;
        var sign1 = (odd & 8) != 0 ? -1f : 1f;
        for (var even = 0; even < 8; ++even) {
          var pos2 = AmrNbTables.GrayDecode[even] + track;
          var sign2 = pos2 < pos1 ? -sign1 : sign1;
          var correlation = residual[pos1] * sign1 + residual[pos2] * sign2;
          var energy = pos1 == pos2 ? Square(sign1 + sign2) : 2d;
          var score = energy > 0 ? correlation * correlation / energy : 0;
          if (score <= bestScore)
            continue;
          bestScore = score;
          bestOdd = odd;
          bestEven = even;
        }
      }
      words[baseWord + 3 + 2 * track + 1] = bestOdd;
      words[baseWord + 3 + 2 * track] = bestEven;
    }
  }

  private static void EncodeMr102Innovation(
    ReadOnlySpan<float> residual,
    int baseWord,
    Span<int> words,
    ReadOnlySpan<int> widths) {
    var pulse4Word = baseWord + 3 + 4;
    var pulse5Word = baseWord + 3 + 5;
    var pulse6Word = baseWord + 3 + 6;
    var max4 = Math.Min(CandidateCount(widths[pulse4Word]), 1024);
    var max5 = Math.Min(CandidateCount(widths[pulse5Word]), 1024);
    var max6 = Math.Min(CandidateCount(widths[pulse6Word]), 128);
    Span<int> positions = stackalloc int[8];

    var best4 = 0;
    var best5 = 0;
    for (var iteration = 0; iteration < 2; ++iteration) {
      var bestScore4 = double.NegativeInfinity;
      for (var candidate = 0; candidate < max4; ++candidate) {
        DecodeMr102Positions(candidate, best5, 0, positions);
        var score = BestPairScore(residual, positions[0], positions[4], out _)
                    + BestPairScore(residual, positions[1], positions[5], out _);
        if (score <= bestScore4)
          continue;
        bestScore4 = score;
        best4 = candidate;
      }

      var bestScore5 = double.NegativeInfinity;
      for (var candidate = 0; candidate < max5; ++candidate) {
        DecodeMr102Positions(best4, candidate, 0, positions);
        var score = BestPairScore(residual, positions[2], positions[6], out _)
                    + BestPairScore(residual, positions[1], positions[5], out _);
        if (score <= bestScore5)
          continue;
        bestScore5 = score;
        best5 = candidate;
      }
    }

    var best6 = 0;
    var bestScore6 = double.NegativeInfinity;
    for (var candidate = 0; candidate < max6; ++candidate) {
      DecodeMr102Positions(best4, best5, candidate, positions);
      var score = BestPairScore(residual, positions[3], positions[7], out _);
      if (score <= bestScore6)
        continue;
      bestScore6 = score;
      best6 = candidate;
    }

    DecodeMr102Positions(best4, best5, best6, positions);
    for (var track = 0; track < 4; ++track) {
      _ = BestPairScore(residual, positions[track], positions[track + 4], out var signBit);
      words[baseWord + 3 + track] = signBit;
    }
    words[pulse4Word] = best4;
    words[pulse5Word] = best5;
    words[pulse6Word] = best6;
  }

  private static void DecodeMr102Positions(int code4, int code5, int code6, Span<int> positions) {
    Span<int> raw = stackalloc int[8];
    DecodeBaseFivePulse(code4, raw, 0, 4, 1);
    DecodeBaseFivePulse(code5, raw, 2, 6, 5);

    var temp = ((code6 >> 2) * 25 + 12) >> 5;
    raw[3] = temp % 5;
    raw[7] = temp / 5;
    if ((raw[7] & 1) != 0)
      raw[3] = 4 - raw[3];
    raw[3] = (raw[3] << 1) + (code6 & 1);
    raw[7] = (raw[7] << 1) + ((code6 >> 1) & 1);

    for (var track = 0; track < 4; ++track) {
      positions[track] = (raw[track] << 2) + track;
      positions[track + 4] = (raw[track + 4] << 2) + track;
    }
  }

  private static void DecodeBaseFivePulse(int code, Span<int> positions, int i1, int i2, int i3) {
    var p = AmrNbTables.BaseFiveTable[code >> 3];
    positions[i1] = (p[2] << 1) + (code & 1);
    positions[i2] = (p[1] << 1) + ((code >> 1) & 1);
    positions[i3] = (p[0] << 1) + ((code >> 2) & 1);
  }

  private static double BestPairScore(ReadOnlySpan<float> residual, int pos1, int pos2, out int signBit) {
    var secondSign = pos2 < pos1 ? -1f : 1f;
    var correlation = residual[pos1] + residual[pos2] * secondSign;
    signBit = correlation >= 0 ? 0 : 1;
    correlation = Math.Abs(correlation);
    var energy = pos1 == pos2 ? Square(1 + secondSign) : 2d;
    return energy > 0 ? correlation * correlation / energy : 0;
  }

  private static float InnovationMeanEnergy(AmrNbMode mode, int subframe, ReadOnlySpan<int> words) {
    Span<float> vector = stackalloc float[AmrNbData.SubframeSize];
    var baseWord = 5 + subframe * 13;

    if (mode == AmrNbMode.Mr122) {
      for (var track = 0; track < 5; ++track) {
        var odd = words[baseWord + 3 + 2 * track + 1];
        var even = words[baseWord + 3 + 2 * track];
        var pos1 = AmrNbTables.GrayDecode[odd & 7] + track;
        var pos2 = AmrNbTables.GrayDecode[even & 7] + track;
        var sign1 = (odd & 8) != 0 ? -1f : 1f;
        var sign2 = pos2 < pos1 ? -sign1 : sign1;
        vector[pos1] += sign1;
        vector[pos2] += sign2;
      }
    } else if (mode == AmrNbMode.Mr102) {
      Span<int> positions = stackalloc int[8];
      DecodeMr102Positions(
        words[baseWord + 7], words[baseWord + 8], words[baseWord + 9], positions);
      for (var track = 0; track < 4; ++track) {
        var sign1 = words[baseWord + 3 + track] != 0 ? -1f : 1f;
        var sign2 = positions[track + 4] < positions[track] ? -sign1 : sign1;
        vector[positions[track]] += sign1;
        vector[positions[track + 4]] += sign2;
      }
    } else {
      Span<int> positions = stackalloc int[4];
      var count = DecodeLowRatePositions(mode, subframe, words[baseWord + 3], positions);
      var signMask = words[baseWord + 4];
      for (var i = 0; i < count; ++i)
        vector[positions[i]] += ((signMask >> i) & 1) != 0 ? 1f : -1f;
    }

    double energy = 0;
    foreach (var sample in vector)
      energy += sample * sample;
    return (float)Math.Max(energy / AmrNbData.SubframeSize, 1e-6);
  }

  // ---------------------------------------------------------------------------------------------
  // Gain VQ with the decoder's MA predictor
  // ---------------------------------------------------------------------------------------------

  private static void QuantizeGains(
    AmrNbMode mode,
    Span<int> words,
    ReadOnlySpan<int> widths,
    ReadOnlySpan<float> targetPitch,
    ReadOnlySpan<float> targetFixed,
    ReadOnlySpan<float> innovationEnergy,
    EncoderState state) {
    if (mode == AmrNbMode.Mr475) {
      QuantizeMr475GainPair(0, words, widths, targetPitch, targetFixed, innovationEnergy, state);
      QuantizeMr475GainPair(2, words, widths, targetPitch, targetFixed, innovationEnergy, state);
      return;
    }

    for (var sub = 0; sub < AmrNbData.SubframeCount; ++sub) {
      var baseWord = 5 + sub * 13;
      if (mode is AmrNbMode.Mr795 or AmrNbMode.Mr122) {
        words[baseWord + 1] = FindNearestPitchGain(targetPitch[sub], widths[baseWord + 1]);
        words[baseWord + 2] = FindNearestSeparateFixedGain(
          targetFixed[sub], innovationEnergy[sub], mode, widths[baseWord + 2], state.PredictionError);
        var factor = AmrNbTables.QuaGainCode[words[baseWord + 2]] / 2048f;
        UpdatePredictionError(state.PredictionError, factor);
        continue;
      }

      var table = mode >= AmrNbMode.Mr67 ? AmrNbTables.GainsHigh : AmrNbTables.GainsLow;
      var candidates = Math.Min(CandidateCount(widths[baseWord + 1]), table.Length);
      var best = 0;
      var bestScore = double.PositiveInfinity;
      for (var index = 0; index < candidates; ++index) {
        var pitch = table[index][0] / 16384f;
        var factor = table[index][1] / 4096f;
        var fixedGain = PredictFixedGain(
          factor, innovationEnergy[sub], state.PredictionError, AmrNbTables.EnergyMean[(int)mode]);
        var score = GainScore(pitch, fixedGain, targetPitch[sub], targetFixed[sub]);
        if (score >= bestScore)
          continue;
        bestScore = score;
        best = index;
      }
      words[baseWord + 1] = best;
      UpdatePredictionError(state.PredictionError, table[best][1] / 4096f);
    }
  }

  private static void QuantizeMr475GainPair(
    int firstSubframe,
    Span<int> words,
    ReadOnlySpan<int> widths,
    ReadOnlySpan<float> targetPitch,
    ReadOnlySpan<float> targetFixed,
    ReadOnlySpan<float> innovationEnergy,
    EncoderState state) {
    var baseWord = 5 + firstSubframe * 13;
    var candidates = Math.Min(CandidateCount(widths[baseWord + 1]), AmrNbTables.GainsMode4k75.Length / 2);
    Span<float> temporaryPrediction = stackalloc float[4];
    var bestWord = 0;
    var bestScore = double.PositiveInfinity;

    for (var word = 0; word < candidates; ++word) {
      state.PredictionError.AsSpan().CopyTo(temporaryPrediction);
      double score = 0;
      for (var parity = 0; parity < 2; ++parity) {
        var sub = firstSubframe + parity;
        var entry = AmrNbTables.GainsMode4k75[(word << 1) + parity];
        var pitch = entry[0] / 16384f;
        var factor = entry[1] / 4096f;
        var fixedGain = PredictFixedGain(
          factor, innovationEnergy[sub], temporaryPrediction, AmrNbTables.EnergyMean[(int)AmrNbMode.Mr475]);
        score += GainScore(pitch, fixedGain, targetPitch[sub], targetFixed[sub]);
        UpdatePredictionError(temporaryPrediction, factor);
      }
      if (score >= bestScore)
        continue;
      bestScore = score;
      bestWord = word;
    }

    words[baseWord + 1] = bestWord;
    for (var parity = 0; parity < 2; ++parity) {
      var factor = AmrNbTables.GainsMode4k75[(bestWord << 1) + parity][1] / 4096f;
      UpdatePredictionError(state.PredictionError, factor);
    }
  }

  private static int FindNearestPitchGain(float target, int fieldBits) {
    var candidates = Math.Min(CandidateCount(fieldBits), AmrNbTables.QuaGainPit.Length);
    var best = 0;
    var bestError = float.PositiveInfinity;
    for (var index = 0; index < candidates; ++index) {
      var gain = AmrNbTables.QuaGainPit[index] / 16384f;
      var error = MathF.Abs(gain - target);
      if (error >= bestError)
        continue;
      bestError = error;
      best = index;
    }
    return best;
  }

  private static int FindNearestSeparateFixedGain(
    float target,
    float innovationEnergy,
    AmrNbMode mode,
    int fieldBits,
    ReadOnlySpan<float> predictionError) {
    var candidates = Math.Min(CandidateCount(fieldBits), AmrNbTables.QuaGainCode.Length);
    var best = 0;
    var bestError = double.PositiveInfinity;
    for (var index = 0; index < candidates; ++index) {
      var factor = AmrNbTables.QuaGainCode[index] / 2048f;
      var gain = PredictFixedGain(factor, innovationEnergy, predictionError, AmrNbTables.EnergyMean[(int)mode]);
      var error = Square(Math.Log((gain + 1) / (target + 1)));
      if (error >= bestError)
        continue;
      bestError = error;
      best = index;
    }
    return best;
  }

  private static float PredictFixedGain(
    float factor,
    float innovationEnergy,
    ReadOnlySpan<float> predictionError,
    float energyMean) {
    var predictedDb = energyMean;
    for (var i = 0; i < predictionError.Length; ++i)
      predictedDb += AmrNbTables.EnergyPredFac[i] * predictionError[i];
    return (float)(factor * Math.Pow(10, 0.05 * predictedDb) / Math.Sqrt(Math.Max(innovationEnergy, 1e-6f)));
  }

  private static double GainScore(float pitch, float fixedGain, float targetPitch, float targetFixed) {
    var pitchError = (pitch - targetPitch) / 0.08;
    var fixedError = Math.Log((fixedGain + 1) / (targetFixed + 1));
    return pitchError * pitchError + fixedError * fixedError;
  }

  private static void UpdatePredictionError(Span<float> predictionError, float factor) {
    predictionError[0] = predictionError[1];
    predictionError[1] = predictionError[2];
    predictionError[2] = predictionError[3];
    predictionError[3] = 20f * MathF.Log10(MathF.Max(factor, 1e-9f));
  }

  // ---------------------------------------------------------------------------------------------
  // Bit packing and utility helpers
  // ---------------------------------------------------------------------------------------------

  private static int[] GetFieldWidths(AmrNbMode mode) {
    var result = new int[AmrNbFrame.WordCount];
    var order = AmrNbTables.UnpackingBitmapsPerMode[(int)mode];
    var p = 0;
    while (true) {
      var bits = order[p++];
      if (bits == 0)
        break;
      var word = order[p++];
      result[word] = Math.Max(result[word], bits);
      p += bits;
    }
    return result;
  }

  private static byte[] Pack(ReadOnlySpan<int> words, AmrNbMode mode) {
    var payload = new byte[AmrNbData.PayloadBytes[(int)mode]];
    var order = AmrNbTables.UnpackingBitmapsPerMode[(int)mode];
    var p = 0;
    while (true) {
      var fieldBits = order[p++];
      if (fieldBits == 0)
        break;
      var word = order[p++];
      var value = (uint)words[word] & FieldMask(fieldBits);
      for (var sourceBit = fieldBits - 1; sourceBit >= 0; --sourceBit) {
        var destinationBit = order[p++];
        if (((value >> sourceBit) & 1u) != 0)
          payload[destinationBit >> 3] |= (byte)(1 << (destinationBit & 7));
      }
    }
    return payload;
  }

  private static int CandidateCount(int bits)
    => bits <= 0 ? 1 : 1 << Math.Min(bits, 20);

  private static uint FieldMask(int bits)
    => bits >= 32 ? uint.MaxValue : bits <= 0 ? 0u : (1u << bits) - 1u;

  private static double Square(double value) => value * value;

  private static float RootMeanSquare(ReadOnlySpan<short> samples) {
    double energy = 0;
    foreach (var sample in samples)
      energy += (double)sample * sample;
    return (float)Math.Sqrt(energy / Math.Max(1, samples.Length));
  }

  private static float RootMeanSquare(ReadOnlySpan<float> samples) {
    double energy = 0;
    foreach (var sample in samples)
      energy += sample * sample;
    return (float)Math.Sqrt(energy / Math.Max(1, samples.Length));
  }
}
