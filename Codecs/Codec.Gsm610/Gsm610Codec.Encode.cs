namespace Codec.Gsm610;

/// <summary>Configuration for GSM 06.10 full-rate encoding.</summary>
/// <param name="Channels">Number of independently encoded interleaved PCM channels.</param>
/// <param name="PadFinalFrame">Pad an incomplete final 20 ms frame with the last available sample.</param>
public sealed record Gsm610EncoderOptions(int Channels = 1, bool PadFinalFrame = true);

/// <summary>
/// Represents a gsm 610 codec.
/// </summary>
public static partial class Gsm610Codec {

  /// <summary>
  /// Encodes interleaved PCM16 at 8 kHz to GSM 06.10 full-rate frames. GSM itself is a mono
  /// speech codec; multiple channels are encoded as independent 33-byte frames in channel order.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm, Gsm610EncoderOptions? options = null) {
    options ??= new Gsm610EncoderOptions();
    if (options.Channels < 1)
      throw new ArgumentOutOfRangeException(nameof(options), "GSM channel count must be positive.");
    if (pcm.Length % options.Channels != 0)
      throw new ArgumentException("Interleaved PCM sample count must be a multiple of the channel count.", nameof(pcm));

    var inputFrames = pcm.Length / options.Channels;
    if (inputFrames == 0)
      return [];
    if (!options.PadFinalFrame && inputFrames % FrameSamples != 0)
      throw new ArgumentException($"PCM must contain whole {FrameSamples}-sample GSM frames when padding is disabled.", nameof(pcm));

    var groups = (inputFrames + FrameSamples - 1) / FrameSamples;
    var result = new byte[groups * options.Channels * FrameBytes];
    var encoders = new Encoder[options.Channels];
    for (var c = 0; c < encoders.Length; ++c)
      encoders[c] = new Encoder();

