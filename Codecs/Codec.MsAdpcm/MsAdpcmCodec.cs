using System.Buffers.Binary;

namespace Codec.MsAdpcm;

/// <summary>
/// Microsoft ADPCM codec (WAV format code 0x0002). Uses the canonical seven adaptive
/// predictor pairs and the Microsoft 4-bit delta adaptation table.
/// </summary>
public static class MsAdpcmCodec {

  private static readonly int[] AdaptationTable = [
    230, 230, 230, 230, 307, 409, 512, 614,
    768, 614, 512, 409, 307, 230, 230, 230
  ];

  private static readonly int[] AdaptCoeff1 = [256, 512, 0, 192, 240, 460, 392];
  private static readonly int[] AdaptCoeff2 = [0, -256, 0, 64, 0, -208, -232];

  /// <summary>
  /// Decodes a buffer of MS-ADPCM blocks to per-channel PCM. Each block emits
  /// <c>2 + (blockAlign - 7*channels) * 2 / channels</c> samples per channel.
  /// </summary>
  public static short[][] Decode(ReadOnlySpan<byte> adpcm, int blockAlign, int channels) {
    ValidateLayout(blockAlign, channels);
    var headerBytes = 7 * channels;
    var samplesPerBlock = 2 + (blockAlign - headerBytes) * 2 / channels;
    var blockCount = adpcm.Length / blockAlign;
    var output = new short[channels][];
    for (var c = 0; c < channels; ++c) output[c] = new short[blockCount * samplesPerBlock];

    Span<int> predIndex = stackalloc int[channels];
    Span<int> delta = stackalloc int[channels];
    Span<int> sample1 = stackalloc int[channels];
    Span<int> sample2 = stackalloc int[channels];

    for (var b = 0; b < blockCount; ++b) {
      var blockStart = b * blockAlign;
      var outStart = b * samplesPerBlock;

      var p = blockStart;
      for (var c = 0; c < channels; ++c) {
        predIndex[c] = Math.Min((int)adpcm[p++], 6);
      }
      for (var c = 0; c < channels; ++c) {
        delta[c] = BinaryPrimitives.ReadInt16LittleEndian(adpcm.Slice(p, 2));
        if (delta[c] < 16) delta[c] = 16;
        p += 2;
      }
      for (var c = 0; c < channels; ++c) {
        sample1[c] = BinaryPrimitives.ReadInt16LittleEndian(adpcm.Slice(p, 2));
        p += 2;
      }
      for (var c = 0; c < channels; ++c) {
        sample2[c] = BinaryPrimitives.ReadInt16LittleEndian(adpcm.Slice(p, 2));
        p += 2;
      }

      for (var c = 0; c < channels; ++c) {
        output[c][outStart] = (short)sample2[c];
        output[c][outStart + 1] = (short)sample1[c];
      }

      var dataLen = blockAlign - headerBytes;
      var sampleIdx = outStart + 2;
      for (var i = 0; i < dataLen; ++i) {
        var byteVal = adpcm[p + i];
        var n1 = (byte)((byteVal >> 4) & 0x0F);
        var n2 = (byte)(byteVal & 0x0F);

        if (channels == 1) {
          output[0][sampleIdx++] = DecodeNibble(n1, predIndex[0], ref delta[0], ref sample1[0], ref sample2[0]);
          output[0][sampleIdx++] = DecodeNibble(n2, predIndex[0], ref delta[0], ref sample1[0], ref sample2[0]);
        } else {
          output[0][sampleIdx] = DecodeNibble(n1, predIndex[0], ref delta[0], ref sample1[0], ref sample2[0]);
          output[1][sampleIdx] = DecodeNibble(n2, predIndex[1], ref delta[1], ref sample1[1], ref sample2[1]);
          ++sampleIdx;
        }
      }
    }
    return output;
  }

