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

  [TestCase(AmrNbMode.Mr475)]
  [TestCase(AmrNbMode.Mr515)]
  [TestCase(AmrNbMode.Mr59)]
  [TestCase(AmrNbMode.Mr67)]
  [TestCase(AmrNbMode.Mr74)]
  [TestCase(AmrNbMode.Mr795)]
  [TestCase(AmrNbMode.Mr102)]
  [TestCase(AmrNbMode.Mr122)]
  public void Encode_AllSpeechModes_PreserveInputStructure(AmrNbMode mode) {
    var pcm = Signal(AmrNbCodec.SamplesPerFrame * 6);
    var encoded = AmrNbCodec.Encode(pcm, new AmrNbEncoderOptions(mode));
    var decoded = AmrNbCodec.Decode(encoded);

    // ACELP synthesis/post-filtering has state and delay, so compare after the first frame and
    // search a bounded alignment window instead of demanding sample-aligned PCM equality.
    var correlation = BestNormalizedCorrelation(
      pcm,
      decoded,
      skipSamples: AmrNbCodec.SamplesPerFrame,
      maximumLag: AmrNbCodec.SamplesPerFrame / 2);

    Assert.That(correlation, Is.GreaterThan(0.05),
      $"{mode} output no longer preserves measurable structure from the source signal");
  }

  [Test]
  public void Encode_IsDeterministicAndSignalDependent() {
    var first = Signal(AmrNbCodec.SamplesPerFrame * 4, fundamentalHz: 220);
    var second = Signal(AmrNbCodec.SamplesPerFrame * 4, fundamentalHz: 347);
    var options = new AmrNbEncoderOptions(AmrNbMode.Mr122);

    var firstA = AmrNbCodec.Encode(first, options);
    var firstB = AmrNbCodec.Encode(first, options);
    var secondEncoded = AmrNbCodec.Encode(second, options);

    Assert.Multiple(() => {
      Assert.That(firstB, Is.EqualTo(firstA), "encoder must be deterministic for identical PCM and options");
      Assert.That(secondEncoded, Is.Not.EqualTo(firstA), "analysis must react to the input signal");
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

  private static short[] Signal(int samples, double fundamentalHz = 220) {
    var result = new short[samples];
    for (var i = 0; i < result.Length; ++i) {
      var t = i / (double)AmrNbCodec.SampleRate;
      var value = Math.Sin(2 * Math.PI * fundamentalHz * t) * 9000
                  + Math.Sin(2 * Math.PI * 730 * t) * 3500
                  + ((i % 31) - 15) * 35;
      result[i] = (short)Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue);
    }
    return result;
  }

  private static double BestNormalizedCorrelation(
    ReadOnlySpan<short> source,
    ReadOnlySpan<short> decoded,
    int skipSamples,
    int maximumLag) {
    var best = 0d;
    for (var lag = -maximumLag; lag <= maximumLag; ++lag) {
      double cross = 0;
      double sourceEnergy = 0;
      double decodedEnergy = 0;
      var start = Math.Max(skipSamples, -lag);
      var end = Math.Min(source.Length, decoded.Length - lag);
      for (var i = start; i < end; ++i) {
        var x = (double)source[i];
        var y = decoded[i + lag];
        cross += x * y;
        sourceEnergy += x * x;
        decodedEnergy += y * y;
      }
      if (sourceEnergy <= 0 || decodedEnergy <= 0)
        continue;
      best = Math.Max(best, Math.Abs(cross) / Math.Sqrt(sourceEnergy * decodedEnergy));
    }
    return best;
  }
}