    Span<short> frame = stackalloc short[FrameSamples];
    for (var group = 0; group < groups; ++group) {
      var first = group * FrameSamples;
      var count = Math.Min(FrameSamples, inputFrames - first);
      for (var c = 0; c < options.Channels; ++c) {
        for (var i = 0; i < count; ++i)
          frame[i] = pcm[(first + i) * options.Channels + c];
        if (count < FrameSamples) {
          var pad = count > 0 ? frame[count - 1] : (short)0;
          frame[count..].Fill(pad);
        }

        var destination = result.AsSpan((group * options.Channels + c) * FrameBytes, FrameBytes);
        encoders[c].EncodeFrame(frame, destination);
      }
    }
    return result;
  }

  /// <summary>Encodes one mono PCM stream to the raw <c>.gsm</c> frame layout.</summary>
  public static byte[] EncodeRaw(ReadOnlySpan<short> pcm, bool padFinalFrame = true)
    => Encode(pcm, new Gsm610EncoderOptions(1, padFinalFrame));

  private sealed class Encoder {
    private readonly short[] _history = new short[120 + FrameSamples];

    public void EncodeFrame(ReadOnlySpan<short> pcm, Span<byte> destination) {
      if (pcm.Length != FrameSamples)
        throw new ArgumentException($"GSM encoder requires {FrameSamples} samples per frame.", nameof(pcm));
      if (destination.Length < FrameBytes)
        throw new ArgumentException($"GSM frame output requires {FrameBytes} bytes.", nameof(destination));

      Span<int> lar = stackalloc int[8];
      AnalyseLar(pcm, lar);

      Span<int> nc = stackalloc int[4];
      Span<int> bc = stackalloc int[4];
      Span<int> mc = stackalloc int[4];
      Span<int> xmaxc = stackalloc int[4];
      Span<int> xmc = stackalloc int[52];

      for (var subframe = 0; subframe < 4; ++subframe)
        AnalyseSubframe(pcm.Slice(subframe * 40, 40), subframe, nc, bc, mc, xmaxc, xmc);

      destination[..FrameBytes].Clear();
      var writer = new BitWriter(destination);
      writer.Write(0xD, 4);
      writer.Write(lar[0], 6);
      writer.Write(lar[1], 6);
      writer.Write(lar[2], 5);
      writer.Write(lar[3], 5);
      writer.Write(lar[4], 4);
      writer.Write(lar[5], 4);
      writer.Write(lar[6], 3);
      writer.Write(lar[7], 3);
      for (var k = 0; k < 4; ++k) {
        writer.Write(nc[k], 7);
        writer.Write(bc[k], 2);
        writer.Write(mc[k], 2);
        writer.Write(xmaxc[k], 6);
        for (var i = 0; i < 13; ++i)
          writer.Write(xmc[k * 13 + i], 3);
      }
      if (writer.BitsWritten != FrameBytes * 8)
        throw new InvalidOperationException($"Internal GSM frame packer wrote {writer.BitsWritten} bits instead of 264.");

      Array.Copy(_history, FrameSamples, _history, 0, 120);
    }

    private void AnalyseSubframe(ReadOnlySpan<short> source, int subframe,
      Span<int> nc, Span<int> bc, Span<int> mc, Span<int> xmaxc, Span<int> xmc) {
      var currentOffset = 120 + subframe * 40;

      var bestLag = 40;
      double bestScore = double.NegativeInfinity;
      double bestGain = 0;
      for (var lag = 40; lag <= 120; ++lag) {
        double cross = 0, histEnergy = 1;
        for (var i = 0; i < 40; ++i) {
          var h = _history[currentOffset + i - lag];
          cross += source[i] * (double)h;
          histEnergy += h * (double)h;
        }
        var gain = Math.Clamp(cross / histEnergy, 0.0, 1.0);
        var score = cross * gain;
        if (score <= bestScore) continue;
        bestScore = score;
        bestLag = lag;
        bestGain = gain;
      }
      nc[subframe] = bestLag;

      var gainIndex = 0;
      var gainError = double.PositiveInfinity;
      for (var i = 0; i < Qlb.Length; ++i) {
        var candidate = Qlb[i] / 32768.0;
        var error = Math.Abs(candidate - bestGain);
        if (error >= gainError) continue;
        gainError = error;
        gainIndex = i;
      }
      bc[subframe] = gainIndex;
      var gainQ15 = Qlb[gainIndex];

      Span<double> residual = stackalloc double[40];
      for (var i = 0; i < 40; ++i) {
        var predicted = gainQ15 * _history[currentOffset + i - bestLag] / 32768.0;
        residual[i] = source[i] - predicted;
      }

      var bestGrid = 0;
      var bestEnergy = -1.0;
      for (var grid = 0; grid < 4; ++grid) {
        double energy = 0;
        for (var i = 0; i < 13; ++i) {
          var index = grid + 3 * i;
          if (index >= 40) break;
          energy += residual[index] * residual[index];
        }
        if (energy <= bestEnergy) continue;
        bestEnergy = energy;
        bestGrid = grid;
      }
      mc[subframe] = bestGrid;

      double maxAbs = 0;
      for (var i = 0; i < 13; ++i) {
        var index = bestGrid + 3 * i;
        if (index < 40)
          maxAbs = Math.Max(maxAbs, Math.Abs(residual[index]));
      }

      // The compact decoder reconstructs RPE samples as signed-3-bit * 2^(xmaxc/8).
      // Pick the closest power-of-two scale that keeps the 3-bit pulse within [-4, 3].
      var exponent = maxAbs <= 3 ? 0 : Math.Clamp((int)Math.Ceiling(Math.Log2(maxAbs / 3.0)), 0, 7);
      var scale = 1 << exponent;
      xmaxc[subframe] = exponent * 8;

      Span<int> excitation = stackalloc int[40];
      excitation.Clear();
      for (var i = 0; i < 13; ++i) {
        var index = bestGrid + 3 * i;
        var quantized = index < 40 ? (int)Math.Round(residual[index] / scale) : 0;
        quantized = Math.Clamp(quantized, -4, 3);
        xmc[subframe * 13 + i] = quantized & 7;
        if (index < 40)
          excitation[index] = quantized * scale;
      }

      for (var i = 0; i < 40; ++i) {
        var predicted = gainQ15 * _history[currentOffset + i - bestLag] >> 15;
        _history[currentOffset + i] = Saturate16(predicted + excitation[i]);
      }
    }

    private static void AnalyseLar(ReadOnlySpan<short> pcm, Span<int> encoded) {
      Span<double> autocorrelation = stackalloc double[9];
      for (var lag = 0; lag <= 8; ++lag) {
        double sum = 0;
        for (var i = lag; i < pcm.Length; ++i)
          sum += pcm[i] * (double)pcm[i - lag];
        autocorrelation[lag] = sum;
      }

      Span<double> a = stackalloc double[9];
      Span<double> previous = stackalloc double[9];
      Span<double> reflection = stackalloc double[8];
      a[0] = 1;
      var error = Math.Max(1.0, autocorrelation[0]);
      for (var order = 1; order <= 8; ++order) {
        double sum = autocorrelation[order];
        for (var j = 1; j < order; ++j)
          sum += a[j] * autocorrelation[order - j];
        var k = Math.Clamp(-sum / error, -0.95, 0.95);
        reflection[order - 1] = k;
        a.CopyTo(previous);
        a[order] = k;
        for (var j = 1; j < order; ++j)
          a[j] = previous[j] + k * previous[order - j];
        error *= Math.Max(0.05, 1 - k * k);
      }

      for (var i = 0; i < 8; ++i) {
        var bits = i < 2 ? 6 : i < 4 ? 5 : i < 6 ? 4 : 3;
        var target = reflection[i] * 32767.0;
        var denominator = Math.Max(1, InvA[i] >> 8);
        var signedCode = (int)Math.Round((target * denominator - MicB[i] * 4.0) / 256.0);
        var min = -(1 << (bits - 1));
        var max = (1 << (bits - 1)) - 1;
        signedCode = Math.Clamp(signedCode, min, max);
        encoded[i] = signedCode & ((1 << bits) - 1);
      }
    }
  }

  private ref struct BitWriter {
    private readonly Span<byte> _buffer;
    private int _bitPosition;

    public BitWriter(Span<byte> buffer) {
      _buffer = buffer;
      _bitPosition = 0;
    }

    public readonly int BitsWritten => _bitPosition;

    public void Write(int value, int bitCount) {
      for (var bit = bitCount - 1; bit >= 0; --bit) {
        if (_bitPosition >= _buffer.Length * 8)
          throw new InvalidOperationException("GSM frame bit writer overflow.");
        if (((value >> bit) & 1) != 0)
          _buffer[_bitPosition >> 3] |= (byte)(1 << (7 - (_bitPosition & 7)));
        ++_bitPosition;
      }
    }
  }

  private static short Saturate16(int value)
    => (short)Math.Clamp(value, short.MinValue, short.MaxValue);
}
