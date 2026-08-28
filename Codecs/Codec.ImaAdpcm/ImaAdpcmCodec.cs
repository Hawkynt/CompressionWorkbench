using System.Buffers.Binary;

namespace Codec.ImaAdpcm;

/// <summary>
/// IMA ADPCM (Interactive Multimedia Association Adaptive Differential PCM) codec.
/// Supports the Microsoft/Intel WAV block layout and Apple/QuickTime <c>ima4</c> packets.
/// </summary>
public static class ImaAdpcmCodec {

  private static readonly int[] StepTable = [
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
    34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
    157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
    724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
    3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
  ];

  private static readonly int[] IndexAdjust = [-1, -1, -1, -1, 2, 4, 6, 8];

  private const int WavHeaderBytes = 4;
  private const int QuickTimePacketBytes = 34;
  private const int QuickTimeSamplesPerPacket = 64;

  /// <summary>
  /// Decodes IMA ADPCM data to one PCM buffer per channel. Each output buffer holds
  /// <c>((blockAlign/channels - 4) * 2 + 1)</c> samples per block.
  /// </summary>
  public static short[][] Decode(ReadOnlySpan<byte> adpcm, int blockAlign, int channels) {
    ValidateWavLayout(blockAlign, channels);

    var samplesPerBlock = (blockAlign - WavHeaderBytes * channels) * 2 / channels + 1;
    var blockCount = adpcm.Length / blockAlign;
    var output = new short[channels][];
    for (var c = 0; c < channels; ++c) output[c] = new short[blockCount * samplesPerBlock];

    Span<int> predictor = stackalloc int[channels];
    Span<int> index = stackalloc int[channels];

    for (var b = 0; b < blockCount; ++b) {
      var blockStart = b * blockAlign;
      var outStart = b * samplesPerBlock;

      for (var c = 0; c < channels; ++c) {
        var h = blockStart + c * WavHeaderBytes;
        predictor[c] = BinaryPrimitives.ReadInt16LittleEndian(adpcm.Slice(h, 2));
        index[c] = Math.Min((int)adpcm[h + 2], 88);
        output[c][outStart] = (short)predictor[c];
      }

      var dataStart = blockStart + WavHeaderBytes * channels;
      var dataLen = blockAlign - WavHeaderBytes * channels;

      if (channels == 1) {
        for (var i = 0; i < dataLen; ++i) {
          var byteVal = adpcm[dataStart + i];
          output[0][outStart + 1 + i * 2] = DecodeNibble((byte)(byteVal & 0x0F), ref predictor[0], ref index[0]);
          output[0][outStart + 2 + i * 2] = DecodeNibble((byte)(byteVal >> 4), ref predictor[0], ref index[0]);
        }
      } else {
        var groups = dataLen / 8;
        for (var g = 0; g < groups; ++g) {
          for (var c = 0; c < 2; ++c) {
            var gs = dataStart + g * 8 + c * 4;
            for (var i = 0; i < 4; ++i) {
              var byteVal = adpcm[gs + i];
              var sampleIdx = outStart + 1 + g * 8 + i * 2;
              output[c][sampleIdx] = DecodeNibble((byte)(byteVal & 0x0F), ref predictor[c], ref index[c]);
              output[c][sampleIdx + 1] = DecodeNibble((byte)(byteVal >> 4), ref predictor[c], ref index[c]);
            }
          }
        }
      }
    }
    return output;
  }

