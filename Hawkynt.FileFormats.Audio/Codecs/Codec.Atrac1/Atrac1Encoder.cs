#pragma warning disable CS1591
namespace Codec.Atrac1;

/// <summary>
/// Fixed-rate Sony ATRAC1 encoder. ATRAC1 sound units are always 212 bytes per channel and
/// represent 512 samples at 44.1 kHz. The encoder supports the eight legal long/short-window
/// combinations and every BFU-count selector representable by the three-bit ATRAC1 field.
/// </summary>
public sealed class Atrac1Encoder {

  private const int SampleRate = 44100;
  private const int SoundUnitBits = Atrac1Codec.SoundUnitSize * 8;
  private const int PaddingReserveBits = 24;
  private static readonly int[] LegalBfuCounts = [20, 28, 32, 36, 40, 44, 48, 52];
  private static readonly float[] Mdct64 = BuildMdctKernel(64, 0.5f);
  private static readonly float[] Mdct256 = BuildMdctKernel(256, 0.5f);
  private static readonly float[] Mdct512 = BuildMdctKernel(512, 1.0f);

  private readonly ChannelState[] _states;
  private readonly Atrac1EncoderOptions _options;
  private readonly int _bfuSelector;

  /// <summary>Gets the input sample rate required by ATRAC1.</summary>
  public static int RequiredSampleRate => SampleRate;

  /// <summary>Gets the number of channels encoded by this instance.</summary>
  public int Channels { get; }

  /// <summary>Gets the number of coded bytes per 512-sample frame across all channels.</summary>
  public int FrameSize => this.Channels * Atrac1Codec.SoundUnitSize;

  /// <summary>Creates a mono or stereo ATRAC1 encoder.</summary>
  public Atrac1Encoder(int channels, Atrac1EncoderOptions? options = null) {
    if (channels is not (1 or 2))
      throw new ArgumentOutOfRangeException(nameof(channels), channels, "ATRAC1 encoding supports mono or stereo.");

    this._options = options ?? new Atrac1EncoderOptions();
    if (this._options.WindowMask is < 0 or > 7)
      throw new ArgumentOutOfRangeException(nameof(options), this._options.WindowMask, "ATRAC1 window mask must be 0..7.");

    this._bfuSelector = Array.IndexOf(LegalBfuCounts, this._options.BfuCount);
    if (this._bfuSelector < 0)
      throw new ArgumentOutOfRangeException(nameof(options), this._options.BfuCount,
        "ATRAC1 BFU count must be one of 20, 28, 32, 36, 40, 44, 48 or 52.");

    this.Channels = channels;
    this._states = new ChannelState[channels];
    for (var channel = 0; channel < channels; ++channel)
      this._states[channel] = new ChannelState();
  }

  /// <summary>
  /// Encodes exactly one interleaved 512-sample-per-channel frame. Encoder state is retained for
  /// the QMF and transform overlap used by subsequent calls.
  /// </summary>
  public byte[] EncodeFrame(ReadOnlySpan<short> interleavedPcm) {
    var expected = Atrac1Codec.SamplesPerFrame * this.Channels;
    if (interleavedPcm.Length != expected)
      throw new ArgumentException($"ATRAC1 frame requires exactly {expected} interleaved samples.", nameof(interleavedPcm));

    var output = new byte[this.FrameSize];
    var source = new float[Atrac1Codec.SamplesPerFrame];
    var spectrum = new float[Atrac1Codec.SamplesPerFrame];

    for (var channel = 0; channel < this.Channels; ++channel) {
      for (var sample = 0; sample < Atrac1Codec.SamplesPerFrame; ++sample)
        source[sample] = interleavedPcm[sample * this.Channels + channel] / 32768.0f;

      var state = this._states[channel];
      Analyze(state, source);
      BuildSpectrum(state, spectrum, this._options.WindowMask);
      var bfus = ScaleBfus(spectrum, this._options.WindowMask, this._options.BfuCount);
      EncodeSoundUnit(output.AsSpan(channel * Atrac1Codec.SoundUnitSize, Atrac1Codec.SoundUnitSize),
        bfus, this._options.WindowMask, this._bfuSelector);
    }

    return output;
  }

