#pragma warning disable CS1591
using Codec.ImaAdpcm;
using Codec.MsAdpcm;

namespace Compression.Tests.Audio;

[TestFixture]
public class AdpcmEncoderTests {

  [Test]
  public void ImaAdpcm_EncodeMono_ProducesDecodableWaveBlock() {
    const int blockAlign = 256;
    const int samples = 505;
    var pcm = BuildSignal(samples, 1);

    var encoded = ImaAdpcmCodec.Encode(pcm, channels: 1, blockAlign);
    var decoded = ImaAdpcmCodec.Decode(encoded, blockAlign, channels: 1);

    Assert.That(encoded.Length, Is.EqualTo(blockAlign));
    Assert.That(decoded, Has.Length.EqualTo(1));
    Assert.That(decoded[0], Has.Length.EqualTo(samples));
    Assert.That(decoded[0][0], Is.EqualTo(pcm[0]));
    Assert.That(MeanAbsoluteError(pcm, decoded[0]), Is.LessThan(3000));
  }

  [Test]
  public void ImaAdpcm_EncodeStereo_UsesMicrosoftFourByteInterleave() {
    const int blockAlign = 512;
    const int samplesPerChannel = 505;
    var pcm = BuildSignal(samplesPerChannel, 2);

    var encoded = ImaAdpcmCodec.Encode(pcm, channels: 2, blockAlign);
    var decoded = ImaAdpcmCodec.Decode(encoded, blockAlign, channels: 2);

    Assert.That(encoded.Length, Is.EqualTo(blockAlign));
    Assert.That(decoded[0], Has.Length.EqualTo(samplesPerChannel));
    Assert.That(decoded[1], Has.Length.EqualTo(samplesPerChannel));
    Assert.That(decoded[0][0], Is.EqualTo(pcm[0]));
    Assert.That(decoded[1][0], Is.EqualTo(pcm[1]));
    Assert.That(MeanAbsoluteError(pcm, decoded[0], channel: 0, channels: 2), Is.LessThan(3000));
    Assert.That(MeanAbsoluteError(pcm, decoded[1], channel: 1, channels: 2), Is.LessThan(3000));
  }

  [Test]
  public void ImaAdpcm_EncodePadsFinalWaveBlock() {
    var pcm = BuildSignal(73, 1);

    var encoded = ImaAdpcmCodec.Encode(pcm, channels: 1, blockAlign: 256);
    var decoded = ImaAdpcmCodec.Decode(encoded, blockAlign: 256, channels: 1);

    Assert.That(encoded, Has.Length.EqualTo(256));
    Assert.That(decoded[0], Has.Length.EqualTo(505));
    Assert.That(MeanAbsoluteError(pcm, decoded[0].AsSpan(0, pcm.Length)), Is.LessThan(3000));
  }

  [Test]
  public void ImaAdpcm_EncodeQuickTime_RoundTripsPacketsAcrossChannels() {
    const int samplesPerChannel = 64;
    var pcm = BuildSignal(samplesPerChannel, 2);

    var encoded = ImaAdpcmCodec.EncodeQuickTime(pcm, channels: 2);
    var decoded = ImaAdpcmCodec.DecodeQuickTime(encoded, channels: 2);

    Assert.That(encoded, Has.Length.EqualTo(68));
    Assert.That(decoded[0], Has.Length.EqualTo(samplesPerChannel));
    Assert.That(decoded[1], Has.Length.EqualTo(samplesPerChannel));
    Assert.That(MeanAbsoluteError(pcm, decoded[0], channel: 0, channels: 2), Is.LessThan(4000));
    Assert.That(MeanAbsoluteError(pcm, decoded[1], channel: 1, channels: 2), Is.LessThan(4000));
  }

  [Test]
  public void ImaAdpcm_EncodeRejectsInvalidStereoGroupLayout() {
    var pcm = new short[32];
    Assert.Throws<ArgumentException>(() => ImaAdpcmCodec.Encode(pcm, channels: 2, blockAlign: 510));
  }