  /// <summary>
  /// Encodes one or two equal-length PCM16 channel buffers to Microsoft/Intel IMA ADPCM WAV blocks.
  /// The final block is padded with the last reconstructed sample so the raw coded stream remains
  /// block-aligned; a container can retain the exact source sample count in its own metadata.
  /// </summary>
  public static byte[] Encode(IReadOnlyList<short[]> pcm, int blockAlign) {
    ArgumentNullException.ThrowIfNull(pcm);
    var channels = pcm.Count;
    ValidateWavLayout(blockAlign, channels);
    if (pcm.Any(static c => c is null))
      throw new ArgumentException("PCM channel buffers cannot be null.", nameof(pcm));

    var sampleCount = pcm[0].Length;
    if (pcm.Any(c => c.Length != sampleCount))
      throw new ArgumentException("All PCM channel buffers must have the same sample count.", nameof(pcm));
    if (sampleCount == 0)
      return [];

    var dataLen = blockAlign - WavHeaderBytes * channels;
    var samplesPerBlock = dataLen * 2 / channels + 1;
    var blockCount = (sampleCount + samplesPerBlock - 1) / samplesPerBlock;
    var output = new byte[blockCount * blockAlign];

    Span<int> predictor = stackalloc int[channels];
    Span<int> index = stackalloc int[channels];

    for (var b = 0; b < blockCount; ++b) {
      var blockStart = b * blockAlign;
      var baseSample = b * samplesPerBlock;

      for (var c = 0; c < channels; ++c) {
        var first = pcm[c][Math.Min(baseSample, sampleCount - 1)];
        var second = baseSample + 1 < sampleCount ? pcm[c][baseSample + 1] : first;
        predictor[c] = first;
        index[c] = StartIndexFor(Math.Abs(second - first));
        var h = blockStart + c * WavHeaderBytes;
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(h, 2), first);
        output[h + 2] = (byte)index[c];
        output[h + 3] = 0;
      }

      var dataStart = blockStart + WavHeaderBytes * channels;
      if (channels == 1) {
        for (var i = 0; i < dataLen; ++i) {
          var sampleIdx = baseSample + 1 + i * 2;
          var low = EncodeNibble(Sample(pcm[0], sampleIdx, predictor[0]), ref predictor[0], ref index[0]);
          var high = EncodeNibble(Sample(pcm[0], sampleIdx + 1, predictor[0]), ref predictor[0], ref index[0]);
          output[dataStart + i] = (byte)((high << 4) | low);
        }
      } else {
        var groups = dataLen / 8;
        for (var g = 0; g < groups; ++g) {
          for (var c = 0; c < 2; ++c) {
            var gs = dataStart + g * 8 + c * 4;
            for (var i = 0; i < 4; ++i) {
              var sampleIdx = baseSample + 1 + g * 8 + i * 2;
              var low = EncodeNibble(Sample(pcm[c], sampleIdx, predictor[c]), ref predictor[c], ref index[c]);
              var high = EncodeNibble(Sample(pcm[c], sampleIdx + 1, predictor[c]), ref predictor[c], ref index[c]);
              output[gs + i] = (byte)((high << 4) | low);
            }
          }
        }
      }
    }

