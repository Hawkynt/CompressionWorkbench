#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>Controls legacy ATSC A/52 AC-3 encoding.</summary>
/// <param name="SampleRate">32000, 44100 or 48000 Hz.</param>
/// <param name="Bitrate">One of the standard AC-3 bitrates from 32 to 640 kbit/s.</param>
/// <param name="Acmod">A/52 audio coding mode 1..7. The input channel order follows that mode.</param>
/// <param name="LowFrequencyEffects">When true, the final interleaved input channel is encoded as LFE.</param>
/// <param name="DialNorm">Dialogue normalization metadata in dB, -31..-1.</param>
/// <param name="Cutoff">Full-bandwidth channel cutoff in Hz; zero chooses a bitrate-dependent value.</param>
/// <param name="PadFinalFrame">Pad an incomplete 1536-sample final frame with its last sample.</param>
public sealed record Ac3EncoderOptions(
  int SampleRate = 48000,
  int Bitrate = 192000,
  int Acmod = 2,
  bool LowFrequencyEffects = false,
  int DialNorm = -31,
  int Cutoff = 0,
  bool PadFinalFrame = true
);

/// <summary>
/// Represents an ac 3 codec.
/// </summary>
public static partial class Ac3Codec {

  /// <summary>Long-block AC-3 analysis delay in samples per channel.</summary>
  public const int EncoderDelaySamples = SamplesPerBlock;

  private const int SamplesPerFrame = BlocksPerFrame * SamplesPerBlock;
  private const uint Crc16Polynomial = 0x18005;

  private static readonly int[] Ac3Bitrates = [
    32000, 40000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 160000,
    192000, 224000, 256000, 320000, 384000, 448000, 512000, 576000, 640000,
  ];

  private static readonly float[] MdctCos = BuildMdctCos();
  private static readonly ushort[] Crc16AnsiTable = BuildCrc16AnsiTable();

  /// <summary>
  /// Encodes interleaved PCM16 as legacy AC-3 (bsid 8). The implementation is a managed adaptation
  /// of FFmpeg's LGPL <c>ac3enc.c</c>: long-block MDCT analysis, D45 exponent grouping/reuse,
  /// standards-defined parametric bit allocation, coarse/fine SNR rate control, grouped and linear
  /// mantissa quantizers, 44.1-kHz alternating frame sizes, and both A/52 CRC fields. Coupling,
  /// rematrixing and short-block switching are deliberately disabled so the core path remains
  /// deterministic and every channel is coded independently.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, Ac3EncoderOptions? options = null) {
    options ??= new Ac3EncoderOptions();
    var channels = ValidateEncoder(interleaved.Length, options);
    if (interleaved.IsEmpty)
      return [];

    var samplesPerChannel = interleaved.Length / channels;
    var frameCount = (samplesPerChannel + SamplesPerFrame - 1) / SamplesPerFrame;
    var history = new float[channels][];
    for (var ch = 0; ch < channels; ++ch)
      history[ch] = new float[SamplesPerBlock];

    using var output = new MemoryStream();
    var framePcm = new short[SamplesPerFrame * channels];
    double frameSizeAccumulator = 0;

    for (var frameIndex = 0; frameIndex < frameCount; ++frameIndex) {
      var sourceStart = frameIndex * SamplesPerFrame;
      var count = Math.Min(SamplesPerFrame, samplesPerChannel - sourceStart);
      CopyAc3Frame(interleaved, framePcm, sourceStart, count, channels);
      if (count < SamplesPerFrame)
        PadAc3Frame(framePcm, count, channels);

      var layout = GetFrameLayout(options.SampleRate, options.Bitrate, ref frameSizeAccumulator);
      var encoded = EncodeFrame(framePcm, channels, options, layout, history);
      output.Write(encoded);
    }