  [Test]
  public void MsAdpcm_EncodeMono_ProducesDecodableBlock() {
    const int blockAlign = 256;
    const int samples = 500;
    var pcm = BuildSignal(samples, 1);

    var encoded = MsAdpcmCodec.Encode(pcm, channels: 1, blockAlign);
    var decoded = MsAdpcmCodec.Decode(encoded, blockAlign, channels: 1);

    Assert.That(encoded, Has.Length.EqualTo(blockAlign));
    Assert.That(decoded, Has.Length.EqualTo(1));
    Assert.That(decoded[0], Has.Length.EqualTo(samples));
    Assert.That(decoded[0][0], Is.EqualTo(pcm[0]));
    Assert.That(decoded[0][1], Is.EqualTo(pcm[1]));
    Assert.That(MeanAbsoluteError(pcm, decoded[0]), Is.LessThan(3000));
  }

  [Test]
  public void MsAdpcm_EncodeStereo_RoundTripsBothChannels() {
    const int blockAlign = 512;
    const int samplesPerChannel = 500;
    var pcm = BuildSignal(samplesPerChannel, 2);

    var encoded = MsAdpcmCodec.Encode(pcm, channels: 2, blockAlign);
    var decoded = MsAdpcmCodec.Decode(encoded, blockAlign, channels: 2);

    Assert.That(encoded, Has.Length.EqualTo(blockAlign));
    Assert.That(decoded[0], Has.Length.EqualTo(samplesPerChannel));
    Assert.That(decoded[1], Has.Length.EqualTo(samplesPerChannel));
    Assert.That(decoded[0][0], Is.EqualTo(pcm[0]));
    Assert.That(decoded[1][0], Is.EqualTo(pcm[1]));
    Assert.That(decoded[0][1], Is.EqualTo(pcm[2]));
    Assert.That(decoded[1][1], Is.EqualTo(pcm[3]));
    Assert.That(MeanAbsoluteError(pcm, decoded[0], channel: 0, channels: 2), Is.LessThan(3000));
    Assert.That(MeanAbsoluteError(pcm, decoded[1], channel: 1, channels: 2), Is.LessThan(3000));
  }

  [Test]
  public void MsAdpcm_EncodePadsFinalBlock() {
    var pcm = BuildSignal(91, 1);

    var encoded = MsAdpcmCodec.Encode(pcm, channels: 1, blockAlign: 256);
    var decoded = MsAdpcmCodec.Decode(encoded, blockAlign: 256, channels: 1);

    Assert.That(encoded, Has.Length.EqualTo(256));
    Assert.That(decoded[0], Has.Length.EqualTo(500));
    Assert.That(MeanAbsoluteError(pcm, decoded[0].AsSpan(0, pcm.Length)), Is.LessThan(3000));
  }

  [TestCase(1)]
  [TestCase(2)]
  public void AdpcmEncoders_EmptyInput_ProducesEmptyOutput(int channels) {
    Assert.That(ImaAdpcmCodec.Encode([], channels, channels == 1 ? 256 : 512), Is.Empty);
    Assert.That(ImaAdpcmCodec.EncodeQuickTime([], channels), Is.Empty);
    Assert.That(MsAdpcmCodec.Encode([], channels, channels == 1 ? 256 : 512), Is.Empty);
  }

  private static short[] BuildSignal(int frames, int channels) {
    var result = new short[frames * channels];
    for (var frame = 0; frame < frames; ++frame) {
      for (var channel = 0; channel < channels; ++channel) {
        var phase = (frame * (channel + 3) * 2.0 * Math.PI) / 97.0;
        var slow = Math.Sin(phase) * 14000.0;
        var ramp = ((frame * (channel + 5)) % 2048 - 1024) * 3.0;
        result[frame * channels + channel] = (short)Math.Clamp((int)(slow + ramp), short.MinValue, short.MaxValue);
      }
    }
    return result;
  }

  private static double MeanAbsoluteError(ReadOnlySpan<short> expected, ReadOnlySpan<short> actual) {
    Assert.That(actual.Length, Is.GreaterThanOrEqualTo(expected.Length));
    long total = 0;
    for (var i = 0; i < expected.Length; ++i)
      total += Math.Abs((int)expected[i] - actual[i]);
    return (double)total / expected.Length;
  }

  private static double MeanAbsoluteError(ReadOnlySpan<short> interleaved, ReadOnlySpan<short> actual, int channel, int channels) {
    Assert.That(interleaved.Length % channels, Is.Zero);
    var frames = interleaved.Length / channels;
    Assert.That(actual.Length, Is.GreaterThanOrEqualTo(frames));
    long total = 0;
    for (var frame = 0; frame < frames; ++frame)
      total += Math.Abs((int)interleaved[frame * channels + channel] - actual[frame]);
    return (double)total / frames;
  }
}
