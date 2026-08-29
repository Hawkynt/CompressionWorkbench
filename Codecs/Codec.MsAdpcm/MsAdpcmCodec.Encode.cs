using System.Buffers.Binary;

namespace Codec.MsAdpcm;

public static partial class MsAdpcmCodec {

  private const int HeaderBytesPerChannel = 7;
  private const int MinimumDelta = 16;
  private const int MaximumDelta = short.MaxValue;

  /// <summary>
  /// Encodes interleaved 16-bit PCM into Microsoft ADPCM WAV blocks.
  /// Predictor coefficients and the initial quantizer delta are selected per channel and
  /// per block by a bounded look-ahead search. A short final block is padded with the last
  /// reconstructed sample so the output always consists of whole blocks.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, int channels, int blockAlign) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("MS ADPCM supports 1 or 2 channels.", nameof(channels));
    if (interleaved.Length % channels != 0)
      throw new ArgumentException("Interleaved sample count must be a multiple of the channel count.", nameof(interleaved));

    var headerBytes = HeaderBytesPerChannel * channels;
    if (blockAlign < headerBytes)
      throw new ArgumentException($"blockAlign {blockAlign} too small for {channels} channel(s).", nameof(blockAlign));
    if (interleaved.IsEmpty)
      return [];

    var dataBytes = blockAlign - headerBytes;
    var samplesPerBlock = 2 + dataBytes * 2 / channels;
    var frames = interleaved.Length / channels;
    var blockCount = (frames + samplesPerBlock - 1) / samplesPerBlock;
    var output = new byte[blockCount * blockAlign];

    Span<int> predictorIndex = stackalloc int[channels];
    Span<int> delta = stackalloc int[channels];
    Span<int> sample1 = stackalloc int[channels];
    Span<int> sample2 = stackalloc int[channels];

    for (var block = 0; block < blockCount; ++block) {
      var blockOffset = block * blockAlign;
      var baseFrame = block * samplesPerBlock;

      for (var channel = 0; channel < channels; ++channel) {
        sample2[channel] = GetSample(interleaved, frames, channels, baseFrame, channel, 0);
        sample1[channel] = GetSample(interleaved, frames, channels, baseFrame + 1, channel, (short)sample2[channel]);
        SelectInitialState(interleaved, frames, channels, baseFrame, channel, samplesPerBlock,
          sample1[channel], sample2[channel], out predictorIndex[channel], out delta[channel]);
      }

      var p = blockOffset;
      for (var channel = 0; channel < channels; ++channel)
        output[p++] = (byte)predictorIndex[channel];
      for (var channel = 0; channel < channels; ++channel) {
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(p, 2), (short)delta[channel]);
        p += 2;
      }
      for (var channel = 0; channel < channels; ++channel) {
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(p, 2), (short)sample1[channel]);
        p += 2;
      }
      for (var channel = 0; channel < channels; ++channel) {
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(p, 2), (short)sample2[channel]);
        p += 2;
      }

      if (channels == 1) {
        for (var i = 0; i < dataBytes; ++i) {
          var frame = baseFrame + 2 + i * 2;
          var highSample = GetSample(interleaved, frames, channels, frame, 0, (short)sample1[0]);
          var high = EncodeNibble(highSample, ref predictorIndex[0], ref delta[0], ref sample1[0], ref sample2[0]);
          var lowSample = GetSample(interleaved, frames, channels, frame + 1, 0, (short)sample1[0]);
          var low = EncodeNibble(lowSample, ref predictorIndex[0], ref delta[0], ref sample1[0], ref sample2[0]);
          output[p + i] = (byte)(high << 4 | low);
        }
      } else {
        for (var i = 0; i < dataBytes; ++i) {
          var frame = baseFrame + 2 + i;
          var leftSample = GetSample(interleaved, frames, channels, frame, 0, (short)sample1[0]);
          var left = EncodeNibble(leftSample, ref predictorIndex[0], ref delta[0], ref sample1[0], ref sample2[0]);
          var rightSample = GetSample(interleaved, frames, channels, frame, 1, (short)sample1[1]);
          var right = EncodeNibble(rightSample, ref predictorIndex[1], ref delta[1], ref sample1[1], ref sample2[1]);
          output[p + i] = (byte)(left << 4 | right);
        }
      }
    }

    return output;
  }

  private static void SelectInitialState(
    ReadOnlySpan<short> interleaved,
    int frames,
    int channels,
    int baseFrame,
    int channel,
    int samplesPerBlock,
    int initialSample1,
    int initialSample2,
    out int bestPredictor,
    out int bestDelta) {

    bestPredictor = 0;
    bestDelta = MinimumDelta;
    var bestError = long.MaxValue;
    var available = Math.Min(samplesPerBlock, Math.Max(0, frames - baseFrame));
    if (available <= 2)
      return;

    Span<int> candidates = stackalloc int[10];
    for (var predictor = 0; predictor < AdaptCoeff1.Length; ++predictor) {
      var predicted = (initialSample1 * AdaptCoeff1[predictor] + initialSample2 * AdaptCoeff2[predictor]) >> 8;
      var firstTarget = GetSample(interleaved, frames, channels, baseFrame + 2, channel, (short)initialSample1);
      var residual = Math.Abs((int)firstTarget - predicted);
      var initialDifference = Math.Abs(initialSample1 - initialSample2);

      candidates[0] = MinimumDelta;
      candidates[1] = ClampDelta(initialDifference);
      for (var divisor = 1; divisor <= 8; ++divisor)
        candidates[divisor + 1] = ClampDelta((residual + divisor / 2) / divisor);

      for (var candidateIndex = 0; candidateIndex < candidates.Length; ++candidateIndex) {
        var candidateDelta = candidates[candidateIndex];
        var statePredictor = predictor;
        var stateDelta = candidateDelta;
        var s1 = initialSample1;
        var s2 = initialSample2;
        long error = 0;
        var lookAhead = Math.Min(available, 34);

        for (var sampleIndex = 2; sampleIndex < lookAhead; ++sampleIndex) {
          var target = GetSample(interleaved, frames, channels, baseFrame + sampleIndex, channel, (short)s1);
          EncodeNibble(target, ref statePredictor, ref stateDelta, ref s1, ref s2);
          var difference = (long)target - s1;
          error += difference * difference;
          if (error >= bestError)
            break;
        }

        if (error >= bestError)
          continue;
        bestError = error;
        bestPredictor = predictor;
        bestDelta = candidateDelta;
      }
    }
  }

  private static int ClampDelta(int delta) => Math.Clamp(delta, MinimumDelta, MaximumDelta);

  private static short GetSample(ReadOnlySpan<short> interleaved, int frames, int channels, int frame, int channel, short fallback)
    => frame < frames ? interleaved[frame * channels + channel] : fallback;

  private static int EncodeNibble(short sample, ref int predictorIndex, ref int delta, ref int sample1, ref int sample2) {
    var bestNibble = 0;
    var bestError = int.MaxValue;

    for (var nibble = 0; nibble < 16; ++nibble) {
      var testPredictorIndex = predictorIndex;
      var testDelta = delta;
      var testSample1 = sample1;
      var testSample2 = sample2;
      var reconstructed = DecodeNibble(nibble, ref testPredictorIndex, ref testDelta, ref testSample1, ref testSample2);
      var error = Math.Abs((int)sample - reconstructed);
      if (error >= bestError)
        continue;
      bestError = error;
      bestNibble = nibble;
      if (error == 0)
        break;
    }

    DecodeNibble(bestNibble, ref predictorIndex, ref delta, ref sample1, ref sample2);
    return bestNibble;
  }
}
