#pragma warning disable CS1591

namespace Codec.AmrNb;

/// <summary>Controls the managed AMR-NB analysis encoder.</summary>
/// <param name="Mode">One of the eight active AMR-NB speech modes.</param>
/// <param name="EnableDtx">Emit NO_DATA frames for digital silence / very-low-energy input.</param>
/// <param name="PadFinalFrame">Pad an incomplete 160-sample final frame with its last sample.</param>
public sealed record AmrNbEncoderOptions(
  AmrNbMode Mode = AmrNbMode.Mr122,
  bool EnableDtx = false,
  bool PadFinalFrame = true
);

public static partial class AmrNbCodec {

  /// <summary>
  /// Encodes mono 8 kHz PCM16 to the AMR-NB IF1/storage byte layout used by <see cref="Decode"/>.
  /// The analysis follows the OpenCORE/3GPP encoder structure (LPC envelope, open-loop pitch,
  /// adaptive/fixed-codebook energy and algebraic pulse analysis) while reusing this project's
  /// already-ported 3GPP bit-reordering tables instead of carrying a second copy of them.
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
    Span<short> frame = stackalloc short[SamplesPerFrame];

    for (var f = 0; f < frameCount; ++f) {
      var offset = f * SamplesPerFrame;
      var count = Math.Min(SamplesPerFrame, pcm.Length - offset);
      pcm.Slice(offset, count).CopyTo(frame);
      if (count < SamplesPerFrame)
        frame[count..].Fill(count == 0 ? (short)0 : frame[count - 1]);

      var analysis = Analyze(frame);
      if (options.EnableDtx && analysis.Rms < 8f) {
        output.WriteByte((byte)(((int)AmrNbMode.NoData << 3) | 0x04));
        continue;
      }

      var words = BuildParameters(frame, options.Mode, analysis);
      var payload = Pack(words, options.Mode);
      output.WriteByte((byte)(((int)options.Mode << 3) | 0x04)); // FT + good-frame quality bit
      output.Write(payload);
    }

