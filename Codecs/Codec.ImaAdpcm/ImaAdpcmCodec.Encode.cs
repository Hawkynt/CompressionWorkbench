using System.Buffers.Binary;

namespace Codec.ImaAdpcm;

public static partial class ImaAdpcmCodec {

  private const int WaveHeaderBytesPerChannel = 4;
  private const int WaveStereoGroupBytesPerChannel = 4;
  private const int QuickTimePacketBytes = 34;
  private const int QuickTimeSamplesPerPacket = 64;

  /// <summary>
  /// Encodes interleaved 16-bit PCM into Microsoft/IMA WAV ADPCM blocks.
  /// The output always consists of whole <paramref name="blockAlign"/> byte blocks;
  /// a short final block is padded with the last reconstructed sample.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, int channels, int blockAlign) {
    ValidateWaveLayout(interleaved, channels, blockAlign);
    if (interleaved.IsEmpty)
      return [];

    var dataBytes = blockAlign - WaveHeaderBytesPerChannel * channels;
    var samplesPerBlock = dataBytes * 2 / channels + 1;
    var frames = interleaved.Length / channels;
    var blockCount = (frames + samplesPerBlock - 1) / samplesPerBlock;
    var output = new byte[blockCount * blockAlign];
    Span<int> predictor = stackalloc int[channels];
    Span<int> index = stackalloc int[channels];

    for (var block = 0; block < blockCount; ++block) {
      var blockOffset = block * blockAlign;
      var baseFrame = block * samplesPerBlock;

      for (var channel = 0; channel < channels; ++channel) {
        var first = GetSample(interleaved, frames, channels, baseFrame, channel, 0);
        var second = GetSample(interleaved, frames, channels, baseFrame + 1, channel, first);
        predictor[channel] = first;
        index[channel] = StartIndexFor(Math.Abs((int)second - first));

        var headerOffset = blockOffset + channel * WaveHeaderBytesPerChannel;
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(headerOffset, 2), first);
        output[headerOffset + 2] = (byte)index[channel];
        output[headerOffset + 3] = 0;
      }

