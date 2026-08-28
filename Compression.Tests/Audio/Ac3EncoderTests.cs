using Codec.Ac3;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class Ac3EncoderTests {

  [TestCase(1, false, 96000, 48000)]
  [TestCase(2, false, 192000, 48000)]
  [TestCase(7, true, 448000, 48000)]
  [TestCase(2, false, 192000, 32000)]
  public void Encode_ProducesParseableDecodableLegacyFrames(int acmod, bool lfe, int bitrate, int sampleRate) {
    var channels = Ac3FrameHeader.AcmodChannelCount(acmod) + (lfe ? 1 : 0);
    var pcm = Signal(1536 * 2, channels, sampleRate);

    var encoded = Ac3Codec.Encode(pcm, new Ac3EncoderOptions(sampleRate, bitrate, acmod, lfe));
    using var infoInput = new MemoryStream(encoded);
    var info = Ac3Codec.ReadStreamInfo(infoInput);
    using var input = new MemoryStream(encoded);
    using var output = new MemoryStream();
    Ac3Codec.Decompress(input, output);

    Assert.Multiple(() => {
      Assert.That(info.IsEnhanced, Is.False);
      Assert.That(info.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(info.Channels, Is.EqualTo(channels));
      Assert.That(info.Bitrate, Is.EqualTo(bitrate));
      Assert.That(info.Acmod, Is.EqualTo(acmod));
      Assert.That(info.Lfe, Is.EqualTo(lfe));
      Assert.That(info.DurationSamples, Is.EqualTo(3072));
      Assert.That(output.Length, Is.EqualTo(3072L * channels * sizeof(short)));
      Assert.That(output.ToArray().Any(value => value != 0), Is.True);
    });
  }

  [Test]
  public void Encode_44100_AlternatesLegalFrameSizes() {
    const int sampleRate = 44100;
    const int bitrate = 192000;
    var pcm = Signal(1536 * 8, 2, sampleRate);
    var encoded = Ac3Codec.Encode(pcm, new Ac3EncoderOptions(sampleRate, bitrate));

    var position = 0;
    var sizes = new HashSet<int>();
    var frames = 0;
    while (position + 6 <= encoded.Length) {
      var header = Ac3FrameHeader.TryParse(encoded, position);
      Assert.That(header, Is.Not.Null);
      sizes.Add(header!.Value.FrameSize);
      position += header.Value.FrameSize;
      ++frames;
    }

    Assert.Multiple(() => {
      Assert.That(frames, Is.EqualTo(8));
      Assert.That(position, Is.EqualTo(encoded.Length));
      Assert.That(sizes.Count, Is.EqualTo(2), "44.1-kHz AC-3 uses the adjacent legal frame sizes to hit the exact average bitrate.");
    });
  }

  [Test]
  public void Encode_PadsFinalFrame() {
    var pcm = Signal(1537, 2, 48000);
    var encoded = Ac3Codec.Encode(pcm);
    using var input = new MemoryStream(encoded);
    var info = Ac3Codec.ReadStreamInfo(input);
    Assert.That(info.DurationSamples, Is.EqualTo(3072));
  }

  [Test]
  public void Encode_RejectsPartialFrameWhenPaddingDisabled() {
    var pcm = Signal(1537, 2, 48000);
    Assert.Throws<ArgumentException>(() =>
      Ac3Codec.Encode(pcm, new Ac3EncoderOptions(PadFinalFrame: false)));
  }

  [Test]
  public void Encode_RejectsNonStandardBitrate() {
    var pcm = Signal(1536, 2, 48000);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      Ac3Codec.Encode(pcm, new Ac3EncoderOptions(Bitrate: 200000)));
  }

  private static short[] Signal(int samplesPerChannel, int channels, int sampleRate) {
    var result = new short[samplesPerChannel * channels];
    for (var n = 0; n < samplesPerChannel; ++n) {
      for (var ch = 0; ch < channels; ++ch) {
        var frequency = 170 + ch * 113;
        var t = n / (double)sampleRate;
        var sample = Math.Sin(2 * Math.PI * frequency * t) * 6500
                   + Math.Sin(2 * Math.PI * (frequency * 2.7) * t) * 1800;
        result[n * channels + ch] = (short)Math.Round(sample);
      }
    }
    return result;
  }
}