  /// <summary>
  /// Encodes an arbitrary number of complete interleaved PCM frames. A final partial 512-sample
  /// frame is zero-padded, matching the fixed ATRAC1 frame geometry.
  /// </summary>
  public byte[] EncodeStream(ReadOnlySpan<short> interleavedPcm) {
    if (interleavedPcm.Length % this.Channels != 0)
      throw new ArgumentException("PCM sample count must be a whole number of interleaved channel frames.", nameof(interleavedPcm));
    if (interleavedPcm.IsEmpty)
      return [];

    var samplesPerChannel = interleavedPcm.Length / this.Channels;
    var frameCount = checked((samplesPerChannel + Atrac1Codec.SamplesPerFrame - 1) / Atrac1Codec.SamplesPerFrame);
    var output = new byte[checked(frameCount * this.FrameSize)];
    var frame = new short[Atrac1Codec.SamplesPerFrame * this.Channels];

    for (var frameIndex = 0; frameIndex < frameCount; ++frameIndex) {
      Array.Clear(frame);
      var sourceSample = frameIndex * Atrac1Codec.SamplesPerFrame * this.Channels;
      var copyCount = Math.Min(frame.Length, interleavedPcm.Length - sourceSample);
      interleavedPcm.Slice(sourceSample, copyCount).CopyTo(frame);
      this.EncodeFrame(frame).CopyTo(output, frameIndex * this.FrameSize);
    }

    return output;
  }

  private static void Analyze(ChannelState state, ReadOnlySpan<float> source) {
    state.HighDelay.AsSpan(256, 39).CopyTo(state.HighDelay);
    state.FirstQmf.Process(source, state.MidLow.AsSpan(0, 256), state.HighDelay.AsSpan(39, 256));
    state.SecondQmf.Process(state.MidLow.AsSpan(0, 256), state.Low.AsSpan(0, 128), state.Mid.AsSpan(0, 128));
    state.HighDelay.AsSpan(0, 256).CopyTo(state.High.AsSpan(0, 256));
  }

  private static void BuildSpectrum(ChannelState state, Span<float> spectrum, int windowMask) {
    spectrum.Clear();
    TransformBand(state.Low, 128, spectrum.Slice(0, 128), (windowMask & 1) != 0, reverse: false, highBand: false);
    TransformBand(state.Mid, 128, spectrum.Slice(128, 128), (windowMask & 2) != 0, reverse: true, highBand: false);
    TransformBand(state.High, 256, spectrum.Slice(256, 256), (windowMask & 4) != 0, reverse: true, highBand: true);
  }

  private static void TransformBand(float[] band, int bandSamples, Span<float> destination,
      bool shortWindows, bool reverse, bool highBand) {
    var blockSize = shortWindows ? 32 : bandSamples;
    var blockCount = bandSamples / blockSize;
    var transformLength = shortWindows ? 64 : bandSamples * 2;
    var windowStart = shortWindows ? 0 : highBand ? 112 : 48;
    var temp = new float[transformLength];
    Span<float> coefficients = stackalloc float[256];

    for (var block = 0; block < blockCount; ++block) {
      temp.AsSpan().Clear();
      band.AsSpan(bandSamples, 32).CopyTo(temp.AsSpan(windowStart, 32));

      var blockOffset = block * blockSize;
      for (var i = 0; i < 32; ++i) {
        var index = blockOffset + blockSize - 32 + i;
        var sample = band[index];
        band[bandSamples + i] = Atrac1Tables.Sine32[i] * sample;
        band[index] = Atrac1Tables.Sine32[31 - i] * sample;
      }

      band.AsSpan(blockOffset, blockSize).CopyTo(temp.AsSpan(windowStart + 32, blockSize));
      var coefficientCount = transformLength / 2;
      ForwardMdct(temp, coefficients[..coefficientCount]);

      var multiplier = shortWindows && highBand ? 2.0f : 1.0f;
      var target = destination.Slice(blockOffset, coefficientCount);
      if (!reverse) {
        for (var i = 0; i < coefficientCount; ++i)
          target[i] = coefficients[i] * multiplier;
      } else {
        for (var i = 0; i < coefficientCount; ++i)
          target[i] = coefficients[coefficientCount - 1 - i] * multiplier;
      }
    }
  }

