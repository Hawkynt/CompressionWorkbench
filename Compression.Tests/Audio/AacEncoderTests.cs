using Codec.Aac;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class AacEncoderTests {

  [TestCase(1, AacEncoderWindowShape.Sine, AacStereoCodingMode.Auto)]
  [TestCase(2, AacEncoderWindowShape.Sine, AacStereoCodingMode.Independent)]
  [TestCase(2, AacEncoderWindowShape.Kbd, AacStereoCodingMode.MidSide)]
  public void Encode_AacLc_ProducesSelfDecodableAdts(
    int channels,
    AacEncoderWindowShape windowShape,
    AacStereoCodingMode stereoMode) {
    const int sampleRate = 44_100;
    var pcm = Signal(AacEncoder.FrameSamples * 2, channels, sampleRate);
    var encoded = AacEncoder.Encode(pcm, new AacEncoderOptions(
      sampleRate,
      channels,
      Bitrate: channels == 1 ? 96_000 : 160_000,
      WindowShape: windowShape,
      StereoMode: stereoMode));

    using var input = new MemoryStream(encoded, writable: false);
    var info = AacCodec.ReadStreamInfo(input);
    input.Position = 0;
    using var decoded = new MemoryStream();
    AacCodec.Decompress(input, decoded);

    Assert.Multiple(() => {
      Assert.That(encoded, Has.Length.GreaterThan(AacAdtsReader.ShortHeaderLength));
      Assert.That(encoded[0], Is.EqualTo(0xFF));
      Assert.That(encoded[1] & 0xF0, Is.EqualTo(0xF0));
      Assert.That(info.Profile, Is.EqualTo((int)AacObjectType.AacLc));
      Assert.That(info.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(info.Channels, Is.EqualTo(channels));
      Assert.That(info.Sbr, Is.False);
      Assert.That(info.DurationSamples, Is.EqualTo(3L * AacEncoder.FrameSamples));
      Assert.That(decoded.Length, Is.EqualTo(3L * AacEncoder.FrameSamples * channels * sizeof(short)));
      Assert.That(decoded.ToArray().Any(static value => value != 0), Is.True);
    });
  }

  [Test]
  public void Encode_PadsPartialFinalFrame() {
    const int sampleRate = 48_000;
    var pcm = Signal(AacEncoder.FrameSamples + 37, channels: 1, sampleRate);

    var encoded = AacEncoder.Encode(pcm, new AacEncoderOptions(sampleRate, Channels: 1));
    using var input = new MemoryStream(encoded, writable: false);
    var info = AacCodec.ReadStreamInfo(input);

    // Two source blocks after padding plus the required AAC overlap tail block.
    Assert.That(info.DurationSamples, Is.EqualTo(3L * AacEncoder.FrameSamples));
  }

  [Test]
  public void Encode_RejectsPartialFinalFrameWhenPaddingDisabled() {
    var pcm = Signal(AacEncoder.FrameSamples + 1, channels: 1, sampleRate: 48_000);

    Assert.Throws<ArgumentException>(() =>
      AacEncoder.Encode(pcm, new AacEncoderOptions(48_000, Channels: 1, PadFinalFrame: false)));
  }

  [Test]
  public void Encode_IsDeterministicAndSignalDependent() {
    const int sampleRate = 44_100;
    var options = new AacEncoderOptions(sampleRate, Channels: 1, Bitrate: 96_000);
    var first = Signal(AacEncoder.FrameSamples, 1, sampleRate, fundamentalHz: 331);
    var second = Signal(AacEncoder.FrameSamples, 1, sampleRate, fundamentalHz: 487);

    var firstA = AacEncoder.Encode(first, options);
    var firstB = AacEncoder.Encode(first, options);
    var secondEncoded = AacEncoder.Encode(second, options);

    Assert.Multiple(() => {
      Assert.That(firstB, Is.EqualTo(firstA));
      Assert.That(secondEncoded, Is.Not.EqualTo(firstA));
    });
  }

  private static short[] Signal(int frames, int channels, int sampleRate, double fundamentalHz = 440) {
    var result = new short[frames * channels];
    for (var frame = 0; frame < frames; ++frame) {
      var t = frame / (double)sampleRate;
      for (var channel = 0; channel < channels; ++channel) {
        var frequency = fundamentalHz * (channel + 1);
        var value = Math.Sin(2 * Math.PI * frequency * t) * 12_000
                    + Math.Sin(2 * Math.PI * 1_337 * t) * 1_700;
        result[frame * channels + channel] = (short)Math.Clamp(
          (int)Math.Round(value), short.MinValue, short.MaxValue);
      }
    }
    return result;
  }
}