    return output.ToArray();
  }

  private readonly record struct FrameLayout(int FsCod, int FrameSizeCode, int FrameBytes);
  private sealed record ChannelCoding(int EndMantissa, byte[] Exponents, byte AbsoluteExponent, byte[] GroupedExponents);

  private static byte[] EncodeFrame(
    ReadOnlySpan<short> pcm,
    int channels,
    Ac3EncoderOptions options,
    FrameLayout layout,
    float[][] history) {

    var fullBandwidthChannels = Ac3FrameHeader.AcmodChannelCount(options.Acmod);
    var lfeIndex = options.LowFrequencyEffects ? channels - 1 : -1;
    var bandwidthCode = ResolveBandwidthCode(options, fullBandwidthChannels);
    var endMantissa = bandwidthCode * 3 + 73;
    var coefficients = AnalyzeFrame(pcm, channels, history);
    var coding = new ChannelCoding[channels];

    for (var ch = 0; ch < channels; ++ch) {
      var end = ch == lfeIndex ? 7 : endMantissa;
      var strategy = ch == lfeIndex ? Ac3Exponents.Strategy.D15 : Ac3Exponents.Strategy.D45;
      coding[ch] = BuildChannelCoding(coefficients, ch, end, strategy);
    }

    // FFmpeg's legacy defaults. Keeping these fixed lets us share the exact decoder-side bit
    // allocation implementation and vary only the SNR offset during the rate search.
    const int slowDecayCode = 2;
    const int fastDecayCode = 1;
    const int slowGainCode = 1;
    const int dbPerBitCode = 3;
    const int floorCode = 7;
    const int fastGainCode = 4;
    var allocation = Ac3BitAllocation.Resolve(slowDecayCode, fastDecayCode, slowGainCode, dbPerBitCode, floorCode);

    // Find the highest coarse SNR setting whose complete syntax + mantissas fits the fixed frame.
    // Once that coarse bucket fits, refine inside it with the 4-bit fine offset.
    var selectedCoarse = -1;
    byte[]? selectedFrame = null;
    for (var coarse = 63; coarse >= 0; --coarse) {
      var candidate = BuildCandidate(coarse, 0);
      if (candidate is null)
        continue;
      selectedCoarse = coarse;
      selectedFrame = candidate;
      break;
    }

    if (selectedFrame is null)
      throw new ArgumentOutOfRangeException(nameof(options),
        $"AC-3 bitrate {options.Bitrate} bit/s is too small for acmod {options.Acmod}" +
        (options.LowFrequencyEffects ? " + LFE" : string.Empty) + ".");

    for (var fine = 15; fine > 0; --fine) {
      var candidate = BuildCandidate(selectedCoarse, fine);
      if (candidate is null)
        continue;
      selectedFrame = candidate;
      break;
    }

    return selectedFrame;

    byte[]? BuildCandidate(int coarseSnr, int fineSnr) {
      var baps = new byte[channels][];
      var snrOffset = (((coarseSnr - 15) << 4) + fineSnr) << 2;
      for (var ch = 0; ch < channels; ++ch) {
        baps[ch] = new byte[256];
        Ac3BitAllocation.ComputeBap(
          coding[ch].Exponents,
          baps[ch],
          0,
          coding[ch].EndMantissa,
          allocation,
          Ac3Tables.FastGain[fastGainCode],
          snrOffset,
          layout.FsCod,
          isCoupling: false,
          0,
          0,
          null);
      }

      // A high-SNR candidate can be much larger than the final frame. Use a generously-sized
      // scratch writer, then accept it only if two bytes remain for CRC2.
      var writer = new Ac3BitWriter(65536);
      WriteFrameHeader(writer, options, layout);
      for (var block = 0; block < BlocksPerFrame; ++block)
        WriteAudioBlock(writer, block, channels, fullBandwidthChannels, lfeIndex, bandwidthCode,
          coding, baps, coefficients, coarseSnr, fineSnr,
          slowDecayCode, fastDecayCode, slowGainCode, dbPerBitCode, floorCode, fastGainCode);

      if (writer.BitPosition > layout.FrameBytes * 8 - 16)
        return null;

      var result = new byte[layout.FrameBytes];
      Array.Copy(writer.Buffer, result, Math.Min(result.Length, writer.Buffer.Length));
      FinalizeCrc(result);
      return result;
    }
  }

  private static float[][][] AnalyzeFrame(ReadOnlySpan<short> pcm, int channels, float[][] history) {
    var result = new float[BlocksPerFrame][][];
    var window = Ac3Tables.Window;
    Span<float> time = stackalloc float[512];

    for (var block = 0; block < BlocksPerFrame; ++block) {
      result[block] = new float[channels][];
      for (var ch = 0; ch < channels; ++ch) {
        var current = new float[SamplesPerBlock];
        for (var n = 0; n < SamplesPerBlock; ++n)
          current[n] = pcm[(block * SamplesPerBlock + n) * channels + ch] / 32768f;

        for (var n = 0; n < SamplesPerBlock; ++n) {
          time[n] = history[ch][n] * window[n];
          time[SamplesPerBlock + n] = current[n] * window[SamplesPerBlock - 1 - n];
        }

        var coeff = new float[256];
        for (var k = 0; k < 256; ++k) {
          double sum = 0;
          var row = k * 512;
          for (var n = 0; n < 512; ++n)
            sum += time[n] * MdctCos[row + n];
          coeff[k] = (float)(sum * (2.0 / 256.0));
        }
        result[block][ch] = coeff;
        current.CopyTo(history[ch], 0);
      }
    }

    return result;
  }

  private static ChannelCoding BuildChannelCoding(
    float[][][] coefficients,
    int channel,
    int endMantissa,
    Ac3Exponents.Strategy strategy) {

    var raw = new byte[256];
    Array.Fill(raw, (byte)24);
    for (var bin = 0; bin < endMantissa; ++bin) {
      var peak = 0f;
      for (var block = 0; block < BlocksPerFrame; ++block)
        peak = Math.Max(peak, MathF.Abs(coefficients[block][channel][bin]));
      if (peak <= 1e-12f) {
        raw[bin] = 24;
        continue;
      }
      var exponent = (int)MathF.Floor(-MathF.Log2(peak));
      raw[bin] = (byte)Math.Clamp(exponent, 0, 24);
    }

    var step = Ac3Exponents.GroupSize(strategy);
    var wordCount = Ac3Exponents.GroupCount(endMantissa, strategy);
    var groupCount = 1 + wordCount * 3;
    var groups = new byte[groupCount];
    for (var group = 0; group < groupCount; ++group) {
      var start = group * step;
      var minimum = 24;
      for (var i = 0; i < step; ++i) {
        var bin = Math.Min(start + i, Math.Max(0, endMantissa - 1));
        minimum = Math.Min(minimum, raw[bin]);
      }
      groups[group] = (byte)minimum;
    }

    groups[0] = (byte)Math.Min((int)groups[0], 15);
    for (var i = 1; i < groups.Length; ++i)
      groups[i] = (byte)Math.Min(groups[i], groups[i - 1] + 2);
    for (var i = groups.Length - 2; i >= 0; --i)
      groups[i] = (byte)Math.Min(groups[i], groups[i + 1] + 2);

    var expanded = new byte[256];
    for (var group = 0; group < groups.Length; ++group) {
      var start = group * step;
      for (var i = 0; i < step && start + i < expanded.Length; ++i)
        expanded[start + i] = groups[group];
    }
    if (groups.Length * step < expanded.Length)
      Array.Fill(expanded, groups[^1], groups.Length * step, expanded.Length - groups.Length * step);

    var packed = new byte[wordCount];
    for (var word = 0; word < wordCount; ++word) {
      var g = 1 + word * 3;
      var d0 = Math.Clamp(groups[g] - groups[g - 1] + 2, 0, 4);
      var d1 = Math.Clamp(groups[g + 1] - groups[g] + 2, 0, 4);
      var d2 = Math.Clamp(groups[g + 2] - groups[g + 1] + 2, 0, 4);
      packed[word] = (byte)((d0 * 5 + d1) * 5 + d2);
    }

    return new ChannelCoding(endMantissa, expanded, groups[0], packed);
  }

  private static void WriteFrameHeader(Ac3BitWriter writer, Ac3EncoderOptions options, FrameLayout layout) {
    writer.WriteBits(0x0B77, 16);
    writer.WriteBits(0, 16); // CRC1 is patched after the frame is complete.
    writer.WriteBits((uint)layout.FsCod, 2);
    writer.WriteBits((uint)layout.FrameSizeCode, 6);
    writer.WriteBits(8, 5);  // bsid: standard AC-3
    writer.WriteBits(0, 3);  // bsmod: complete main audio service
    writer.WriteBits((uint)options.Acmod, 3);
    if ((options.Acmod & 1) != 0 && options.Acmod != 1)
      writer.WriteBits(1, 2); // center mix level: -4.5 dB
    if ((options.Acmod & 4) != 0)
      writer.WriteBits(1, 2); // surround mix level: -6 dB
    if (options.Acmod == 2)
      writer.WriteBits(0, 2); // Dolby Surround mode not indicated
    writer.WriteBits(options.LowFrequencyEffects ? 1u : 0u, 1);
    writer.WriteBits((uint)-options.DialNorm, 5);
    writer.WriteBits(0, 1); // compre
    writer.WriteBits(0, 1); // langcode
    writer.WriteBits(0, 1); // audprodie
    writer.WriteBits(0, 1); // copyright
    writer.WriteBits(1, 1); // original bitstream
    writer.WriteBits(0, 1); // timecod1e
    writer.WriteBits(0, 1); // timecod2e
    writer.WriteBits(0, 1); // addbsie
  }

  private static void WriteAudioBlock(
    Ac3BitWriter writer,
    int block,
    int channels,
    int fullBandwidthChannels,
    int lfeIndex,
    int bandwidthCode,
    ChannelCoding[] coding,
    byte[][] baps,
    float[][][] coefficients,
    int coarseSnr,
    int fineSnr,
    int slowDecayCode,
    int fastDecayCode,
    int slowGainCode,
    int dbPerBitCode,
    int floorCode,
    int fastGainCode) {

    for (var ch = 0; ch < fullBandwidthChannels; ++ch)
      writer.WriteBits(0, 1); // long block
    for (var ch = 0; ch < fullBandwidthChannels; ++ch)
      writer.WriteBits(0, 1); // no dither for bap zero
    writer.WriteBits(0, 1);   // dynrnge

    writer.WriteBits(1, 1);   // new coupling strategy
    writer.WriteBits(0, 1);   // coupling disabled

    // acmod is implied by the number/layout of full-band channels. Rematrix syntax only exists for 2/0.
    if (fullBandwidthChannels == 2 && channels - (lfeIndex >= 0 ? 1 : 0) == 2)
      writer.WriteBits(0, 1); // rematstr

    for (var ch = 0; ch < fullBandwidthChannels; ++ch)
      writer.WriteBits(block == 0 ? 3u : 0u, 2); // D45 then reuse
    if (lfeIndex >= 0)
      writer.WriteBits(block == 0 ? 1u : 0u, 1); // LFE D15 then reuse

    if (block == 0) {
      for (var ch = 0; ch < fullBandwidthChannels; ++ch)
        writer.WriteBits((uint)bandwidthCode, 6);

      for (var ch = 0; ch < fullBandwidthChannels; ++ch) {
        WriteExponents(writer, coding[ch]);
        writer.WriteBits(0, 2); // gainrng
      }
      if (lfeIndex >= 0)
        WriteExponents(writer, coding[lfeIndex]);
    }

    writer.WriteBits(block == 0 ? 1u : 0u, 1); // baie
    if (block == 0) {
      writer.WriteBits((uint)slowDecayCode, 2);
      writer.WriteBits((uint)fastDecayCode, 2);
      writer.WriteBits((uint)slowGainCode, 2);
      writer.WriteBits((uint)dbPerBitCode, 2);
      writer.WriteBits((uint)floorCode, 3);
    }

    writer.WriteBits(block == 0 ? 1u : 0u, 1); // snroffste
    if (block == 0) {
      writer.WriteBits((uint)coarseSnr, 6);
      for (var ch = 0; ch < fullBandwidthChannels; ++ch) {
        writer.WriteBits((uint)fineSnr, 4);
        writer.WriteBits((uint)fastGainCode, 3);
      }
      if (lfeIndex >= 0) {
        writer.WriteBits((uint)fineSnr, 4);
        writer.WriteBits((uint)fastGainCode, 3);
      }
    }

    writer.WriteBits(0, 1); // deltbaie
    writer.WriteBits(0, 1); // skiple

    WriteMantissas(writer, block, channels, coding, baps, coefficients);
  }

  private static void WriteExponents(Ac3BitWriter writer, ChannelCoding coding) {
    writer.WriteBits(coding.AbsoluteExponent, 4);
    foreach (var packed in coding.GroupedExponents)
      writer.WriteBits(packed, 7);
  }

  private readonly record struct MantissaItem(int Bap, float Value);

  private static void WriteMantissas(
    Ac3BitWriter writer,
    int block,
    int channels,
    ChannelCoding[] coding,
    byte[][] baps,
    float[][][] coefficients) {

    var items = new List<MantissaItem>(channels * 253);
    for (var ch = 0; ch < channels; ++ch) {
      var end = coding[ch].EndMantissa;
      for (var bin = 0; bin < end; ++bin) {
        var bap = baps[ch][bin];
        if (bap == 0)
          continue;
        var scaled = coefficients[block][ch][bin] * MathF.Pow(2f, coding[ch].Exponents[bin]);
        items.Add(new MantissaItem(bap, Math.Clamp(scaled, -0.999969f, 0.999969f)));
      }
    }

    var q1 = QuantizedValues(items, 1, Ac3Tables.Quant3);
    var q2 = QuantizedValues(items, 2, Ac3Tables.Quant5);
    var q4 = QuantizedValues(items, 4, Ac3Tables.Quant11);
    var i1 = 0;
    var i2 = 0;
    var i4 = 0;

    foreach (var item in items) {
      switch (item.Bap) {
        case 1:
          if (i1 % 3 == 0) {
            var a = q1[i1];
            var b = i1 + 1 < q1.Count ? q1[i1 + 1] : 1;
            var c = i1 + 2 < q1.Count ? q1[i1 + 2] : 1;
            writer.WriteBits((uint)((a * 3 + b) * 3 + c), 5);
          }
          ++i1;
          break;
        case 2:
          if (i2 % 3 == 0) {
            var a = q2[i2];
            var b = i2 + 1 < q2.Count ? q2[i2 + 1] : 2;
            var c = i2 + 2 < q2.Count ? q2[i2 + 2] : 2;
            writer.WriteBits((uint)((a * 5 + b) * 5 + c), 7);
          }
          ++i2;
          break;
        case 3:
          writer.WriteBits((uint)Nearest(Ac3Tables.Quant7, item.Value), 3);
          break;
        case 4:
          if ((i4 & 1) == 0) {
            var a = q4[i4];
            var b = i4 + 1 < q4.Count ? q4[i4 + 1] : 5;
            writer.WriteBits((uint)(a * 11 + b), 7);
          }
          ++i4;
          break;
        case 5:
          writer.WriteBits((uint)Nearest(Ac3Tables.Quant15, item.Value), 4);
          break;
        default: {
          var bits = Ac3Tables.QuantizationBits[item.Bap];
          var scale = 1 << (bits - 1);
          var raw = Math.Clamp((int)MathF.Round(item.Value * scale), -scale, scale - 1);
          writer.WriteSigned(raw, bits);
          break;
        }
      }
    }
  }

  private static List<int> QuantizedValues(List<MantissaItem> items, int bap, float[] levels) {
    var result = new List<int>();
    foreach (var item in items)
      if (item.Bap == bap)
        result.Add(Nearest(levels, item.Value));
    return result;
  }

  private static int Nearest(float[] levels, float value) {
    var best = 0;
    var bestError = float.PositiveInfinity;
    for (var i = 0; i < levels.Length; ++i) {
      var error = MathF.Abs(levels[i] - value);
      if (error >= bestError)
        continue;
      bestError = error;
      best = i;
    }
    return best;
  }

  private static int ResolveBandwidthCode(Ac3EncoderOptions options, int fullBandwidthChannels) {
    var cutoff = options.Cutoff;
    if (cutoff == 0) {
      var bitsPerChannel = options.Bitrate / Math.Max(1, fullBandwidthChannels);
      cutoff = Math.Min((int)(options.SampleRate * 0.47), 5000 + bitsPerChannel / 8);
    }
    var coefficients = cutoff * 2 * 256 / options.SampleRate;
    return Math.Clamp((coefficients - 73) / 3, 0, 60);
  }

  private static FrameLayout GetFrameLayout(int sampleRate, int bitrate, ref double accumulator) {
    var bitrateIndex = Array.IndexOf(Ac3Bitrates, bitrate);
    var kbps = bitrate / 1000;
    return sampleRate switch {
      48000 => new FrameLayout(0, bitrateIndex * 2, kbps * 4),
      32000 => new FrameLayout(2, bitrateIndex * 2, kbps * 6),
      44100 => Get44100Layout(bitrateIndex, bitrate, ref accumulator),
      _ => throw new ArgumentOutOfRangeException(nameof(sampleRate)),
    };
  }

  private static FrameLayout Get44100Layout(int bitrateIndex, int bitrate, ref double accumulator) {
    var exactWords = bitrate * (double)SamplesPerFrame / (44100 * 16.0);
    var lowWords = (int)Math.Floor(exactWords);
    var fraction = exactWords - lowWords;
    accumulator += fraction;
    var high = accumulator >= 1.0;
    if (high)
      accumulator -= 1.0;
    var words = lowWords + (high ? 1 : 0);
    return new FrameLayout(1, bitrateIndex * 2 + (high ? 1 : 0), words * 2);
  }

  private static int ValidateEncoder(int sampleCount, Ac3EncoderOptions options) {
    if (options.Acmod is < 1 or > 7)
      throw new ArgumentOutOfRangeException(nameof(options), "AC-3 acmod 1..7 is supported; dual-mono acmod 0 remains decoder-incomplete.");
    var channels = Ac3FrameHeader.AcmodChannelCount(options.Acmod) + (options.LowFrequencyEffects ? 1 : 0);
    if (channels is < 1 or > 6)
      throw new ArgumentOutOfRangeException(nameof(options), "Invalid AC-3 channel layout.");
    if (sampleCount % channels != 0)
      throw new ArgumentException("Interleaved PCM sample count must be divisible by the AC-3 channel count.");
    if (options.SampleRate is not (32000 or 44100 or 48000))
      throw new ArgumentOutOfRangeException(nameof(options), "Legacy AC-3 supports 32, 44.1 and 48 kHz in this encoder.");
    if (Array.IndexOf(Ac3Bitrates, options.Bitrate) < 0)
      throw new ArgumentOutOfRangeException(nameof(options), "Bitrate must be a standard AC-3 rate from 32 to 640 kbit/s.");
    if (options.DialNorm is < -31 or > -1)
      throw new ArgumentOutOfRangeException(nameof(options), "dialnorm must be in the range -31..-1 dB.");
    if (options.Cutoff < 0 || options.Cutoff > options.SampleRate / 2)
      throw new ArgumentOutOfRangeException(nameof(options), "Cutoff must be zero (automatic) or within the Nyquist limit.");
    if (!options.PadFinalFrame && sampleCount / channels % SamplesPerFrame != 0)
      throw new ArgumentException($"AC-3 PCM must contain whole {SamplesPerFrame}-sample frames when padding is disabled.");
    return channels;
  }

  private static void CopyAc3Frame(ReadOnlySpan<short> source, Span<short> destination, int sourceFrame, int samplesPerChannel, int channels) {
    destination.Clear();
    source.Slice(sourceFrame * channels, samplesPerChannel * channels).CopyTo(destination);
  }

  private static void PadAc3Frame(Span<short> frame, int samplesPerChannel, int channels) {
    if (samplesPerChannel <= 0)
      return;
    for (var ch = 0; ch < channels; ++ch) {
      var value = frame[(samplesPerChannel - 1) * channels + ch];
      for (var sample = samplesPerChannel; sample < SamplesPerFrame; ++sample)
        frame[sample * channels + ch] = value;
    }
  }

  private static float[] BuildMdctCos() {
    var table = new float[256 * 512];
    for (var k = 0; k < 256; ++k)
      for (var n = 0; n < 512; ++n)
        table[k * 512 + n] = (float)Math.Cos(Math.PI / 512.0 * (2 * n + 1 + 256) * (2 * k + 1));
    return table;
  }

  private static void FinalizeCrc(byte[] frame) {
    var frameSize58 = ((frame.Length >> 2) + (frame.Length >> 4)) << 1;
    var crc1 = Swap16(Crc16Ansi(frame.AsSpan(4, frameSize58 - 4)));
    var crcInverse = PowPoly(Crc16Polynomial >> 1, (8 * frameSize58) - 16, Crc16Polynomial);
    crc1 = (ushort)MulPoly(crcInverse, crc1, Crc16Polynomial);
    WriteBigEndian16(frame, 2, crc1);

    var crc2 = Swap16(Crc16Ansi(frame.AsSpan(frameSize58, frame.Length - frameSize58 - 2)));
    if (crc2 == 0x0B77) {
      frame[^3] ^= 0x01;
      crc2 ^= 0x8005;
    }
    WriteBigEndian16(frame, frame.Length - 2, crc2);
  }

  private static ushort[] BuildCrc16AnsiTable() {
    var table = new ushort[256];
    const uint polynomial = 0x80050000;
    for (uint i = 0; i < 256; ++i) {
      var c = i << 24;
      for (var bit = 0; bit < 8; ++bit)
        c = (c << 1) ^ (((int)c < 0) ? polynomial : 0u);
      var swapped = ((c & 0x000000FFu) << 24) |
                    ((c & 0x0000FF00u) << 8) |
                    ((c & 0x00FF0000u) >> 8) |
                    ((c & 0xFF000000u) >> 24);
      table[i] = (ushort)swapped;
    }
    return table;
  }

  private static ushort Crc16Ansi(ReadOnlySpan<byte> data) {
    uint crc = 0;
    foreach (var value in data)
      crc = Crc16AnsiTable[((byte)crc) ^ value] ^ (crc >> 8);
    return (ushort)crc;
  }

  private static ushort Swap16(ushort value) => (ushort)((value << 8) | (value >> 8));

  private static uint MulPoly(uint a, uint b, uint polynomial) {
    uint result = 0;
    while (a != 0) {
      if ((a & 1) != 0)
        result ^= b;
      a >>= 1;
      b <<= 1;
      if ((b & (1u << 16)) != 0)
        b ^= polynomial;
    }
    return result;
  }

  private static uint PowPoly(uint value, int exponent, uint polynomial) {
    uint result = 1;
    while (exponent != 0) {
      if ((exponent & 1) != 0)
        result = MulPoly(result, value, polynomial);
      value = MulPoly(value, value, polynomial);
      exponent >>= 1;
    }
    return result;
  }

  private static void WriteBigEndian16(Span<byte> data, int offset, ushort value) {
    data[offset] = (byte)(value >> 8);
    data[offset + 1] = (byte)value;
  }

  private sealed class Ac3BitWriter {
    private readonly byte[] _buffer;
    private int _bitPosition;

    public Ac3BitWriter(int bytes) => this._buffer = new byte[bytes];

    public byte[] Buffer => this._buffer;
    public int BitPosition => this._bitPosition;

    public void WriteBits(uint value, int count) {
      if (count is < 0 or > 32)
        throw new ArgumentOutOfRangeException(nameof(count));
      if (this._bitPosition + count > this._buffer.Length * 8)
        throw new InvalidDataException("AC-3 scratch bit buffer exhausted.");
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
