#pragma warning disable CS1591

namespace Codec.Dts;

/// <summary>Managed DTS core encoder controls.</summary>
/// <param name="SampleRate">Core sample rate. The classic DTS rates from 8 to 48 kHz are supported.</param>
/// <param name="Channels">Full-bandwidth channel count: mono, stereo, quad or 5.0.</param>
/// <param name="Bitrate">DTS core transmission bitrate in bit/s. Must be one of the core bitrate table entries.</param>
/// <param name="ActiveSubbands">Number of coded QMF subbands (2..32). More bands preserve more bandwidth at higher bitrates.</param>
/// <param name="PadFinalFrame">Pad an incomplete 512-sample-per-channel final frame with its last sample.</param>
public sealed record DtsEncoderOptions(
  int SampleRate = 48000,
  int Channels = 2,
  int Bitrate = 768000,
  int ActiveSubbands = 16,
  bool PadFinalFrame = true
);

public static partial class DtsCodec {

  private const int EncoderSamplesPerFrame = 512;
  private const int EncoderSubbands = 32;
  private const int EncoderSubbandSamples = 16;
  private const int EncoderQuantBits = 10;
  private const int EncoderSampleBits = EncoderQuantBits - 3;

  private static readonly int[] QuantSelectorWidths = [1, 2, 2, 2, 2, 3, 3, 3, 3, 3];
  private static readonly int[] RawQuantSelectors = [1, 3, 3, 3, 3, 7, 7, 7, 7, 7];

  /// <summary>
  /// Encodes interleaved PCM16 to standard 16-bit-big-endian DTS Coherent Acoustics core frames.
  /// This is a managed adaptation of FFmpeg's LGPL <c>dcaenc.c</c> core path: 512 PCM samples per
  /// frame, 32-band cosine analysis, direct bit allocation and scale-factor transmission, the
  /// no-Huffman quantizer selector, two sub-subframes and the mandatory 0xFFFF DSYNC marker.
  /// Prediction, high-frequency VQ and LFE coding are intentionally left off so every coded
  /// subband remains independently decodable and the generated core is deterministic.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, DtsEncoderOptions? options = null) {
    options ??= new DtsEncoderOptions();
    ValidateEncoder(interleaved.Length, options);
    if (interleaved.IsEmpty)
      return [];

    var samplesPerChannel = interleaved.Length / options.Channels;
    var frameCount = (samplesPerChannel + EncoderSamplesPerFrame - 1) / EncoderSamplesPerFrame;
    using var output = new MemoryStream();
    var frame = new short[EncoderSamplesPerFrame * options.Channels];

    for (var f = 0; f < frameCount; ++f) {
      var sourceFrame = f * EncoderSamplesPerFrame;
      var remaining = samplesPerChannel - sourceFrame;
      var count = Math.Min(EncoderSamplesPerFrame, remaining);
      CopyFrame(interleaved, frame, sourceFrame, count, options.Channels);
      if (count < EncoderSamplesPerFrame)
        PadFrame(frame, count, options.Channels);

      output.Write(EncodeFrame(frame, options));
    }

