using Codec.Dts;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class DtsEncoderTests {

  [TestCase(1, 384000)]
  [TestCase(2, 768000)]
  [TestCase(4, 1536000)]
  [TestCase(5, 1920000)]
  public void Encode_ProducesParseableDecodableCoreFrames(int channels, int bitrate) {
    const int sampleRate = 48000;
    var pcm = Signal(512 * 2, channels, sampleRate);

    var encoded = DtsCodec.Encode(pcm, new DtsEncoderOptions(sampleRate, channels, bitrate, ActiveSubbands: 12));
    using var infoInput = new MemoryStream(encoded);
    var info = DtsCodec.ReadStreamInfo(infoInput);
    using var input = new MemoryStream(encoded);
    using var output = new MemoryStream();
    DtsCodec.Decompress(input, output);

    Assert.Multiple(() => {
      Assert.That(info.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(info.Channels, Is.EqualTo(channels));
      Assert.That(info.Bitrate, Is.EqualTo(bitrate));
      Assert.That(info.DurationSamples, Is.EqualTo(1024));
      Assert.That(output.Length, Is.EqualTo(1024L * channels * sizeof(short)));
      Assert.That(output.ToArray().Any(value => value != 0), Is.True);
    });
  }

  [Test]
  public void Encode_PadsFinalFrame() {
    var pcm = Signal(513, 2, 48000);
    var encoded = DtsCodec.Encode(pcm, new DtsEncoderOptions());
    using var input = new MemoryStream(encoded);
    var info = DtsCodec.ReadStreamInfo(input);
    Assert.That(info.DurationSamples, Is.EqualTo(1024));
  }

  [Test]
  public void Encode_RejectsPartialFrameWhenPaddingDisabled() {
    var pcm = Signal(513, 2, 48000);
    Assert.Throws<ArgumentException>(() =>
      DtsCodec.Encode(pcm, new DtsEncoderOptions(PadFinalFrame: false)));
  }

  [Test]
  public void Encode_RejectsUnsupportedLayout() {
    var pcm = Signal(512, 3, 48000);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      DtsCodec.Encode(pcm, new DtsEncoderOptions(Channels: 3)));
  }

  private static short[] Signal(int samplesPerChannel, int channels, int sampleRate) {
    var result = new short[samplesPerChannel * channels];
    for (var n = 0; n < samplesPerChannel; ++n) {
      for (var ch = 0; ch < channels; ++ch) {
        var frequency = 180 + 137 * ch;
        var t = n / (double)sampleRate;
        var sample = Math.Sin(2 * Math.PI * frequency * t) * 10000
                   + Math.Sin(2 * Math.PI * (frequency * 2.3) * t) * 2600;
        result[n * channels + ch] = (short)Math.Round(sample);
      }
    }
    return result;
  }
}