    return output;
  }

  /// <summary>
  /// Decodes the Apple/QuickTime <c>ima4</c> packet variant (as carried by AIFC) into
  /// one PCM buffer per channel. Packets are 34 bytes and round-robin through channels.
  /// </summary>
  public static short[][] DecodeQuickTime(ReadOnlySpan<byte> data, int channels) {
    if (channels < 1)
      throw new ArgumentException("QuickTime IMA ADPCM needs at least one channel.", nameof(channels));

    var packetCount = data.Length / QuickTimePacketBytes;
    var packetsPerChannel = packetCount / channels;

    var output = new short[channels][];
    for (var c = 0; c < channels; ++c)
      output[c] = new short[packetsPerChannel * QuickTimeSamplesPerPacket];

    for (var p = 0; p < packetsPerChannel * channels; ++p) {
      var channel = p % channels;
      var packet = data.Slice(p * QuickTimePacketBytes, QuickTimePacketBytes);

      var preamble = BinaryPrimitives.ReadUInt16BigEndian(packet);
      var predictor = (int)(short)(preamble & 0xFF80);
      var index = Math.Min(preamble & 0x007F, 88);

      var outBase = (p / channels) * QuickTimeSamplesPerPacket;
      for (var i = 0; i < 32; ++i) {
        var byteVal = packet[2 + i];
        output[channel][outBase + i * 2] = DecodeNibble((byte)(byteVal & 0x0F), ref predictor, ref index);
        output[channel][outBase + i * 2 + 1] = DecodeNibble((byte)(byteVal >> 4), ref predictor, ref index);
      }
    }

    return output;
  }

  /// <summary>
  /// Encodes equal-length PCM16 channel buffers to Apple/QuickTime <c>ima4</c> packets.
  /// A final partial packet is padded with the last reconstructed sample. Packets are emitted
  /// in the channel-round-robin order used by AIFC/QuickTime.
  /// </summary>
  public static byte[] EncodeQuickTime(IReadOnlyList<short[]> pcm) {
    ArgumentNullException.ThrowIfNull(pcm);
    var channels = pcm.Count;
    if (channels < 1)
      throw new ArgumentException("QuickTime IMA ADPCM needs at least one channel.", nameof(pcm));
    if (pcm.Any(static c => c is null))
      throw new ArgumentException("PCM channel buffers cannot be null.", nameof(pcm));

    var sampleCount = pcm[0].Length;
    if (pcm.Any(c => c.Length != sampleCount))
      throw new ArgumentException("All PCM channel buffers must have the same sample count.", nameof(pcm));
    if (sampleCount == 0)
      return [];

    var packetsPerChannel = (sampleCount + QuickTimeSamplesPerPacket - 1) / QuickTimeSamplesPerPacket;
    var output = new byte[packetsPerChannel * channels * QuickTimePacketBytes];

    for (var packetIndex = 0; packetIndex < packetsPerChannel; ++packetIndex) {
      var baseSample = packetIndex * QuickTimeSamplesPerPacket;
      for (var c = 0; c < channels; ++c) {
        var packetOffset = (packetIndex * channels + c) * QuickTimePacketBytes;
        var first = pcm[c][Math.Min(baseSample, sampleCount - 1)];
        var predictor = (int)(short)(first & ~0x7F);
        var second = baseSample + 1 < sampleCount ? pcm[c][baseSample + 1] : first;
        var index = StartIndexFor(Math.Max(Math.Abs(first - predictor), Math.Abs(second - first)));
        var preamble = (ushort)(((ushort)predictor & 0xFF80) | index);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(packetOffset, 2), preamble);

        for (var i = 0; i < 32; ++i) {
          var sampleIdx = baseSample + i * 2;
          var low = EncodeNibble(Sample(pcm[c], sampleIdx, predictor), ref predictor, ref index);
          var high = EncodeNibble(Sample(pcm[c], sampleIdx + 1, predictor), ref predictor, ref index);
          output[packetOffset + 2 + i] = (byte)((high << 4) | low);
        }
      }
    }

    return output;
  }

  private static void ValidateWavLayout(int blockAlign, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("IMA ADPCM in WAV supports 1 or 2 channels.", nameof(channels));
    if (blockAlign < WavHeaderBytes * channels)
      throw new ArgumentException($"blockAlign {blockAlign} too small for {channels} channel(s).", nameof(blockAlign));
    var dataLen = blockAlign - WavHeaderBytes * channels;
    if (channels == 2 && dataLen % 8 != 0)
      throw new ArgumentException("Stereo IMA ADPCM block data must contain complete 4-byte groups per channel.", nameof(blockAlign));
  }

  private static short Sample(short[] pcm, int index, int fallback)
    => index < pcm.Length ? pcm[index] : (short)fallback;

  private static int StartIndexFor(int delta) {
    var i = 0;
    while (i < StepTable.Length - 1 && StepTable[i] < delta)
      ++i;
    return i;
  }

  private static byte EncodeNibble(int sample, ref int predictor, ref int index) {
    var step = StepTable[index];
    var delta = sample - predictor;
    byte nibble = 0;
    if (delta < 0) {
      nibble = 8;
      delta = -delta;
    }
    if (delta >= step) { nibble |= 4; delta -= step; }
    if (delta >= step >> 1) { nibble |= 2; delta -= step >> 1; }
    if (delta >= step >> 2) nibble |= 1;
    DecodeNibble(nibble, ref predictor, ref index);
    return nibble;
  }

  private static short DecodeNibble(byte nibble, ref int predictor, ref int index) {
    var step = StepTable[index];
    var diff = step >> 3;
    if ((nibble & 1) != 0) diff += step >> 2;
    if ((nibble & 2) != 0) diff += step >> 1;
    if ((nibble & 4) != 0) diff += step;
    if ((nibble & 8) != 0) predictor -= diff;
    else predictor += diff;
    predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
    index = Math.Clamp(index + IndexAdjust[nibble & 0x07], 0, 88);
    return (short)predictor;
  }
}
