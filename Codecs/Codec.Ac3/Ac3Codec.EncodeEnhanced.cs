#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>Controls ATSC A/52 Enhanced AC-3 encoding of an independent substream.</summary>
/// <param name="SampleRate">16000, 22050, 24000, 32000, 44100 or 48000 Hz.</param>
/// <param name="Bitrate">Target average bitrate in bit/s. Frame sizes are word-aligned.</param>
/// <param name="Acmod">A/52 audio coding mode 0..7. The input channel order follows that mode.</param>
/// <param name="LowFrequencyEffects">When true, the final interleaved input channel is encoded as LFE.</param>
/// <param name="DialNorm">Primary-program dialogue normalization metadata in dB, -31..-1.</param>
/// <param name="Cutoff">Full-bandwidth channel cutoff in Hz; zero chooses a bitrate-dependent value.</param>
/// <param name="PadFinalFrame">Pad an incomplete final frame with its last sample.</param>
/// <param name="BlocksPerFrame">1, 2, 3 or 6; reduced-rate streams require 6; null selects automatically.</param>
/// <param name="DialNorm2">Dual-mono second-program dialogue normalization; null reuses <paramref name="DialNorm"/>.</param>
public sealed record Eac3EncoderOptions(
  int SampleRate = 48000,
  int Bitrate = 192000,
  int Acmod = 2,
  bool LowFrequencyEffects = false,
  int DialNorm = -31,
  int Cutoff = 0,
  bool PadFinalFrame = true,
  int? BlocksPerFrame = null,
  int? DialNorm2 = null
);

public static partial class Ac3Codec {

  /// <summary>
  /// Encodes interleaved PCM16 as an E-AC-3 independent substream (strmtyp 0, substreamid 0,
  /// bsid 16). The Annex E framing is written independently from legacy AC-3 while reusing the
  /// shared long-block MDCT, exponent coding, parametric bit allocation and mantissa quantizers.
  /// Coupling, spectral extension, AHT, rematrixing and short-block switching are disabled; every
  /// full-bandwidth channel is coded independently. Full-rate streams support 1/2/3/6-block
  /// syncframes; reduced 24/22.05/16-kHz streams use the Annex E six-block form.
  /// </summary>
  public static byte[] EncodeEnhanced(ReadOnlySpan<short> interleaved, Eac3EncoderOptions? options = null) {
    options ??= new Eac3EncoderOptions();
    var (channels, blocksPerFrame) = ValidateEnhancedEncoder(interleaved.Length, options);
    if (interleaved.IsEmpty)
      return [];

    var samplesPerFrame = blocksPerFrame * SamplesPerBlock;
    var samplesPerChannel = interleaved.Length / channels;
    var frameCount = (samplesPerChannel + samplesPerFrame - 1) / samplesPerFrame;
    var history = new float[channels][];
    for (var ch = 0; ch < channels; ++ch)
      history[ch] = new float[SamplesPerBlock];

    var framePcm = new short[samplesPerFrame * channels];
    using var output = new MemoryStream();
    double frameSizeAccumulator = 0;

    for (var frameIndex = 0; frameIndex < frameCount; ++frameIndex) {
      var sourceStart = frameIndex * samplesPerFrame;
      var count = Math.Min(samplesPerFrame, samplesPerChannel - sourceStart);
      CopyEnhancedFrame(interleaved, framePcm, sourceStart, count, channels);
      if (count < samplesPerFrame)
        PadEnhancedFrame(framePcm, count, samplesPerFrame, channels);

      var frameBytes = GetEnhancedFrameBytes(options.SampleRate, options.Bitrate, blocksPerFrame, ref frameSizeAccumulator);
      var encoded = EncodeEnhancedFrame(framePcm, channels, blocksPerFrame, frameIndex, options, frameBytes, history);
      output.Write(encoded);
    }

    return output.ToArray();
  }