  private static void ForwardMdct(ReadOnlySpan<float> input, Span<float> output) {
    var kernel = input.Length switch {
      64 => Mdct64,
      256 => Mdct256,
      512 => Mdct512,
      _ => throw new ArgumentOutOfRangeException(nameof(input), input.Length, "Unsupported ATRAC1 MDCT size."),
    };

    for (var k = 0; k < output.Length; ++k) {
      var kernelOffset = k * input.Length;
      float sum = 0;
      for (var n = 0; n < input.Length; ++n)
        sum += input[n] * kernel[kernelOffset + n];
      output[k] = sum;
    }
  }

  private static float[] BuildMdctKernel(int inputLength, float scale) {
    var coefficients = inputLength / 2;
    var kernel = new float[checked(inputLength * coefficients)];
    var normalization = scale / inputLength;
    for (var k = 0; k < coefficients; ++k)
      for (var n = 0; n < inputLength; ++n)
        kernel[k * inputLength + n] = (float)(
          Math.Cos((Math.PI / coefficients) * (n + 0.5 + coefficients / 2.0) * (k + 0.5)) * normalization);
    return kernel;
  }

  private static ScaledBfu[] ScaleBfus(ReadOnlySpan<float> spectrum, int windowMask, int bfuCount) {
    var result = new ScaledBfu[bfuCount];
    for (var bfu = 0; bfu < bfuCount; ++bfu) {
      var band = bfu < 20 ? 0 : bfu < 36 ? 1 : 2;
      var shortWindow = (windowMask & (1 << band)) != 0;
      var start = shortWindow ? Atrac1Tables.BfuStartShort[bfu] : Atrac1Tables.BfuStartLong[bfu];
      var length = Atrac1Tables.SpecsPerBfu[bfu];
      var source = spectrum.Slice(start, length);

      float max = 0;
      double energy = 0;
      foreach (var value in source) {
        max = Math.Max(max, Math.Abs(value));
        energy += value * value;
      }

      if (max == 0) {
        result[bfu] = new ScaledBfu(0, new float[length], 0);
        continue;
      }

      var scaleIndex = 0;
      while (scaleIndex < 63 && EncoderScaleFactor(scaleIndex) < max)
        ++scaleIndex;
      var scaleFactor = EncoderScaleFactor(scaleIndex);
      var values = new float[length];
      for (var i = 0; i < length; ++i)
        values[i] = Math.Clamp(source[i] / scaleFactor, -0.999999f, 0.999999f);
      result[bfu] = new ScaledBfu((byte)scaleIndex, values, energy);
    }
    return result;
  }

  private static float EncoderScaleFactor(int index) => Atrac1Tables.SfTable[index] / 65536.0f;

  private static void EncodeSoundUnit(Span<byte> destination, ScaledBfu[] bfus, int windowMask, int bfuSelector) {
    destination.Clear();
    var writer = new BitWriter(destination);

    writer.WriteBits((uint)((windowMask & 1) != 0 ? 0 : 2), 2);
    writer.WriteBits((uint)((windowMask & 2) != 0 ? 0 : 2), 2);
    writer.WriteBits((uint)((windowMask & 4) != 0 ? 0 : 3), 2);
    writer.WriteBits(0, 2);
    writer.WriteBits((uint)bfuSelector, 3);
    writer.WriteBits(0, 2);
    writer.WriteBits(0, 3);

    var headerBits = 16 + bfus.Length * 10;
    var mantissaBudget = SoundUnitBits - headerBits - PaddingReserveBits;
    var wordLengths = AllocateWordLengths(bfus, mantissaBudget);

    foreach (var wordLength in wordLengths)
      writer.WriteBits((uint)(wordLength == 0 ? 0 : wordLength - 1), 4);
    foreach (var bfu in bfus)
      writer.WriteBits(bfu.ScaleFactorIndex, 6);

    for (var bfuIndex = 0; bfuIndex < bfus.Length; ++bfuIndex) {
      var wordLength = wordLengths[bfuIndex];
      if (wordLength == 0)
        continue;

      var maximum = (1 << (wordLength - 1)) - 1;
      foreach (var value in bfus[bfuIndex].Values) {
        var quantized = (int)MathF.Round(value * maximum);
        quantized = Math.Clamp(quantized, -maximum, maximum);
        writer.WriteSigned(quantized, wordLength);
      }
    }
  }

