#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>AAC long-window shape.</summary>
public enum AacEncoderWindowShape {
  /// <summary>
  /// Specifies the sine option.
  /// </summary>
Sine = 0,
  /// <summary>
  /// Specifies the kbd option.
  /// </summary>
Kbd = 1,
}

/// <summary>Stereo spectral coding mode for AAC channel-pair elements.</summary>
public enum AacStereoCodingMode {
  /// <summary>
  /// Specifies the independent option.
  /// </summary>
Independent,
  /// <summary>
  /// Specifies the mid side option.
  /// </summary>
MidSide,
  /// <summary>
  /// Selects the value automatically.
  /// </summary>
Auto,
}

/// <summary>
/// Controls for the reference AAC-LC encoder. The implementation deliberately uses the
/// standards tables already shared with the decoder rather than an unmanaged FAAC/FDK wrapper.
/// </summary>
public sealed record AacEncoderOptions(
  int SampleRate,
  int Channels,
  int Bitrate = 128_000,
  int CutoffHz = 0,
  AacEncoderWindowShape WindowShape = AacEncoderWindowShape.Sine,
  AacStereoCodingMode StereoMode = AacStereoCodingMode.Auto,
  bool PadFinalFrame = true
);

/// <summary>
/// Pure-managed AAC-LC/ADTS encoder. It implements the normative long-window transform,
/// scalefactor-band quantisation, spectral Huffman codebooks 5..11 including escape values,
/// channel-pair elements and optional M/S stereo. The rate controller is intentionally simple:
/// a monotonic global-gain search targets the requested average bits per 1024-sample frame.
/// </summary>
public static class AacEncoder {
  /// <summary>
  /// Defines the frame samples constant value.
  /// </summary>
public const int FrameSamples = 1024;
  /// <summary>
  /// Defines the encoder delay samples constant value.
  /// </summary>
public const int EncoderDelaySamples = 1024;

  private static readonly float[] MdctCosine = BuildMdctCosine();
  private static readonly float[] SineWindow = BuildSineWindow();
  private static readonly float[] KbdWindow = BuildKbdWindow();

  private sealed record EncodedChannel(int GlobalGain, int[] Quantized, int[] Codebooks, int EstimatedBits);

  /// <summary>Encodes interleaved PCM16 to a CRC-absent ADTS AAC-LC stream.</summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, AacEncoderOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    Validate(interleaved.Length, options);
    if (interleaved.Length == 0)
      return [];

    var sampleRateIndex = Array.IndexOf(AacAdtsReader.SampleRateTable, options.SampleRate);
    var frames = interleaved.Length / options.Channels;
    if (!options.PadFinalFrame && frames % FrameSamples != 0)
      throw new ArgumentException($"AAC input must contain whole {FrameSamples}-sample frames when padding is disabled.", nameof(interleaved));

    var inputBlocks = (frames + FrameSamples - 1) / FrameSamples;
    var previous = new float[options.Channels][];
    for (var c = 0; c < options.Channels; ++c)
      previous[c] = new float[FrameSamples];

    using var output = new MemoryStream();
    var current = new float[options.Channels][];
    for (var c = 0; c < options.Channels; ++c)
      current[c] = new float[FrameSamples];

