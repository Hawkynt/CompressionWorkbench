using Codec.AmrWb;

namespace Compression.Tests.Audio;

[TestFixture]
public class AmrWbEncoderTests {

  [TestCase(AmrWbMode.Mr660)]
  [TestCase(AmrWbMode.Mr885)]
  [TestCase(AmrWbMode.Mr1265)]
  [TestCase(AmrWbMode.Mr1425)]
  [TestCase(AmrWbMode.Mr1585)]
  [TestCase(AmrWbMode.Mr1825)]
  [TestCase(AmrWbMode.Mr1985)]
  [TestCase(AmrWbMode.Mr2305)]
  [TestCase(AmrWbMode.Mr2385)]
  public void Encode_AllSpeechModes_ProduceStorageFrames(AmrWbMode mode) {
    var pcm = BuildSignal(AmrWbCodec.SamplesPerFrame * 3);
    var encoded = AmrWbCodec.Encode(pcm, new AmrWbEncoderOptions(mode));
    var info = AmrWbCodec.ReadInfo(encoded);

    Assert.Multiple(() => {
      Assert.That(info, Has.Count.EqualTo(3));
      Assert.That(info.All(frame => frame.Mode == mode), Is.True);
      Assert.That(info.All(frame => frame.SizeBytes == AmrWbCodec.FrameBytes((int)mode)), Is.True);
      Assert.That(encoded.Length, Is.EqualTo(3 * AmrWbCodec.FrameBytes((int)mode)));
    });
  }

  [TestCase(AmrWbMode.Mr660)]
  [TestCase(AmrWbMode.Mr1265)]
  [TestCase(AmrWbMode.Mr2385)]
  public void Encode_DecodesThroughInTreeDecoder(AmrWbMode mode) {
    var pcm = BuildSignal(AmrWbCodec.SamplesPerFrame * 6);
    var encoded = AmrWbCodec.Encode(pcm, new AmrWbEncoderOptions(mode));
    var decoded = AmrWbCodec.Decode(encoded);

    Assert.Multiple(() => {
      Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
      Assert.That(decoded.Any(static sample => sample != 0), Is.True);
      Assert.That(Rms(decoded), Is.GreaterThan(100));
    });
  }

  [Test]
  public void Encode_PadsFinal20MsFrame() {
    var pcm = BuildSignal(AmrWbCodec.SamplesPerFrame + 31);
    var encoded = AmrWbCodec.Encode(pcm, new AmrWbEncoderOptions(AmrWbMode.Mr1265));
    Assert.That(AmrWbCodec.CountFrames(encoded), Is.EqualTo(2));
  }

  [Test]
  public void Encode_CanRejectPartialFrame() {
    var pcm = new short[AmrWbCodec.SamplesPerFrame + 1];
    Assert.Throws<ArgumentException>(() =>
      AmrWbCodec.Encode(pcm, new AmrWbEncoderOptions(PadFinalFrame: false)));
  }

  [Test]
  public void Encode_Dtx_ProducesSidOrNoDataDuringSustainedSilence() {
    var silence = new short[AmrWbCodec.SamplesPerFrame * 30];
    var encoded = AmrWbCodec.Encode(silence, new AmrWbEncoderOptions(
      AmrWbMode.Mr1265,
      EnableDtx: true));
    var info = AmrWbCodec.ReadInfo(encoded);

    Assert.Multiple(() => {
      Assert.That(info, Has.Count.EqualTo(30));
      Assert.That(info.Any(frame => frame.Mode is AmrWbMode.Sid or AmrWbMode.NoData), Is.True);
    });
  }

  [TestCase(AmrWbMode.Sid)]
  [TestCase(AmrWbMode.SpeechLost)]
  [TestCase(AmrWbMode.NoData)]
  public void Encode_RejectsNonSpeechMode(AmrWbMode mode) {
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      AmrWbCodec.Encode(new short[AmrWbCodec.SamplesPerFrame], new AmrWbEncoderOptions(mode)));
  }

  private static short[] BuildSignal(int samples) {
    var result = new short[samples];
    for (var i = 0; i < result.Length; ++i) {
      var fundamental = Math.Sin(2 * Math.PI * 180 * i / AmrWbCodec.SampleRate) * 9500;
      var harmonic = Math.Sin(2 * Math.PI * 540 * i / AmrWbCodec.SampleRate) * 2800;
      result[i] = (short)(fundamental + harmonic);
    }
    return result;
  }

  private static double Rms(ReadOnlySpan<short> samples) {
    double sum = 0;
    foreach (var sample in samples)
      sum += sample * (double)sample;
    return Math.Sqrt(sum / samples.Length);
  }
}
