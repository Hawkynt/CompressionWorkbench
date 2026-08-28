using Codec.AmrNb;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class AmrNbEncoderTests {

  [TestCase(AmrNbMode.Mr475)]
  [TestCase(AmrNbMode.Mr515)]
  [TestCase(AmrNbMode.Mr59)]
  [TestCase(AmrNbMode.Mr67)]
  [TestCase(AmrNbMode.Mr74)]
  [TestCase(AmrNbMode.Mr795)]
  [TestCase(AmrNbMode.Mr102)]
  [TestCase(AmrNbMode.Mr122)]
  public void Encode_AllSpeechModes_ProducesDecodableIf1Frames(AmrNbMode mode) {
    var pcm = Signal(AmrNbCodec.SamplesPerFrame * 3);

    var encoded = AmrNbCodec.Encode(pcm, new AmrNbEncoderOptions(mode));
    var frames = AmrNbCodec.ReadInfo(encoded);
    var decoded = AmrNbCodec.Decode(encoded);

    Assert.Multiple(() => {
      Assert.That(frames, Has.Count.EqualTo(3));
      Assert.That(frames.All(frame => frame.Mode == mode), Is.True);
      Assert.That(decoded, Has.Length.EqualTo(3 * AmrNbCodec.SamplesPerFrame));
      Assert.That(decoded.Any(sample => sample != 0), Is.True);
    });
  }

  [Test]
  public void Encode_PadsFinalFrame() {
    var pcm = Signal(AmrNbCodec.SamplesPerFrame + 17);
    var encoded = AmrNbCodec.Encode(pcm, new AmrNbEncoderOptions(AmrNbMode.Mr122));

    Assert.That(AmrNbCodec.CountFrames(encoded), Is.EqualTo(2));
    Assert.That(AmrNbCodec.Decode(encoded), Has.Length.EqualTo(2 * AmrNbCodec.SamplesPerFrame));
  }

  [Test]
  public void Encode_RejectsPartialFrameWhenPaddingDisabled() {
    var pcm = Signal(AmrNbCodec.SamplesPerFrame + 1);
    Assert.Throws<ArgumentException>(() =>
      AmrNbCodec.Encode(pcm, new AmrNbEncoderOptions(AmrNbMode.Mr122, PadFinalFrame: false)));
  }

  [Test]
  public void Encode_DtxSilence_UsesNoDataFrames() {
    var pcm = new short[AmrNbCodec.SamplesPerFrame * 2];
    var encoded = AmrNbCodec.Encode(pcm, new AmrNbEncoderOptions(AmrNbMode.Mr122, EnableDtx: true));
    var frames = AmrNbCodec.ReadInfo(encoded);

    Assert.Multiple(() => {
      Assert.That(frames, Has.Count.EqualTo(2));
      Assert.That(frames.All(frame => frame.Mode == AmrNbMode.NoData), Is.True);
      Assert.That(AmrNbCodec.Decode(encoded).All(sample => sample == 0), Is.True);
    });
  }

  private static short[] Signal(int samples) {
    var result = new short[samples];
    for (var i = 0; i < result.Length; ++i) {
      var t = i / (double)AmrNbCodec.SampleRate;
      var value = Math.Sin(2 * Math.PI * 220 * t) * 9000
                  + Math.Sin(2 * Math.PI * 730 * t) * 3500
                  + ((i % 31) - 15) * 35;
      result[i] = (short)Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue);
    }
    return result;
  }
}