    // AAC's lapped transform needs one priming frame and one trailing frame. The first
    // decoded block is encoder delay; the final zero-input block releases the last input tail.
    for (var block = 0; block <= inputBlocks; ++block) {
      for (var c = 0; c < options.Channels; ++c)
        Array.Clear(current[c]);

      if (block < inputBlocks) {
        var baseFrame = block * FrameSamples;
        var available = Math.Min(FrameSamples, frames - baseFrame);
        for (var i = 0; i < available; ++i)
          for (var c = 0; c < options.Channels; ++c)
            current[c][i] = interleaved[(baseFrame + i) * options.Channels + c] / 32768f;
      }

      var spectrum = new float[options.Channels][];
      for (var c = 0; c < options.Channels; ++c)
        spectrum[c] = ForwardLongMdct(previous[c], current[c], options.WindowShape);

      var useMs = options.Channels == 2 && options.StereoMode switch {
        AacStereoCodingMode.MidSide => true,
        AacStereoCodingMode.Independent => false,
        _ => PreferMidSide(spectrum[0], spectrum[1]),
      };
      if (useMs) {
        for (var k = 0; k < FrameSamples; ++k) {
          var left = spectrum[0][k];
          var right = spectrum[1][k];
          spectrum[0][k] = (left + right) * 0.5f;
          spectrum[1][k] = (left - right) * 0.5f;
        }
      }

      var targetPerChannel = Math.Clamp(
        (int)((long)options.Bitrate * FrameSamples / options.SampleRate / options.Channels),
        128, 6144);
      var cutoffBin = options.CutoffHz == 0
        ? FrameSamples
        : Math.Clamp((int)Math.Ceiling(options.CutoffHz * (2.0 * FrameSamples) / options.SampleRate), 1, FrameSamples);

      var channels = new EncodedChannel[options.Channels];
      for (var c = 0; c < options.Channels; ++c)
        channels[c] = QuantizeToBudget(spectrum[c], sampleRateIndex, cutoffBin, targetPerChannel);

      var raw = BuildRawDataBlock(channels, sampleRateIndex, options.WindowShape, useMs);
      var header = AacAdtsReader.BuildHeader(
        profile: (int)AacObjectType.AacLc - 1,
        sampleRateIndex: sampleRateIndex,
        channelConfig: options.Channels,
        frameLength: AacAdtsReader.ShortHeaderLength + raw.Length,
        bufferFullness: 0x7FF);
      output.Write(header);
      output.Write(raw);

      for (var c = 0; c < options.Channels; ++c)
        Array.Copy(current[c], previous[c], FrameSamples);
    }

