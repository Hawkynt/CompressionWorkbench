namespace FileFormat.Mpq;

/// <summary>
/// MPQ compression methods 0x40 (mono) and 0x80 (stereo): Blizzard's IMA-ADPCM variant for
/// 16-bit PCM WAVE sectors. Lossy: the decoder reproduces exactly what Storm/StormLib decode.
/// </summary>
/// <remarks>
/// Converted from StormLib <c>src/adpcm/adpcm.cpp</c> (Ladislav Zezula, after Tom Amigo; MIT) —
/// see <c>THIRD-PARTY-NOTICE.StormLib.txt</c>. The step tables are copied exactly. Stream: a zero
/// byte, the bit shift (compression level minus one), one initial 16-bit sample per channel, then
/// one byte per sample; 0x80 repeats the prediction and lowers the step, 0x81 raises the step
/// by eight without producing a sample.
/// </remarks>
internal static class MpqAdpcm {

  private const int InitialStepIndex = 0x2C;

  private static ReadOnlySpan<sbyte> NextStep => [
    -1, 0, -1, 4, -1, 2, -1, 6,
    -1, 1, -1, 5, -1, 3, -1, 7,
    -1, 1, -1, 5, -1, 3, -1, 7,
    -1, 2, -1, 4, -1, 6, -1, 8,
  ];

  private static ReadOnlySpan<short> StepSize => [
        7,     8,     9,    10,    11,    12,    13,    14,
       16,    17,    19,    21,    23,    25,    28,    31,
       34,    37,    41,    45,    50,    55,    60,    66,
       73,    80,    88,    97,   107,   118,   130,   143,
      157,   173,   190,   209,   230,   253,   279,   307,
      337,   371,   408,   449,   494,   544,   598,   658,
      724,   796,   876,   963,  1060,  1166,  1282,  1411,
     1552,  1707,  1878,  2066,  2272,  2499,  2749,  3024,
     3327,  3660,  4026,  4428,  4871,  5358,  5894,  6484,
     7132,  7845,  8630,  9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
    32767,
  ];

  /// <summary>Decodes into at most <paramref name="maxOutput"/> bytes for 1 or 2 channels.</summary>
  internal static byte[] Decompress(ReadOnlySpan<byte> input, int maxOutput, int channels) {
    var output = new byte[maxOutput];
    var written = 0;
    var predicted = new short[2];
    var stepIndex = new short[] { InitialStepIndex, InitialStepIndex };

    if (input.Length < 2)
      return [];
    var bitShift = input[1];
    var position = 2;

    for (var i = 0; i < channels; ++i) {
      if (position + 2 > input.Length)
        return output[..written];
      var sample = (short)(input[position] | (input[position + 1] << 8));
      position += 2;
      predicted[i] = sample;
      if (!TryWrite(output, ref written, sample))
        return output[..written];
    }

    var channel = channels - 1;
    while (position < input.Length) {
      var encoded = input[position++];
      channel = (channel + 1) % channels;

      if (encoded == 0x80) {
        if (stepIndex[channel] != 0)
          --stepIndex[channel];
        if (!TryWrite(output, ref written, predicted[channel]))
          return output[..written];
      } else if (encoded == 0x81) {
        stepIndex[channel] = (short)Math.Min(stepIndex[channel] + 8, 0x58);
        channel = (channel + 1) % channels;
      } else {
        int index = stepIndex[channel];
        int step = StepSize[index];
        predicted[channel] = (short)DecodeSample(predicted[channel], encoded, step, step >> bitShift);
        if (!TryWrite(output, ref written, predicted[channel]))
          break;
        stepIndex[channel] = NextStepIndex(index, encoded);
      }
    }
    return output[..written];
  }

  /// <summary>Encodes 16-bit little-endian PCM at the given level (StormLib uses 4, 5 or 6).</summary>
  internal static byte[] Compress(ReadOnlySpan<byte> input, int channels, int level) {
    var output = new List<byte>(input.Length / 2 + 8);
    var bitShift = level - 1;
    var predicted = new short[2];
    var stepIndex = new short[] { InitialStepIndex, InitialStepIndex };

    output.Add(0);
    output.Add((byte)bitShift);
    var position = 0;
    for (var i = 0; i < channels; ++i) {
      if (position + 2 > input.Length)
        return [.. output];
      var sample = (short)(input[position] | (input[position + 1] << 8));
      position += 2;
      predicted[i] = sample;
      output.Add((byte)sample);
      output.Add((byte)(sample >> 8));
    }

    var channel = channels - 1;
    while (position + 2 <= input.Length) {
      var sample = (short)(input[position] | (input[position + 1] << 8));
      position += 2;
      channel = (channel + 1) % channels;

      var encoded = 0;
      var difference = sample - predicted[channel];
      if (difference < 0) {
        difference = -difference;
        encoded |= 0x40;
      }

      int step = StepSize[stepIndex[channel]];
      if (difference < step >> level) {
        if (stepIndex[channel] != 0)
          --stepIndex[channel];
        output.Add(0x80);
        continue;
      }

      while (difference > step << 1) {
        if (stepIndex[channel] >= 0x58)
          break;
        stepIndex[channel] = (short)Math.Min(stepIndex[channel] + 8, 0x58);
        step = StepSize[stepIndex[channel]];
        output.Add(0x81);
      }

      var maxBit = Math.Min(1 << (bitShift - 1), 0x20);
      var baseDifference = step >> bitShift;
      var total = 0;
      for (var bit = 1; bit <= maxBit; bit <<= 1) {
        if (total + step <= difference) {
          total += step;
          encoded |= bit;
        }
        step >>= 1;
      }

      predicted[channel] = (short)UpdatePrediction(predicted[channel], encoded, baseDifference + total);
      output.Add((byte)encoded);
      stepIndex[channel] = NextStepIndex(stepIndex[channel], encoded);
    }
    return [.. output];
  }

  private static bool TryWrite(byte[] output, ref int written, short sample) {
    if (output.Length - written < 2)
      return false;
    output[written++] = (byte)sample;
    output[written++] = (byte)(sample >> 8);
    return true;
  }

  private static short NextStepIndex(int stepIndex, int encoded)
    => (short)Math.Clamp(stepIndex + NextStep[encoded & 0x1F], 0, 88);

  private static int UpdatePrediction(int predicted, int encoded, int difference) {
    if ((encoded & 0x40) != 0) {
      predicted -= difference;
      return predicted <= -32768 ? -32768 : predicted;
    }
    predicted += difference;
    return predicted >= 32767 ? 32767 : predicted;
  }

  private static int DecodeSample(int predicted, int encoded, int step, int difference) {
    for (var bit = 0; bit < 6; ++bit)
      if ((encoded & (1 << bit)) != 0)
        difference += step >> bit;
    return UpdatePrediction(predicted, encoded, difference);
  }
}