      var dataOffset = blockOffset + WaveHeaderBytesPerChannel * channels;
      if (channels == 1) {
        for (var i = 0; i < dataBytes; ++i) {
          var frame = baseFrame + 1 + i * 2;
          var lowSample = GetSample(interleaved, frames, channels, frame, 0, (short)predictor[0]);
          var low = EncodeNibble(lowSample, ref predictor[0], ref index[0]);
          var highSample = GetSample(interleaved, frames, channels, frame + 1, 0, (short)predictor[0]);
          var high = EncodeNibble(highSample, ref predictor[0], ref index[0]);
          output[dataOffset + i] = (byte)(low | high << 4);
        }
      } else {
        var groups = dataBytes / (WaveStereoGroupBytesPerChannel * channels);
        for (var group = 0; group < groups; ++group) {
          for (var channel = 0; channel < channels; ++channel) {
            var groupOffset = dataOffset + (group * channels + channel) * WaveStereoGroupBytesPerChannel;
            for (var i = 0; i < WaveStereoGroupBytesPerChannel; ++i) {
              var sampleInBlock = 1 + group * 8 + i * 2;
              var lowSample = GetSample(interleaved, frames, channels, baseFrame + sampleInBlock, channel, (short)predictor[channel]);
              var low = EncodeNibble(lowSample, ref predictor[channel], ref index[channel]);
              var highSample = GetSample(interleaved, frames, channels, baseFrame + sampleInBlock + 1, channel, (short)predictor[channel]);
              var high = EncodeNibble(highSample, ref predictor[channel], ref index[channel]);
              output[groupOffset + i] = (byte)(low | high << 4);
            }
          }
        }
      }
    }

    return output;
  }

  /// <summary>
  /// Encodes interleaved 16-bit PCM into Apple/QuickTime <c>ima4</c> packets.
  /// Packets contain 64 samples for one channel and are emitted round-robin by channel.
  /// A short final packet is padded with the last reconstructed sample.
  /// </summary>
  public static byte[] EncodeQuickTime(ReadOnlySpan<short> interleaved, int channels) {
    if (channels < 1)
      throw new ArgumentException("QuickTime IMA ADPCM needs at least one channel.", nameof(channels));
    if (interleaved.Length % channels != 0)
      throw new ArgumentException("Interleaved sample count must be a multiple of the channel count.", nameof(interleaved));
    if (interleaved.IsEmpty)
      return [];

    var frames = interleaved.Length / channels;
    var packetsPerChannel = (frames + QuickTimeSamplesPerPacket - 1) / QuickTimeSamplesPerPacket;
    var output = new byte[packetsPerChannel * channels * QuickTimePacketBytes];

    for (var packetIndex = 0; packetIndex < packetsPerChannel; ++packetIndex) {
      var baseFrame = packetIndex * QuickTimeSamplesPerPacket;
      for (var channel = 0; channel < channels; ++channel) {
        var packetNumber = packetIndex * channels + channel;
        var packetOffset = packetNumber * QuickTimePacketBytes;
        var first = GetSample(interleaved, frames, channels, baseFrame, channel, 0);
        var predictor = (int)(short)(first & ~0x7F);
        var second = GetSample(interleaved, frames, channels, baseFrame + 1, channel, first);
        var index = StartIndexFor(Math.Abs((int)second - first));
        var preamble = (ushort)((predictor & 0xFF80) | index);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(packetOffset, 2), preamble);

        for (var i = 0; i < 32; ++i) {
          var frame = baseFrame + i * 2;
          var lowSample = GetSample(interleaved, frames, channels, frame, channel, (short)predictor);
          var low = EncodeNibble(lowSample, ref predictor, ref index);
          var highSample = GetSample(interleaved, frames, channels, frame + 1, channel, (short)predictor);
          var high = EncodeNibble(highSample, ref predictor, ref index);
          output[packetOffset + 2 + i] = (byte)(low | high << 4);
        }
      }
    }

    return output;
  }

  private static void ValidateWaveLayout(ReadOnlySpan<short> interleaved, int channels, int blockAlign) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("IMA ADPCM in WAV supports 1 or 2 channels.", nameof(channels));
    if (interleaved.Length % channels != 0)
      throw new ArgumentException("Interleaved sample count must be a multiple of the channel count.", nameof(interleaved));

    var headerBytes = WaveHeaderBytesPerChannel * channels;
    if (blockAlign < headerBytes)
      throw new ArgumentException($"blockAlign {blockAlign} too small for {channels} channel(s).", nameof(blockAlign));
    if (channels == 2 && (blockAlign - headerBytes) % (WaveStereoGroupBytesPerChannel * channels) != 0)
      throw new ArgumentException("Stereo IMA WAV data must contain whole 4-byte groups per channel.", nameof(blockAlign));
  }

  private static short GetSample(ReadOnlySpan<short> interleaved, int frames, int channels, int frame, int channel, short fallback)
    => frame < frames ? interleaved[frame * channels + channel] : fallback;

  private static int StartIndexFor(int delta) {
    var index = 0;
    while (index < StepTable.Length - 1 && StepTable[index] < delta)
      ++index;
    return index;
  }

  private static byte EncodeNibble(short sample, ref int predictor, ref int index) {
    var step = StepTable[index];
    var delta = (int)sample - predictor;
    byte nibble = 0;
    if (delta < 0) {
      nibble = 8;
      delta = -delta;
    }

    if (delta >= step) {
      nibble |= 4;
      delta -= step;
    }
    if (delta >= step >> 1) {
      nibble |= 2;
      delta -= step >> 1;
    }
    if (delta >= step >> 2)
      nibble |= 1;

    DecodeNibble(nibble, ref predictor, ref index);
    return nibble;
  }
}
