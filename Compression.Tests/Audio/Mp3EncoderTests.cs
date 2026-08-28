using Codec.Mp3;

namespace Compression.Tests.Audio;

[TestFixture]
public class Mp3EncoderTests {

  [TestCase(32000, 1, 64, Mp3EncoderChannelMode.Mono)]
  [TestCase(44100, 2, 128, Mp3EncoderChannelMode.Stereo)]
  [TestCase(44100, 2, 128, Mp3EncoderChannelMode.JointStereo)]
  [TestCase(48000, 2, 192, Mp3EncoderChannelMode.DualChannel)]
  public void Encode_CbrModes_AreParsedAndDecoded(int sampleRate, int channels, int bitrate, Mp3EncoderChannelMode mode) {
    var pcm = BuildSignal(sampleRate / 2, channels, sampleRate);
    var encoded = Mp3Encoder.Encode(pcm, new Mp3EncoderOptions(sampleRate, channels, bitrate, mode, Quality: 5));

    using var infoStream = new MemoryStream(encoded, writable: false);
    var info = Mp3Codec.ReadStreamInfo(infoStream);
    var decoded = Decode(encoded);

    Assert.Multiple(() => {
      Assert.That(encoded.Length, Is.GreaterThan(100));
      Assert.That(info.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(info.Channels, Is.EqualTo(channels));
      Assert.That(info.Bitrate, Is.EqualTo(bitrate * 1000));
      Assert.That(decoded.Length, Is.GreaterThan(0));
      Assert.That(decoded.Any(static sample => sample != 0), Is.True);
    });
  }

  [TestCase(1)]
  [TestCase(3)]
  [TestCase(5)]
  [TestCase(7)]
  [TestCase(9)]
  public void Encode_Vbr_AllQualitySettings_Decode(int quality) {
    var pcm = BuildSignal(44100, 2, 44100);
    var encoded = Mp3Encoder.Encode(pcm, new Mp3EncoderOptions(
      44100, 2,
      BitrateKbps: -1,
      ChannelMode: Mp3EncoderChannelMode.JointStereo,
      Quality: quality,
      VariableBitrate: true));

    using var input = new MemoryStream(encoded, writable: false);
    var info = Mp3Codec.ReadStreamInfo(input);
    var decoded = Decode(encoded);

    Assert.Multiple(() => {
      Assert.That(info.SampleRate, Is.EqualTo(44100));
      Assert.That(info.Channels, Is.EqualTo(2));
      Assert.That(decoded.Length, Is.GreaterThan(0));
      Assert.That(decoded.Any(static sample => sample != 0), Is.True);
    });
  }

  [Test]
  public void Encode_OutputResampling_ChangesHeaderRate() {
    var pcm = BuildSignal(48000 / 2, 1, 48000);
    var encoded = Mp3Encoder.Encode(pcm, new Mp3EncoderOptions(
      48000, 1,
      BitrateKbps: 64,
      ChannelMode: Mp3EncoderChannelMode.Mono,
      OutputSampleRate: 24000));

    using var stream = new MemoryStream(encoded, writable: false);
    var info = Mp3Codec.ReadStreamInfo(stream);
    Assert.Multiple(() => {
      Assert.That(info.SampleRate, Is.EqualTo(24000));
      Assert.That(info.Channels, Is.EqualTo(1));
    });
  }

  [Test]
  public void Encode_HigherCbrBitrate_ProducesLargerStream() {
    var pcm = BuildSignal(44100 * 2, 2, 44100);
    var low = Mp3Encoder.Encode(pcm, new Mp3EncoderOptions(44100, 2, 64, Mp3EncoderChannelMode.JointStereo));
    var high = Mp3Encoder.Encode(pcm, new Mp3EncoderOptions(44100, 2, 256, Mp3EncoderChannelMode.JointStereo));
    Assert.That(high.Length, Is.GreaterThan(low.Length * 2));
  }

  [Test]
  public void Encode_RejectsStereoModeForMonoInput() {
    Assert.Throws<ArgumentException>(() => Mp3Encoder.Encode(
      new short[1152],
      new Mp3EncoderOptions(44100, 1, ChannelMode: Mp3EncoderChannelMode.Stereo)));
  }

  private static short[] BuildSignal(int frames, int channels, int sampleRate) {
    var result = new short[frames * channels];
    for (var frame = 0; frame < frames; ++frame)
      for (var c = 0; c < channels; ++c) {
        var a = Math.Sin(2 * Math.PI * (240 + c * 170) * frame / sampleRate) * 11000;
        var b = Math.Sin(2 * Math.PI * (710 + c * 90) * frame / sampleRate) * 3300;
        result[frame * channels + c] = (short)(a + b);
      }
    return result;
  }

  private static short[] Decode(byte[] mp3) {
    using var input = new MemoryStream(mp3, writable: false);
    using var output = new MemoryStream();
    Mp3Codec.Decompress(input, output);
    var bytes = output.ToArray();
    var pcm = new short[bytes.Length / 2];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(bytes[i * 2] | bytes[i * 2 + 1] << 8);
    return pcm;
  }
}