  private static byte[] EncodeEnhancedFrame(
    ReadOnlySpan<short> pcm,
    int channels,
    int blocksPerFrame,
    int frameIndex,
    Eac3EncoderOptions options,
    int frameBytes,
    float[][] history) {

    var fullBandwidthChannels = Ac3FrameHeader.AcmodChannelCount(options.Acmod);
    var lfeIndex = options.LowFrequencyEffects ? channels - 1 : -1;
    var bandwidthCode = ResolveEnhancedBandwidthCode(options, fullBandwidthChannels);
    var endMantissa = bandwidthCode * 3 + 73;
    var sourceChannel = InputChannelOrder(options.Acmod, options.LowFrequencyEffects, channels);
    var coefficients = AnalyzeEnhancedFrame(pcm, channels, sourceChannel, history, blocksPerFrame);
    var coding = new ChannelCoding[channels];

    for (var ch = 0; ch < channels; ++ch) {
      var end = ch == lfeIndex ? 7 : endMantissa;
      var strategy = ch == lfeIndex ? Ac3Exponents.Strategy.D15 : Ac3Exponents.Strategy.D45;
      coding[ch] = BuildEnhancedChannelCoding(coefficients, ch, end, strategy, blocksPerFrame);
    }

    // Annex E defaults when bit_allocation_syntax is disabled.
    const int slowDecayCode = 2;
    const int fastDecayCode = 1;
    const int slowGainCode = 1;
    const int dbPerBitCode = 2;
    const int floorCode = 7;
    const int fastGainCode = 4;
    var allocation = Ac3BitAllocation.Resolve(slowDecayCode, fastDecayCode, slowGainCode, dbPerBitCode, floorCode);
    var sampleRate = Ac3BitAllocation.SampleRateContext.FromSampleRate(options.SampleRate);

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
        $"E-AC-3 bitrate {options.Bitrate} bit/s is too small for acmod {options.Acmod}, " +
        $"{blocksPerFrame} block(s) per frame" + (options.LowFrequencyEffects ? " + LFE" : string.Empty) + ".");

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
          sampleRate,
          isCoupling: false,
          0,
          0,
          null);
      }

      var writer = new Ac3BitWriter(65536);
      WriteEnhancedFrameHeader(writer, options, frameBytes, blocksPerFrame, frameIndex, coarseSnr, fineSnr);
      for (var block = 0; block < blocksPerFrame; ++block)
        WriteEnhancedAudioBlock(writer, block, options.Acmod, channels, fullBandwidthChannels, lfeIndex,
          bandwidthCode, coding, baps, coefficients);

      // Annex E errorcheck consists of a reserved bit somewhere in the zero padding and a trailing
      // 16-bit CRC. Leave at least 18 bits after the coded audio data, as the reference encoder does.
      if (writer.BitPosition > frameBytes * 8 - 18)
        return null;

      var result = new byte[frameBytes];
      Array.Copy(writer.Buffer, result, Math.Min(result.Length, writer.Buffer.Length));
      FinalizeEnhancedCrc(result);
      return result;
    }
  }

  private static void WriteEnhancedFrameHeader(
    Ac3BitWriter writer,
    Eac3EncoderOptions options,
    int frameBytes,
    int blocksPerFrame,
    int frameIndex,
    int coarseSnr,
    int fineSnr) {

    writer.WriteBits(0x0B77, 16);
    writer.WriteBits(0, 2);                              // strmtyp: independent substream
    writer.WriteBits(0, 3);                              // substreamid
    writer.WriteBits((uint)(frameBytes / 2 - 1), 11);   // frmsiz

    if (options.SampleRate < 32_000) {
      writer.WriteBits(3, 2);                            // fscod: reduced sample rate follows
      writer.WriteBits(options.SampleRate switch {
        24_000 => 0u,
        22_050 => 1u,
        _ => 2u,                                         // 16 kHz
      }, 2);                                             // fscod2; six blocks are implicit
    } else {
      writer.WriteBits(options.SampleRate switch { 48_000 => 0u, 44_100 => 1u, _ => 2u }, 2); // fscod
      writer.WriteBits(blocksPerFrame switch { 1 => 0u, 2 => 1u, 3 => 2u, _ => 3u }, 2);       // numblkscod
    }

    writer.WriteBits((uint)options.Acmod, 3);
    writer.WriteBits(options.LowFrequencyEffects ? 1u : 0u, 1);
    writer.WriteBits(16, 5);                             // bsid: E-AC-3

    writer.WriteBits((uint)-options.DialNorm, 5);
    writer.WriteBits(0, 1);                              // compre
    if (options.Acmod == 0) {
      writer.WriteBits((uint)-(options.DialNorm2 ?? options.DialNorm), 5);
      writer.WriteBits(0, 1);                            // compr2e
    }

    writer.WriteBits(0, 1);                              // mixmdate
    writer.WriteBits(0, 1);                              // infomdate
    if (blocksPerFrame != 6)
      writer.WriteBits(frameIndex % 6 == 0 ? 1u : 0u, 1); // convsync
    writer.WriteBits(0, 1);                              // addbsie

    if (blocksPerFrame == 6) {
      writer.WriteBits(1, 1);                            // AC-3-style exponent strategy syntax
      writer.WriteBits(0, 1);                            // AHT disabled
    }
    writer.WriteBits(0, 2);                              // frame-level SNR offset strategy
    writer.WriteBits(0, 1);                              // transient pre-noise syntax disabled
    writer.WriteBits(0, 1);                              // block-switch syntax disabled
    writer.WriteBits(1, 1);                              // explicit dither flags (we write zero)
    writer.WriteBits(0, 1);                              // bit-allocation syntax disabled
    writer.WriteBits(0, 1);                              // fast-gain syntax disabled
    writer.WriteBits(0, 1);                              // delta-bit-allocation syntax disabled
    writer.WriteBits(0, 1);                              // skip-field syntax disabled
    writer.WriteBits(0, 1);                              // SPX attenuation syntax disabled

    if (options.Acmod > 1) {
      writer.WriteBits(0, 1);                            // block 0 coupling not in use
      for (var block = 1; block < blocksPerFrame; ++block)
        writer.WriteBits(0, 1);                          // no new coupling strategy
    }

    var fullBandwidthChannels = Ac3FrameHeader.AcmodChannelCount(options.Acmod);
    for (var block = 0; block < blocksPerFrame; ++block)
      for (var ch = 0; ch < fullBandwidthChannels; ++ch)
        writer.WriteBits(block == 0 ? 3u : 0u, 2);       // D45 then reuse
    if (options.LowFrequencyEffects)
      for (var block = 0; block < blocksPerFrame; ++block)
        writer.WriteBits(block == 0 ? 1u : 0u, 1);       // LFE D15 then reuse

    if (blocksPerFrame == 6)
      for (var ch = 0; ch < fullBandwidthChannels; ++ch)
        writer.WriteBits(0, 5);                          // converter exponent strategy
    else
      writer.WriteBits(0, 1);                            // no converter exponent strategy

    writer.WriteBits((uint)coarseSnr, 6);
    writer.WriteBits((uint)fineSnr, 4);
    if (blocksPerFrame > 1)
      writer.WriteBits(0, 1);                            // no block-start-info table
  }

  private static void WriteEnhancedAudioBlock(
    Ac3BitWriter writer,
    int block,
    int acmod,
    int channels,
    int fullBandwidthChannels,
    int lfeIndex,
    int bandwidthCode,
    ChannelCoding[] coding,
    byte[][] baps,
    float[][][] coefficients) {

    for (var ch = 0; ch < fullBandwidthChannels; ++ch)
      writer.WriteBits(0, 1);                            // dithflag = 0

    writer.WriteBits(0, 1);                              // dynrnge
    if (acmod == 0)
      writer.WriteBits(0, 1);                            // dynrng2e

    writer.WriteBits(0, 1);                              // spxinu (blk0) / spxstre (later)

    if (acmod == 2) {
      if (block == 0) {
        for (var band = 0; band < 4; ++band)
          writer.WriteBits(0, 1);                        // rematrixing flags
      } else {
        writer.WriteBits(0, 1);                          // no new rematrixing strategy
      }
    }

    if (block == 0) {
      for (var ch = 0; ch < fullBandwidthChannels; ++ch)
        writer.WriteBits((uint)bandwidthCode, 6);

      for (var ch = 0; ch < fullBandwidthChannels; ++ch) {
        WriteExponents(writer, coding[ch]);
        writer.WriteBits(0, 2);                          // gainrng
      }
      if (lfeIndex >= 0)
        WriteExponents(writer, coding[lfeIndex]);
    }

    writer.WriteBits(0, 1);                              // no converter SNR offset
    WriteMantissas(writer, block, channels, coding, baps, coefficients);
  }

  private static float[][][] AnalyzeEnhancedFrame(
    ReadOnlySpan<short> pcm,
    int channels,
    int[] sourceChannel,
    float[][] history,
    int blocksPerFrame) {

    if (blocksPerFrame == BlocksPerFrame)
      return AnalyzeFrame(pcm, channels, sourceChannel, history);

    var result = new float[blocksPerFrame][][];
    var window = Ac3Tables.Window;
    Span<float> time = stackalloc float[512];

    for (var block = 0; block < blocksPerFrame; ++block) {
      result[block] = new float[channels][];
      for (var ch = 0; ch < channels; ++ch) {
        var current = new float[SamplesPerBlock];
        for (var n = 0; n < SamplesPerBlock; ++n)
          current[n] = pcm[(block * SamplesPerBlock + n) * channels + sourceChannel[ch]] / 32768f;

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
          coeff[k] = (float)sum * MdctScale;
        }
        result[block][ch] = coeff;
        current.CopyTo(history[ch], 0);
      }
    }

    return result;
  }

  private static ChannelCoding BuildEnhancedChannelCoding(
    float[][][] coefficients,
    int channel,
    int endMantissa,
    Ac3Exponents.Strategy strategy,
    int blocksPerFrame) {

    if (blocksPerFrame == BlocksPerFrame)
      return BuildChannelCoding(coefficients, channel, endMantissa, strategy);

    var raw = new byte[256];
    Array.Fill(raw, (byte)24);
    for (var bin = 0; bin < endMantissa; ++bin) {
      var peak = 0f;
      for (var block = 0; block < blocksPerFrame; ++block)
        peak = Math.Max(peak, MathF.Abs(coefficients[block][channel][bin]));
      if (peak <= 1e-12f) {
        raw[bin] = 24;
        continue;
      }
      raw[bin] = (byte)Math.Clamp((int)MathF.Floor(-MathF.Log2(peak)), 0, 24);
    }

    var step = Ac3Exponents.GroupSize(strategy);
    var wordCount = Ac3Exponents.GroupCount(endMantissa, strategy);
    var groups = new byte[1 + wordCount * 3];
    for (var group = 0; group < groups.Length; ++group) {
      var start = group == 0 ? 0 : 1 + (group - 1) * step;
      var span = group == 0 ? 1 : step;
      var minimum = 24;
      for (var i = 0; i < span; ++i)
        minimum = Math.Min(minimum, raw[Math.Min(start + i, Math.Max(0, endMantissa - 1))]);
      groups[group] = (byte)minimum;
    }

    groups[0] = (byte)Math.Min((int)groups[0], 15);
    for (var i = 1; i < groups.Length; ++i)
      groups[i] = (byte)Math.Min(groups[i], groups[i - 1] + 2);
    for (var i = groups.Length - 2; i >= 0; --i)
      groups[i] = (byte)Math.Min(groups[i], groups[i + 1] + 2);

    var expanded = new byte[256];
    expanded[0] = groups[0];
    var expandedBin = 1;
    for (var group = 1; group < groups.Length; ++group)
      for (var i = 0; i < step && expandedBin < expanded.Length; ++i)
        expanded[expandedBin++] = groups[group];
    if (expandedBin < expanded.Length)
      Array.Fill(expanded, groups[^1], expandedBin, expanded.Length - expandedBin);

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

  private static (int Channels, int BlocksPerFrame) ValidateEnhancedEncoder(int sampleCount, Eac3EncoderOptions options) {
    if (options.Acmod is < 0 or > 7)
      throw new ArgumentOutOfRangeException(nameof(options), "E-AC-3 acmod must be in the range 0..7.");
    var channels = Ac3FrameHeader.AcmodChannelCount(options.Acmod) + (options.LowFrequencyEffects ? 1 : 0);
    if (channels is < 1 or > 6)
      throw new ArgumentOutOfRangeException(nameof(options), "Invalid E-AC-3 channel layout.");
    if (sampleCount % channels != 0)
      throw new ArgumentException("Interleaved PCM sample count must be divisible by the E-AC-3 channel count.");
    if (options.SampleRate is not (16_000 or 22_050 or 24_000 or 32_000 or 44_100 or 48_000))
      throw new ArgumentOutOfRangeException(nameof(options),
        "E-AC-3 supports 16, 22.05, 24, 32, 44.1 and 48 kHz in this encoder.");
    if (options.Bitrate <= 0)
      throw new ArgumentOutOfRangeException(nameof(options), "E-AC-3 bitrate must be positive.");
    if (options.DialNorm is < -31 or > -1)
      throw new ArgumentOutOfRangeException(nameof(options), "dialnorm must be in the range -31..-1 dB.");
    if (options.DialNorm2 is < -31 or > -1)
      throw new ArgumentOutOfRangeException(nameof(options), "dialnorm2 must be null or in the range -31..-1 dB.");
    if (options.DialNorm2 is not null && options.Acmod != 0)
      throw new ArgumentException("dialnorm2 is only valid for E-AC-3 acmod 0 (1+1 dual mono).", nameof(options));
    if (options.Cutoff < 0 || options.Cutoff > options.SampleRate / 2)
      throw new ArgumentOutOfRangeException(nameof(options), "Cutoff must be zero (automatic) or within the Nyquist limit.");

    var reducedRate = options.SampleRate < 32_000;
    var blocks = options.BlocksPerFrame ?? (reducedRate ? 6 : SelectEnhancedBlockCount(options.SampleRate, options.Bitrate));
    if (blocks is not (1 or 2 or 3 or 6))
      throw new ArgumentOutOfRangeException(nameof(options), "E-AC-3 blocks per frame must be 1, 2, 3 or 6.");
    if (reducedRate && blocks != 6)
      throw new ArgumentOutOfRangeException(nameof(options),
        "Reduced-rate E-AC-3 (16/22.05/24 kHz) always carries six audio blocks per syncframe.");

    var exactWords = options.Bitrate * (long)(blocks * SamplesPerBlock) / (options.SampleRate * 16L);
    if (exactWords is < 1 or > 2048)
      throw new ArgumentOutOfRangeException(nameof(options), "E-AC-3 frame size must fit the 11-bit frmsiz field (1..2048 words).");
    if (!options.PadFinalFrame && sampleCount / channels % (blocks * SamplesPerBlock) != 0)
      throw new ArgumentException($"E-AC-3 PCM must contain whole {blocks * SamplesPerBlock}-sample frames when padding is disabled.");
    return (channels, blocks);
  }

  private static int SelectEnhancedBlockCount(int sampleRate, int bitrate) {
    foreach (var blocks in new[] { 6, 3, 2, 1 }) {
      var samples = blocks * SamplesPerBlock;
      var maxBitrate = 2048L * sampleRate / samples * 16;
      if (bitrate <= maxBitrate)
        return blocks;
    }
    throw new ArgumentOutOfRangeException(nameof(bitrate), "E-AC-3 bitrate exceeds the maximum representable one-block frame size.");
  }

  private static int GetEnhancedFrameBytes(int sampleRate, int bitrate, int blocksPerFrame, ref double accumulator) {
    var exactWords = bitrate * (double)(blocksPerFrame * SamplesPerBlock) / (sampleRate * 16.0);
    var lowWords = (int)Math.Floor(exactWords);
    accumulator += exactWords - lowWords;
    var high = accumulator >= 1.0;
    if (high)
      accumulator -= 1.0;
    var words = lowWords + (high ? 1 : 0);
    if (words is < 1 or > 2048)
      throw new ArgumentOutOfRangeException(nameof(bitrate), "E-AC-3 frame size is outside 1..2048 16-bit words.");
    return words * 2;
  }

  private static int ResolveEnhancedBandwidthCode(Eac3EncoderOptions options, int fullBandwidthChannels) {
    var cutoff = options.Cutoff;
    if (cutoff == 0) {
      var bitsPerChannel = options.Bitrate / Math.Max(1, fullBandwidthChannels);
      cutoff = Math.Min((int)(options.SampleRate * 0.47), 5000 + bitsPerChannel / 8);
    }
    var coefficients = cutoff * 2 * 256 / options.SampleRate;
    return Math.Clamp((coefficients - 73) / 3, 0, 60);
  }

  private static void CopyEnhancedFrame(
    ReadOnlySpan<short> source,
    Span<short> destination,
    int sourceFrame,
    int samplesPerChannel,
    int channels) {
    destination.Clear();
    source.Slice(sourceFrame * channels, samplesPerChannel * channels).CopyTo(destination);
  }

  private static void PadEnhancedFrame(Span<short> frame, int samplesPerChannel, int targetSamples, int channels) {
    if (samplesPerChannel <= 0)
      return;
    for (var ch = 0; ch < channels; ++ch) {
      var value = frame[(samplesPerChannel - 1) * channels + ch];
      for (var sample = samplesPerChannel; sample < targetSamples; ++sample)
        frame[sample * channels + ch] = value;
    }
  }

  private static void FinalizeEnhancedCrc(byte[] frame) {
    var crc2 = Swap16(Crc16Ansi(frame.AsSpan(2, frame.Length - 4)));
    if (crc2 == 0x0B77) {
      frame[^3] ^= 0x01;
      crc2 ^= 0x8005;
    }
    WriteBigEndian16(frame, frame.Length - 2, crc2);
  }
}