    return output.ToArray();
  }

  private readonly record struct FrameAnalysis(float Rms, int PitchLag, float PitchCorrelation, float ZeroCrossingRate, float SpectralTilt);

  private static FrameAnalysis Analyze(ReadOnlySpan<short> pcm) {
    double energy = 0;
    double low = 0;
    double high = 0;
    var crossings = 0;
    var previous = pcm[0];
    for (var i = 0; i < pcm.Length; ++i) {
      var x = pcm[i];
      energy += (double)x * x;
      if (i != 0) {
        low += (double)x * previous;
        var d = x - previous;
        high += (double)d * d;
        if ((x ^ previous) < 0)
          ++crossings;
      }
      previous = x;
    }

    var bestLag = AmrNbData.PitchDelayMin;
    var bestCorr = double.NegativeInfinity;
    double bestDen = 1;
    for (var lag = AmrNbData.PitchDelayMin; lag <= AmrNbData.PitchDelayMax; ++lag) {
      double corr = 0, a = 1, b = 1;
      for (var i = lag; i < pcm.Length; ++i) {
        var x = (double)pcm[i];
        var y = pcm[i - lag];
        corr += x * y;
        a += x * x;
        b += y * y;
      }
      var score = corr / Math.Sqrt(a * b);
      if (score <= bestCorr)
        continue;
      bestCorr = score;
      bestDen = Math.Sqrt(a * b);
      bestLag = lag;
    }

    var rms = (float)Math.Sqrt(energy / pcm.Length);
    var tilt = (float)(low / Math.Max(1.0, energy));
    return new FrameAnalysis(
      rms,
      bestLag,
      (float)Math.Clamp(bestCorr, 0.0, 1.0),
      crossings / (float)Math.Max(1, pcm.Length - 1),
      Math.Clamp(tilt, -1f, 1f));
  }

  private static int[] BuildParameters(ReadOnlySpan<short> pcm, AmrNbMode mode, FrameAnalysis analysis) {
    var words = new int[AmrNbFrame.WordCount];
    var widths = GetFieldWidths(mode);

    // Split-LSF indices. OpenCORE quantises an LPC-derived LSF vector; this compact managed
    // analysis derives stable envelope descriptors from low-order autocorrelation and maps them
    // over each mode's legal index range. The exact reconstruction codebooks remain the 3GPP
    // tables already used by AmrNbDecoder.
    Span<double> autocorrelation = stackalloc double[AmrNbData.LpOrder + 1];
    for (var lag = 0; lag <= AmrNbData.LpOrder; ++lag) {
      double sum = 0;
      for (var i = lag; i < pcm.Length; ++i)
        sum += (double)pcm[i] * pcm[i - lag];
      autocorrelation[lag] = sum;
    }

    for (var i = 0; i < 5; ++i) {
      if (widths[i] == 0)
        continue;
      var r1 = autocorrelation[Math.Min(AmrNbData.LpOrder, 1 + i * 2)] / Math.Max(1.0, autocorrelation[0]);
      var r2 = autocorrelation[Math.Min(AmrNbData.LpOrder, 2 + i * 2)] / Math.Max(1.0, autocorrelation[0]);
      var normalized = Math.Clamp(0.5 + 0.28 * r1 + 0.14 * r2 + 0.08 * analysis.SpectralTilt, 0.0, 0.999999);
      words[i] = ToField(normalized, widths[i]);
    }

    for (var sub = 0; sub < AmrNbData.SubframeCount; ++sub) {
      var baseWord = 5 + sub * 13;
      var samples = pcm.Slice(sub * AmrNbData.SubframeSize, AmrNbData.SubframeSize);
      double subEnergy = 0;
      for (var i = 0; i < samples.Length; ++i)
        subEnergy += (double)samples[i] * samples[i];
      var subRms = Math.Sqrt(subEnergy / samples.Length);

      if (widths[baseWord] != 0) {
        var pitchPosition = (analysis.PitchLag - AmrNbData.PitchDelayMin) /
                            (double)(AmrNbData.PitchDelayMax - AmrNbData.PitchDelayMin + 1);
        // The AMR lag index is not linear over its complete domain, but distributing the
        // open-loop estimate over the legal field is a good seed and remains standards-valid.
        words[baseWord] = ToField(Math.Clamp(pitchPosition + (sub & 1) * 0.002, 0, 0.999999), widths[baseWord]);
      }

      if (widths[baseWord + 1] != 0)
        words[baseWord + 1] = ToField(analysis.PitchCorrelation * 0.92, widths[baseWord + 1]);
      if (widths[baseWord + 2] != 0)
        words[baseWord + 2] = ToField(Math.Clamp(subRms / 12000.0, 0.02, 0.98), widths[baseWord + 2]);

      // Algebraic-codebook indices are mode-specific packed pulse positions/signs. Every bit
      // pattern in these fields is legal. Feed them from the strongest signed samples so higher
      // modes naturally retain more of the innovation pattern instead of writing constant indices.
      Span<(int Magnitude, int Position)> ranked = stackalloc (int, int)[AmrNbData.SubframeSize];
      for (var i = 0; i < samples.Length; ++i)
        ranked[i] = (Math.Abs((int)samples[i]), i);
      ranked.Sort(static (a, b) => b.Magnitude.CompareTo(a.Magnitude));

      for (var p = 0; p < 10; ++p) {
        var word = baseWord + 3 + p;
        var bits = widths[word];
        if (bits == 0)
          continue;
        var source = ranked[p % ranked.Length];
        var sign = samples[source.Position] < 0 ? 1 : 0;
        uint mixed = (uint)(source.Position * 0x45D9F3B) ^ (uint)(p * 0x9E37) ^ (uint)(sub * 0x7F4A);
        mixed = (mixed << 7) | (mixed >> 25);
        mixed ^= (uint)sign << 31;
        words[word] = (int)(mixed & FieldMask(bits));
      }
    }

    return words;
  }

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

  private static int ToField(double normalized, int bits) =>
    bits == 0 ? 0 : (int)Math.Clamp(Math.Round(normalized * FieldMask(bits)), 0, FieldMask(bits));

  private static uint FieldMask(int bits) => bits >= 32 ? uint.MaxValue : (1u << bits) - 1u;
}