  /// <summary>
  /// Encodes one or two equal-length PCM16 channel buffers to Microsoft ADPCM WAV blocks.
  /// For each block the encoder searches all seven standard predictor pairs and a logarithmic
  /// set of legal starting deltas, retaining the combination with the lowest reconstruction
  /// error. The final block is padded with the last reconstructed sample.
  /// </summary>
  public static byte[] Encode(IReadOnlyList<short[]> pcm, int blockAlign) {
    ArgumentNullException.ThrowIfNull(pcm);
    var channels = pcm.Count;
    ValidateLayout(blockAlign, channels);
    if (pcm.Any(static c => c is null))
      throw new ArgumentException("PCM channel buffers cannot be null.", nameof(pcm));

    var sampleCount = pcm[0].Length;
    if (pcm.Any(c => c.Length != sampleCount))
      throw new ArgumentException("All PCM channel buffers must have the same sample count.", nameof(pcm));
    if (sampleCount == 0)
      return [];

    var headerBytes = 7 * channels;
    var dataLen = blockAlign - headerBytes;
    var samplesPerBlock = 2 + dataLen * 2 / channels;
    var nibblesPerChannel = samplesPerBlock - 2;
    var blockCount = (sampleCount + samplesPerBlock - 1) / samplesPerBlock;
    var output = new byte[blockCount * blockAlign];

    for (var b = 0; b < blockCount; ++b) {
      var baseSample = b * samplesPerBlock;
      var predictorIndex = new int[channels];
      var initialDelta = new int[channels];
      var sample1 = new short[channels];
      var sample2 = new short[channels];
      var channelNibbles = new byte[channels][];

      for (var c = 0; c < channels; ++c) {
        sample2[c] = pcm[c][Math.Min(baseSample, sampleCount - 1)];
        sample1[c] = baseSample + 1 < sampleCount ? pcm[c][baseSample + 1] : sample2[c];
        (predictorIndex[c], initialDelta[c], channelNibbles[c]) = SelectBlockEncoding(
          pcm[c], baseSample, nibblesPerChannel, sample1[c], sample2[c]);
      }

      var p = b * blockAlign;
      for (var c = 0; c < channels; ++c)
        output[p++] = (byte)predictorIndex[c];
      for (var c = 0; c < channels; ++c) {
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(p, 2), (short)initialDelta[c]);
        p += 2;
      }
      for (var c = 0; c < channels; ++c) {
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(p, 2), sample1[c]);
        p += 2;
      }
      for (var c = 0; c < channels; ++c) {
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(p, 2), sample2[c]);
        p += 2;
      }

      if (channels == 1) {
        for (var i = 0; i < dataLen; ++i)
          output[p + i] = (byte)((channelNibbles[0][i * 2] << 4) | channelNibbles[0][i * 2 + 1]);
      } else {
        for (var i = 0; i < dataLen; ++i)
          output[p + i] = (byte)((channelNibbles[0][i] << 4) | channelNibbles[1][i]);
      }
    }

    return output;
  }

  private static (int Predictor, int Delta, byte[] Nibbles) SelectBlockEncoding(
      short[] pcm, int baseSample, int nibbleCount, short firstHistory, short secondHistory) {
    var bestError = long.MaxValue;
    var bestPredictor = 0;
    var bestDelta = 16;
    byte[] bestNibbles = new byte[nibbleCount];

    Span<int> deltaCandidates = stackalloc int[13];
    var candidateCount = 0;
    for (var d = 16; d <= 32767 && candidateCount < deltaCandidates.Length; d <<= 1)
      deltaCandidates[candidateCount++] = d;
    deltaCandidates[candidateCount - 1] = 32767;

    for (var predictor = 0; predictor < AdaptCoeff1.Length; ++predictor) {
      for (var candidate = 0; candidate < candidateCount; ++candidate) {
        var delta = deltaCandidates[candidate];
        var s1 = (int)firstHistory;
        var s2 = (int)secondHistory;
        var nibbles = new byte[nibbleCount];
        long error = 0;

        for (var i = 0; i < nibbleCount; ++i) {
          var sourceIndex = baseSample + 2 + i;
          var target = sourceIndex < pcm.Length ? pcm[sourceIndex] : (short)s1;
          var nibble = EncodeNibble(target, predictor, ref delta, ref s1, ref s2);
          nibbles[i] = nibble;
          var diff = target - s1;
          error += (long)diff * diff;
          if (error >= bestError)
            break;
        }

        if (error >= bestError)
          continue;
        bestError = error;
        bestPredictor = predictor;
        bestDelta = deltaCandidates[candidate];
        bestNibbles = nibbles;
      }
    }

    return (bestPredictor, bestDelta, bestNibbles);
  }

  private static byte EncodeNibble(int sample, int predIndex, ref int delta, ref int sample1, ref int sample2) {
    var predicted = (sample1 * AdaptCoeff1[predIndex] + sample2 * AdaptCoeff2[predIndex]) >> 8;
    var residual = sample - predicted;
    var quantized = residual >= 0
      ? (residual + delta / 2) / delta
      : -((-residual + delta / 2) / delta);
    quantized = Math.Clamp(quantized, -8, 7);
    var nibble = (byte)(quantized & 0x0F);
    DecodeNibble(nibble, predIndex, ref delta, ref sample1, ref sample2);
    return nibble;
  }

  private static short DecodeNibble(byte nibble, int predIndex, ref int delta, ref int sample1, ref int sample2) {
    var signed = nibble < 8 ? nibble : nibble - 16;
    var predicted = (sample1 * AdaptCoeff1[predIndex] + sample2 * AdaptCoeff2[predIndex]) >> 8;
    predicted += signed * delta;
    predicted = Math.Clamp(predicted, short.MinValue, short.MaxValue);
    sample2 = sample1;
    sample1 = predicted;
    delta = AdaptationTable[nibble] * delta >> 8;
    if (delta < 16) delta = 16;
    return (short)predicted;
  }

  private static void ValidateLayout(int blockAlign, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("MS ADPCM supports 1 or 2 channels.", nameof(channels));
    var headerBytes = 7 * channels;
    if (blockAlign < headerBytes)
      throw new ArgumentException($"blockAlign {blockAlign} too small for {channels} channel(s).", nameof(blockAlign));
  }
}
