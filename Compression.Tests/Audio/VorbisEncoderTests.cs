#pragma warning disable CS1591
using Codec.Vorbis;

namespace Compression.Tests.Audio;

[TestFixture]
public class VorbisEncoderTests {

  [TestCase(8000, 1, -0.1f)]
  [TestCase(16000, 1, 0.3f)]
  [TestCase(22050, 2, 0.5f)]
  [TestCase(44100, 2, 0.9f)]
  public void Encode_QualityRateAndChannelMatrix_ProducesDecodableVorbis(int sampleRate, int channels, float quality) {
    var frames = Math.Max(512, sampleRate / 10);
    var pcm = BuildSignal(frames, channels, sampleRate);
    var encoded = VorbisEncoder.Encode(pcm, new VorbisEncoderOptions(sampleRate, channels, quality,
      SerialNumber: 0x12345678, Comments: new Dictionary<string, string> { ["ENCODER"] = "CompressionWorkbench" }));

    using var infoStream = new MemoryStream(encoded, writable: false);
    var info = VorbisCodec.ReadStreamInfo(infoStream);
    Assert.Multiple(() => {
      Assert.That(info.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(info.Channels, Is.EqualTo(channels));
      Assert.That(encoded.AsSpan(0, 4).ToArray(), Is.EqualTo("OggS"u8.ToArray()));
    });

    using var input = new MemoryStream(encoded, writable: false);
    using var output = new MemoryStream();
    VorbisCodec.Decompress(input, output);
    Assert.That(output.Length, Is.GreaterThan(0));
    Assert.That(output.Length % (channels * 2), Is.Zero);
  }

  [Test]
  public void Encode_DeterministicSerial_ProducesDeterministicStream() {
    var pcm = BuildSignal(1000, 2, 44100);
    var options = new VorbisEncoderOptions(44100, 2, 0.5f, SerialNumber: 42);
    var a = VorbisEncoder.Encode(pcm, options);
    var b = VorbisEncoder.Encode(pcm, options);
    Assert.That(b, Is.EqualTo(a));
  }

  [TestCase(-0.11f)]
  [TestCase(1.01f)]
  public void Encode_RejectsUnsupportedQuality(float quality) {
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      VorbisEncoder.Encode(new short[128], new VorbisEncoderOptions(44100, 1, quality)));
  }

  private static short[] BuildSignal(int frames, int channels, int sampleRate) {
    var result = new short[frames * channels];
    for (var frame = 0; frame < frames; ++frame)
      for (var c = 0; c < channels; ++c) {
        var frequency = 330 + c * 220;
        result[frame * channels + c] = (short)(Math.Sin(2 * Math.PI * frequency * frame / sampleRate) * 12000);
      }
    return result;
  }
}