  private static int[] AllocateWordLengths(ScaledBfu[] bfus, int bitBudget) {
    var result = new int[bfus.Length];
    var remaining = bitBudget;

    while (true) {
      var bestIndex = -1;
      var bestNextLength = 0;
      var bestCost = 0;
      double bestUtility = 0;

      for (var i = 0; i < bfus.Length; ++i) {
        if (bfus[i].Energy <= 0)
          continue;

        var current = result[i];
        var next = current == 0 ? 2 : current + 1;
        if (next > 16)
          continue;

        var cost = Atrac1Tables.SpecsPerBfu[i] * (next - current);
        if (cost > remaining)
          continue;

        var frequencyWeight = 1.30 - 0.65 * i / 51.0;
        var refinementPenalty = 1 << Math.Min(12, Math.Max(0, next - 2));
        var utility = (bfus[i].ScaleFactorIndex + 1.0) * frequencyWeight / (cost * refinementPenalty);
        if (utility <= bestUtility)
          continue;

        bestUtility = utility;
        bestIndex = i;
        bestNextLength = next;
        bestCost = cost;
      }

      if (bestIndex < 0)
        break;
      result[bestIndex] = bestNextLength;
      remaining -= bestCost;
    }

    return result;
  }

  private sealed class ChannelState {
    public readonly QmfAnalysis FirstQmf = new(512);
    public readonly QmfAnalysis SecondQmf = new(256);
    public readonly float[] MidLow = new float[512];
    public readonly float[] HighDelay = new float[512 + 39];
    public readonly float[] Low = new float[128 + 32];
    public readonly float[] Mid = new float[128 + 32];
    public readonly float[] High = new float[256 + 32];
  }

  private sealed class QmfAnalysis {
    private readonly int _inputCount;
    private readonly float[] _pcmBuffer;

    public QmfAnalysis(int inputCount) {
      this._inputCount = inputCount;
      this._pcmBuffer = new float[inputCount + 46];
    }

    public void Process(ReadOnlySpan<float> input, Span<float> lower, Span<float> upper) {
      if (input.Length != this._inputCount || lower.Length < this._inputCount / 2 || upper.Length < this._inputCount / 2)
        throw new ArgumentException("Invalid ATRAC1 QMF buffer geometry.");

      this._pcmBuffer.AsSpan(this._inputCount, 46).CopyTo(this._pcmBuffer);
      input.CopyTo(this._pcmBuffer.AsSpan(46));

      for (var j = 0; j < this._inputCount; j += 2) {
        float low = 0;
        float high = 0;
        for (var tap = 0; tap < 24; ++tap) {
          low += Atrac1Tables.QmfWindow[tap * 2] * this._pcmBuffer[47 + j - tap * 2];
          high += Atrac1Tables.QmfWindow[tap * 2 + 1] * this._pcmBuffer[46 + j - tap * 2];
        }
        lower[j / 2] = low + high;
        upper[j / 2] = low - high;
      }
    }
  }

  private readonly record struct ScaledBfu(byte ScaleFactorIndex, float[] Values, double Energy);

  private ref struct BitWriter {
    private readonly Span<byte> _destination;
    private int _bitPosition;

    public BitWriter(Span<byte> destination) {
      this._destination = destination;
      this._bitPosition = 0;
    }

    public void WriteBits(uint value, int count) {
      if (count is < 0 or > 32 || this._bitPosition + count > this._destination.Length * 8)
        throw new InvalidOperationException("ATRAC1 bit allocation exceeded the 212-byte sound unit.");

      for (var bit = count - 1; bit >= 0; --bit) {
        if (((value >> bit) & 1) != 0)
          this._destination[this._bitPosition >> 3] |= (byte)(1 << (7 - (this._bitPosition & 7)));
        ++this._bitPosition;
      }
    }

    public void WriteSigned(int value, int count) {
      var mask = count == 32 ? uint.MaxValue : (1u << count) - 1;
      this.WriteBits(unchecked((uint)value) & mask, count);
    }
  }
}

/// <summary>Controls representable ATRAC1 sound-unit coding choices.</summary>
public sealed record Atrac1EncoderOptions {
  /// <summary>
  /// Three-bit mask selecting short windows for the low, middle and high QMF bands respectively.
  /// Every value from 0 through 7 is representable by the ATRAC1 sound-unit header.
  /// </summary>
  public int WindowMask { get; init; }

  /// <summary>Number of block floating units to carry. Legal values are 20, 28, 32, 36, 40, 44, 48 and 52.</summary>
  public int BfuCount { get; init; } = 52;
}