    return output.ToArray();
  }

  private static EncodedChannel QuantizeToBudget(float[] spectrum, int sampleRateIndex, int cutoffBin, int targetBits) {
    var low = 0;
    var high = 255;
    EncodedChannel? best = null;
    while (low <= high) {
      var gain = (low + high) >> 1;
      var candidate = Quantize(spectrum, sampleRateIndex, cutoffBin, gain);
      if (candidate.EstimatedBits <= targetBits) {
        best = candidate;
        high = gain - 1;
      } else {
        low = gain + 1;
      }
    }
    return best ?? Quantize(spectrum, sampleRateIndex, cutoffBin, 255);
  }

  private static EncodedChannel Quantize(float[] spectrum, int sampleRateIndex, int cutoffBin, int globalGain) {
    var swb = AacScaleFactorBands.Long1024[sampleRateIndex];
    var maxSfb = AacScaleFactorBands.NumSwbLong[sampleRateIndex];
    var quant = new int[FrameSamples];
    var codebooks = new int[maxSfb];
    var gain = AacSpectral.ScaleFactorGain(globalGain);
    var inverseGain = gain > 0 ? 1f / gain : float.MaxValue;

    for (var k = 0; k < cutoffBin; ++k) {
      var value = spectrum[k];
      if (MathF.Abs(value) < 1e-12f) continue;
      var q = (int)MathF.Round(MathF.Pow(MathF.Abs(value) * inverseGain, 0.75f));
      q = Math.Clamp(q, 0, 32767);
      quant[k] = value < 0 ? -q : q;
    }

    var spectralBits = 0;
    for (var sfb = 0; sfb < maxSfb; ++sfb) {
      var start = swb[sfb];
      var end = swb[sfb + 1];
      var cb = SelectCodebook(quant, start, end, out var bits);
      codebooks[sfb] = cb;
      spectralBits += bits;
    }

    // global_gain + long ics_info + section/scalefactor/tool fields + spectrum.
    var sectionBits = EstimateSectionBits(codebooks);
    var scaleFactorBits = codebooks.Count(static cb => cb != 0); // delta zero is one-bit HCB_SF codeword
    return new EncodedChannel(globalGain, quant, codebooks,
      8 + 11 + sectionBits + scaleFactorBits + 3 + spectralBits);
  }

  private static int SelectCodebook(int[] quant, int start, int end, out int bestBits) {
    var maxAbs = 0;
    var any = false;
    for (var i = start; i < end; ++i) {
      var a = Math.Abs(quant[i]);
      maxAbs = Math.Max(maxAbs, a);
      any |= a != 0;
    }
    if (!any) {
      bestBits = 0;
      return AacHuffmanTables.ZeroHcb;
    }

    Span<int> candidates = stackalloc int[7];
    var count = 0;
    if (maxAbs <= 4) { candidates[count++] = 5; candidates[count++] = 6; }
    if (maxAbs <= 7) { candidates[count++] = 7; candidates[count++] = 8; }
    if (maxAbs <= 12) { candidates[count++] = 9; candidates[count++] = 10; }
    candidates[count++] = 11;

    var bestCb = 11;
    bestBits = int.MaxValue;
    for (var c = 0; c < count; ++c) {
      var cb = candidates[c];
      var bits = SpectralBits(quant, start, end, cb);
      if (bits >= bestBits) continue;
      bestBits = bits;
      bestCb = cb;
    }
    return bestCb;
  }

  private static int SpectralBits(int[] quant, int start, int end, int cb) {
    var bits = 0;
    for (var i = start; i < end; i += 2) {
      var a = quant[i];
      var b = i + 1 < end ? quant[i + 1] : 0;
      var index = SpectralIndex(a, b, cb);
      bits += AacHuffmanTables.SpectralBits[cb - 1][index];
      if (AacHuffmanTables.Unsigned[cb]) {
        if (a != 0) ++bits;
        if (b != 0) ++bits;
      }
      if (cb == AacHuffmanTables.EscapeHcb) {
        if (Math.Abs(a) >= 16) bits += EscapeBitCount(Math.Abs(a));
        if (Math.Abs(b) >= 16) bits += EscapeBitCount(Math.Abs(b));
      }
    }
    return bits;
  }

  private static int SpectralIndex(int a, int b, int cb) {
    var lav = AacHuffmanTables.Lav[cb];
    if (AacHuffmanTables.Unsigned[cb]) {
      var radix = lav + 1;
      return Math.Min(Math.Abs(a), lav) * radix + Math.Min(Math.Abs(b), lav);
    }
    var signedRadix = 2 * lav + 1;
    return (Math.Clamp(a, -lav, lav) + lav) * signedRadix + (Math.Clamp(b, -lav, lav) + lav);
  }

  private static int EstimateSectionBits(int[] codebooks) {
    var bits = 0;
    for (var sfb = 0; sfb < codebooks.Length;) {
      var cb = codebooks[sfb];
      var end = sfb + 1;
      while (end < codebooks.Length && codebooks[end] == cb) ++end;
      var length = end - sfb;
      bits += 4;
      while (length >= 31) { bits += 5; length -= 31; }
      bits += 5;
      sfb = end;
    }
    return bits;
  }

  private static byte[] BuildRawDataBlock(
    EncodedChannel[] channels, int sampleRateIndex, AacEncoderWindowShape shape, bool midSide) {
    ActiveLongOffsets.Value = AacScaleFactorBands.Long1024[sampleRateIndex];
    var writer = new AacBitWriter();
    var maxSfb = AacScaleFactorBands.NumSwbLong[sampleRateIndex];

    if (channels.Length == 1) {
      writer.Write((uint)AacElementType.Sce, 3);
      writer.Write(0, 4); // element_instance_tag
      WriteIndividualChannelStream(writer, channels[0], maxSfb, shape, writeIcsInfo: true);
    } else {
      writer.Write((uint)AacElementType.Cpe, 3);
      writer.Write(0, 4);
      writer.Write(1, 1); // common_window
      WriteIcsInfo(writer, maxSfb, shape);
      writer.Write(midSide ? 2u : 0u, 2); // ms_mask_present: 2 = all bands, 0 = none
      WriteIndividualChannelStream(writer, channels[0], maxSfb, shape, writeIcsInfo: false);
      WriteIndividualChannelStream(writer, channels[1], maxSfb, shape, writeIcsInfo: false);
    }

    writer.Write((uint)AacElementType.End, 3);
    writer.AlignByte();
    return writer.ToArray();
  }

  private static void WriteIndividualChannelStream(
    AacBitWriter writer, EncodedChannel channel, int maxSfb,
    AacEncoderWindowShape shape, bool writeIcsInfo) {
    writer.Write((uint)channel.GlobalGain, 8);
    if (writeIcsInfo)
      WriteIcsInfo(writer, maxSfb, shape);
    WriteSections(writer, channel.Codebooks);

    for (var sfb = 0; sfb < channel.Codebooks.Length; ++sfb) {
      if (channel.Codebooks[sfb] == AacHuffmanTables.ZeroHcb) continue;
      // All active bands use the same scale factor == global_gain, so DPCM delta = 0.
      writer.Write(AacHuffmanTables.ScaleFactorCodes[60], AacHuffmanTables.ScaleFactorBits[60]);
    }

    writer.Write(0, 1); // pulse_data_present
    writer.Write(0, 1); // tns_data_present
    writer.Write(0, 1); // gain_control_data_present
    WriteSpectrum(writer, channel.Quantized, channel.Codebooks);
  }

  private static void WriteIcsInfo(AacBitWriter writer, int maxSfb, AacEncoderWindowShape shape) {
    writer.Write(0, 1); // reserved
    writer.Write(AacFilterBank.OnlyLong, 2);
    writer.Write((uint)shape, 1);
    writer.Write((uint)maxSfb, 6);
    writer.Write(0, 1); // predictor_data_present (AAC-LC)
  }

  private static void WriteSections(AacBitWriter writer, int[] codebooks) {
    for (var sfb = 0; sfb < codebooks.Length;) {
      var cb = codebooks[sfb];
      var end = sfb + 1;
      while (end < codebooks.Length && codebooks[end] == cb) ++end;
      var length = end - sfb;
      writer.Write((uint)cb, 4);
      while (length >= 31) {
        writer.Write(31, 5);
        length -= 31;
      }
      writer.Write((uint)length, 5);
      sfb = end;
    }
  }

  private static void WriteSpectrum(AacBitWriter writer, int[] quant, int[] codebooks) {
    // Long windows have one group; codebook geometry follows the long SWB table.
    // Derive the table index from max_sfb by matching the codebook count at the caller's rate.
    // The actual boundaries are carried by the encoder via the selected codebook array below.
    var swb = ResolveLongOffsets(codebooks.Length);
    for (var sfb = 0; sfb < codebooks.Length; ++sfb) {
      var cb = codebooks[sfb];
      if (cb == 0) continue;
      var start = swb[sfb];
      var end = swb[sfb + 1];
      for (var i = start; i < end; i += 2) {
        var a = quant[i];
        var b = i + 1 < end ? quant[i + 1] : 0;
        var index = SpectralIndex(a, b, cb);
        writer.Write(AacHuffmanTables.SpectralCodes[cb - 1][index], AacHuffmanTables.SpectralBits[cb - 1][index]);
        if (AacHuffmanTables.Unsigned[cb]) {
          if (a != 0) writer.Write(a < 0 ? 1u : 0u, 1);
          if (b != 0) writer.Write(b < 0 ? 1u : 0u, 1);
        }
        if (cb == AacHuffmanTables.EscapeHcb) {
          if (Math.Abs(a) >= 16) WriteEscape(writer, Math.Abs(a));
          if (Math.Abs(b) >= 16) WriteEscape(writer, Math.Abs(b));
        }
      }
    }
  }

  // max_sfb counts happen to collide at several rates, so the spectral writer cannot infer
  // the rate from the count alone. This field is set around BuildRawDataBlock and kept local
  // to a thread via AsyncLocal to avoid changing the compact channel record with duplicated data.
  private static readonly AsyncLocal<int[]?> ActiveLongOffsets = new();

  private static int[] ResolveLongOffsets(int maxSfb)
    => ActiveLongOffsets.Value is { Length: > 1 } offsets && offsets.Length - 1 == maxSfb
      ? offsets
      : throw new InvalidOperationException("AAC encoder scale-factor-band geometry was not installed.");

  private static void WriteEscape(AacBitWriter writer, int magnitude) {
    magnitude = Math.Max(16, magnitude);
    var n = 31 - int.LeadingZeroCount(magnitude);
    for (var i = 4; i < n; ++i) writer.Write(1, 1);
    writer.Write(0, 1);
    writer.Write((uint)(magnitude - (1 << n)), n);
  }

  private static int EscapeBitCount(int magnitude) {
    var n = 31 - int.LeadingZeroCount(Math.Max(16, magnitude));
    return (n - 4) + 1 + n;
  }

  private static bool PreferMidSide(float[] left, float[] right) {
    double lr = 0, ms = 0;
    for (var i = 0; i < left.Length; ++i) {
      var l = left[i];
      var r = right[i];
      lr += Math.Abs(l) + Math.Abs(r);
      ms += Math.Abs((l + r) * 0.5) + Math.Abs((l - r) * 0.5);
    }
    return ms < lr * 0.96;
  }

  private static float[] ForwardLongMdct(float[] previous, float[] current, AacEncoderWindowShape shape) {
    var window = shape == AacEncoderWindowShape.Kbd ? KbdWindow : SineWindow;
    var time = new float[2 * FrameSamples];
    for (var i = 0; i < FrameSamples; ++i) {
      time[i] = previous[i] * window[i];
      time[FrameSamples + i] = current[i] * window[FrameSamples + i];
    }

    var spectrum = new float[FrameSamples];
    for (var k = 0; k < FrameSamples; ++k) {
      double sum = 0;
      for (var n = 0; n < 2 * FrameSamples; ++n)
        sum += time[n] * MdctCosine[n * FrameSamples + k];
      spectrum[k] = (float)sum;
    }
    return spectrum;
  }

  private static float[] BuildMdctCosine() {
    var table = new float[2 * FrameSamples * FrameSamples];
    var n0 = FrameSamples / 2.0 + 0.5;
    for (var n = 0; n < 2 * FrameSamples; ++n)
      for (var k = 0; k < FrameSamples; ++k)
        table[n * FrameSamples + k] =
          (float)Math.Cos(Math.PI / FrameSamples * (n + n0) * (2 * k + 1));
    return table;
  }

  private static float[] BuildSineWindow() {
    var result = new float[2 * FrameSamples];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (float)Math.Sin(Math.PI / (2.0 * FrameSamples) * (i + 0.5));
    return result;
  }

  private static float[] BuildKbdWindow() {
    var half = MakeKbdHalf(FrameSamples, 4.0);
    var result = new float[2 * FrameSamples];
    for (var i = 0; i < FrameSamples; ++i) {
      result[i] = half[i];
      result[2 * FrameSamples - 1 - i] = half[i];
    }
    return result;
  }

  private static float[] MakeKbdHalf(int n, double alpha) {
    var kaiser = new double[n + 1];
    var denom = BesselI0(Math.PI * alpha);
    for (var i = 0; i <= n; ++i) {
      var ratio = 2.0 * i / n - 1.0;
      kaiser[i] = BesselI0(Math.PI * alpha * Math.Sqrt(Math.Max(0, 1.0 - ratio * ratio))) / denom;
    }
    var total = kaiser.Sum();
    var running = 0.0;
    var result = new float[n];
    for (var i = 0; i < n; ++i) {
      running += kaiser[i];
      result[i] = (float)Math.Sqrt(running / total);
    }
    return result;
  }

  private static double BesselI0(double x) {
    var sum = 1.0;
    var term = 1.0;
    var half = x * 0.5;
    for (var k = 1; k < 64; ++k) {
      term *= (half / k) * (half / k);
      sum += term;
      if (term < sum * 1e-16) break;
    }
    return sum;
  }

  private static void Validate(int sampleCount, AacEncoderOptions options) {
    var sampleRateIndex = Array.IndexOf(AacAdtsReader.SampleRateTable, options.SampleRate);
    if (sampleRateIndex is < 0 or > 12)
      throw new ArgumentOutOfRangeException(nameof(options), "AAC sample rate must be one of the ADTS standard rates 7350..96000 Hz.");
    if (options.Channels is < 1 or > 2)
      throw new ArgumentOutOfRangeException(nameof(options), "This AAC-LC encoder supports mono or stereo.");
    if (sampleCount % options.Channels != 0)
      throw new ArgumentException("Interleaved PCM sample count must be a multiple of the channel count.");
    if (options.Bitrate is < 8_000 or > 576_000)
      throw new ArgumentOutOfRangeException(nameof(options), "AAC target bitrate must be 8-576 kbit/s for this mono/stereo LC surface.");
    if (options.CutoffHz < 0 || options.CutoffHz > options.SampleRate / 2)
      throw new ArgumentOutOfRangeException(nameof(options), "AAC cutoff must be between 0 and Nyquist.");
  }

  private sealed class AacBitWriter {
    private readonly List<byte> _bytes = [];
    private byte _current;
    private int _used;

    public void Write(int value, int bits) => Write((uint)value, bits);

    public void Write(uint value, int bits) {
      for (var i = bits - 1; i >= 0; --i) {
        _current = (byte)(((uint)_current << 1) | ((value >> i) & 1u));
        if (++_used != 8) continue;
        _bytes.Add(_current);
        _current = 0;
        _used = 0;
      }
    }

    public void AlignByte() {
      if (_used == 0) return;
      _current <<= 8 - _used;
      _bytes.Add(_current);
      _current = 0;
      _used = 0;
    }

    public byte[] ToArray() {
      AlignByte();
      return [.. _bytes];
    }
  }
}