    return output.ToArray();
  }

  private static byte[] EncodeFrame(ReadOnlySpan<short> pcm, DtsEncoderOptions options) {
    var bitRateIndex = Array.IndexOf(DtsTables.BitRates, options.Bitrate);
    var sampleRateCode = Array.IndexOf(DtsTables.SampleRates, options.SampleRate);
    var frameBits = checked((int)((((long)options.Bitrate * EncoderSamplesPerFrame + options.SampleRate - 1) / options.SampleRate + 31) & ~31L));
    var frameBytes = (frameBits + 7) >> 3;
    if (frameBytes > 0x4000)
      throw new ArgumentOutOfRangeException(nameof(options), "DTS core frame exceeds the 14-bit FSIZE field.");

    var active = options.ActiveSubbands;
    var subband = AnalyzeSubbands(pcm, options.Channels, active);
    var scales = new int[options.Channels][];
    var quantized = new sbyte[options.Channels][][][];
    for (var ch = 0; ch < options.Channels; ++ch) {
      scales[ch] = new int[active];
      quantized[ch] = new sbyte[2][][];
      for (var block = 0; block < 2; ++block) {
        quantized[ch][block] = new sbyte[active][];
        for (var band = 0; band < active; ++band)
          quantized[ch][block][band] = new sbyte[8];
      }

      for (var band = 0; band < active; ++band) {
        var peak = 0f;
        for (var i = 0; i < EncoderSubbandSamples; ++i)
          peak = Math.Max(peak, MathF.Abs(subband[ch][band][i]));
        var scaleIndex = SelectScaleFactor(peak);
        scales[ch][band] = scaleIndex;
        var step = DtsTables.LossyQuant[EncoderQuantBits] * DtsTables.ScaleFactorQuant7[scaleIndex];
        if (step <= 0)
          step = 1;
        for (var i = 0; i < EncoderSubbandSamples; ++i) {
          var q = (int)MathF.Round(subband[ch][band][i] / step);
          q = Math.Clamp(q, -(1 << (EncoderSampleBits - 1)), (1 << (EncoderSampleBits - 1)) - 1);
          quantized[ch][i >> 3][band][i & 7] = (sbyte)q;
        }
      }
    }

    var writer = new DtsBitWriter(frameBytes);
    WriteCoreHeader(writer, frameBytes, options, sampleRateCode, bitRateIndex);
    WritePrimaryAudioHeader(writer, options.Channels, active);
    WriteSubframeHeader(writer, options.Channels, active, scales);

    for (var block = 0; block < 2; ++block) {
      for (var ch = 0; ch < options.Channels; ++ch)
        for (var band = 0; band < active; ++band)
          for (var i = 0; i < 8; ++i)
            writer.WriteSigned(quantized[ch][block][band][i], EncoderSampleBits);
      if (block == 1)
        writer.WriteBits(0xFFFF, 16);
    }

    if (writer.BitPosition > frameBits)
      throw new ArgumentOutOfRangeException(nameof(options),
        $"DTS bitrate {options.Bitrate} bit/s is too small for {options.Channels} channels and {active} active subbands.");
    return writer.Buffer;
  }

  private static void WriteCoreHeader(DtsBitWriter writer, int frameBytes, DtsEncoderOptions options, int sampleRateCode, int bitRateIndex) {
    writer.WriteBits(0x7FFE8001, 32); // sync
    writer.WriteBits(1, 1);          // FTYPE: normal frame
    writer.WriteBits(31, 5);         // SHORT: no deficit samples
    writer.WriteBits(0, 1);          // CPF: no header CRC
    writer.WriteBits(15, 7);         // NBLKS = 16 sample blocks = 512 PCM samples
    writer.WriteBits((uint)(frameBytes - 1), 14);
    writer.WriteBits((uint)AmodeForChannels(options.Channels), 6);
    writer.WriteBits((uint)sampleRateCode, 4);
    writer.WriteBits((uint)bitRateIndex, 5);
    writer.WriteBits(0, 1);          // fixed bit
    writer.WriteBits(0, 1);          // DYNF
    writer.WriteBits(0, 1);          // TIMEF
    writer.WriteBits(0, 1);          // AUXF
    writer.WriteBits(0, 1);          // HDCD
    writer.WriteBits(0, 3);          // extension audio id
    writer.WriteBits(0, 1);          // extension audio absent
    writer.WriteBits(0, 1);          // ASPF: DSYNC only at end of subframe
    writer.WriteBits(0, 2);          // no LFE
    writer.WriteBits(0, 1);          // no predictor history
    writer.WriteBits(0, 1);          // non-perfect reconstruction QMF
    writer.WriteBits(7, 4);          // encoder software revision
    writer.WriteBits(0, 2);          // copy history
    writer.WriteBits(0, 3);          // source PCM resolution code
    writer.WriteBits(0, 1);          // front sum/difference
    writer.WriteBits(0, 1);          // surround sum/difference
    writer.WriteBits(0, 4);          // dialog normalization
    writer.WriteBits(0, 4);          // one subframe
  }

  private static void WritePrimaryAudioHeader(DtsBitWriter writer, int channels, int activeSubbands) {
    writer.WriteBits((uint)(channels - 1), 3);
    for (var ch = 0; ch < channels; ++ch)
      writer.WriteBits((uint)(activeSubbands - 2), 5);
    for (var ch = 0; ch < channels; ++ch)
      writer.WriteBits((uint)(activeSubbands - 1), 5); // VQ start == active => no high-frequency VQ
    for (var ch = 0; ch < channels; ++ch)
      writer.WriteBits(0, 3); // no joint intensity
    for (var ch = 0; ch < channels; ++ch)
      writer.WriteBits(0, 2); // transition-mode VLC table 0; symbol zero is one zero bit
    for (var ch = 0; ch < channels; ++ch)
      writer.WriteBits(6, 3); // direct 7-bit scale factors
    for (var ch = 0; ch < channels; ++ch)
      writer.WriteBits(6, 3); // direct 5-bit bit allocation

    for (var group = 0; group < QuantSelectorWidths.Length; ++group)
      for (var ch = 0; ch < channels; ++ch)
        writer.WriteBits((uint)RawQuantSelectors[group], QuantSelectorWidths[group]);
    // Selecting exactly the group-size sentinel means "no Huffman" and therefore carries no
    // scale-factor-adjust field; this mirrors FFmpeg dcaenc's initialization.
  }

  private static void WriteSubframeHeader(DtsBitWriter writer, int channels, int activeSubbands, int[][] scales) {
    writer.WriteBits(1, 2); // two sub-subframes
    writer.WriteBits(0, 3); // no partial samples

    for (var ch = 0; ch < channels; ++ch)
      for (var band = 0; band < activeSubbands; ++band)
        writer.WriteBits(0, 1); // no ADPCM prediction

    for (var ch = 0; ch < channels; ++ch)
      for (var band = 0; band < activeSubbands; ++band)
        writer.WriteBits(EncoderQuantBits, 5);

    for (var ch = 0; ch < channels; ++ch)
      for (var band = 0; band < activeSubbands; ++band)
        writer.WriteBits(0, 1); // transition mode 0 in VLC table 0

    for (var ch = 0; ch < channels; ++ch)
      for (var band = 0; band < activeSubbands; ++band)
        writer.WriteBits((uint)scales[ch][band], 7);
  }

  private static float[][][] AnalyzeSubbands(ReadOnlySpan<short> pcm, int channels, int activeSubbands) {
    var result = new float[channels][][];
    for (var ch = 0; ch < channels; ++ch) {
      result[ch] = new float[activeSubbands][];
      for (var band = 0; band < activeSubbands; ++band)
        result[ch][band] = new float[EncoderSubbandSamples];
    }

    for (var slot = 0; slot < EncoderSubbandSamples; ++slot) {
      var sampleBase = slot * EncoderSubbands;
      for (var ch = 0; ch < channels; ++ch) {
        for (var band = 0; band < activeSubbands; ++band) {
          double sum = 0;
          for (var n = 0; n < EncoderSubbands; ++n) {
            var sample = pcm[(sampleBase + n) * channels + ch];
            sum += sample * Math.Cos(Math.PI / EncoderSubbands * (n + 0.5) * (band + 0.5));
          }
          var value = (float)(sum / 16.0);
          // Compensate the synthesis-side DTS subband sign convention.
          result[ch][band][slot] = ((band - 1) & 2) != 0 ? -value : value;
        }
      }
    }
    return result;
  }

  private static int SelectScaleFactor(float peak) {
    if (peak <= 0)
      return 0;
    var required = peak / (((1 << (EncoderSampleBits - 1)) - 1) * DtsTables.LossyQuant[EncoderQuantBits]);
    var table = DtsTables.ScaleFactorQuant7;
    for (var i = 0; i < table.Length; ++i)
      if (table[i] != 0 && table[i] >= required)
        return i;
    return 124; // largest non-zero entry in the 7-bit DTS scale-factor table
  }

  private static int AmodeForChannels(int channels) => channels switch {
    1 => 0,
    2 => 2,
    4 => 8,
    5 => 9,
    _ => throw new ArgumentOutOfRangeException(nameof(channels)),
  };

  private static void ValidateEncoder(int sampleCount, DtsEncoderOptions options) {
    if (options.Channels is not (1 or 2 or 4 or 5))
      throw new ArgumentOutOfRangeException(nameof(options), "Managed DTS core encoding currently supports mono, stereo, quad and 5.0 layouts.");
    if (sampleCount % options.Channels != 0)
      throw new ArgumentException("Interleaved PCM sample count must be divisible by the channel count.");
    if (Array.IndexOf(DtsTables.SampleRates, options.SampleRate) is < 0 or 0)
      throw new ArgumentOutOfRangeException(nameof(options), "Unsupported DTS core sample rate.");
    if (options.SampleRate > 48000)
      throw new ArgumentOutOfRangeException(nameof(options), "The managed DTS core encoder currently targets the classic <=48 kHz core rates.");
    if (Array.IndexOf(DtsTables.BitRates, options.Bitrate) is < 0 or > 28)
      throw new ArgumentOutOfRangeException(nameof(options), "DTS bitrate must be one of the fixed core transmission rates (32 kbit/s .. 3.84 Mbit/s).");
    if (options.ActiveSubbands is < 2 or > EncoderSubbands)
      throw new ArgumentOutOfRangeException(nameof(options), "DTS active subband count must be 2..32.");
    if (!options.PadFinalFrame && sampleCount / options.Channels % EncoderSamplesPerFrame != 0)
      throw new ArgumentException($"DTS PCM must contain whole {EncoderSamplesPerFrame}-sample frames when padding is disabled.");
  }

  private static void CopyFrame(ReadOnlySpan<short> source, Span<short> destination, int sourceFrame, int samplesPerChannel, int channels) {
    destination.Clear();
    var sourceOffset = sourceFrame * channels;
    source.Slice(sourceOffset, samplesPerChannel * channels).CopyTo(destination);
  }

  private static void PadFrame(Span<short> frame, int samplesPerChannel, int channels) {
    if (samplesPerChannel <= 0)
      return;
    for (var ch = 0; ch < channels; ++ch) {
      var value = frame[(samplesPerChannel - 1) * channels + ch];
      for (var sample = samplesPerChannel; sample < EncoderSamplesPerFrame; ++sample)
        frame[sample * channels + ch] = value;
    }
  }

  private sealed class DtsBitWriter {
    private readonly byte[] _buffer;
    private int _bitPosition;

    public DtsBitWriter(int bytes) => this._buffer = new byte[bytes];

    public byte[] Buffer => this._buffer;
    public int BitPosition => this._bitPosition;

    public void WriteBits(uint value, int count) {
      if (count is < 0 or > 32)
        throw new ArgumentOutOfRangeException(nameof(count));
      if (this._bitPosition + count > this._buffer.Length * 8)
        throw new InvalidDataException("DTS frame bit budget exhausted.");
      for (var bit = count - 1; bit >= 0; --bit) {
        if (((value >> bit) & 1u) != 0)
          this._buffer[this._bitPosition >> 3] |= (byte)(1 << (7 - (this._bitPosition & 7)));
        ++this._bitPosition;
      }
    }

    public void WriteSigned(int value, int count) {
      var mask = (1u << count) - 1u;
      this.WriteBits((uint)value & mask, count);
    }
  }
}
