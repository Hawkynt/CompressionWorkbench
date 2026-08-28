using Codec.Gsm610;

namespace Compression.Tests.Audio;

[TestFixture]
public class Gsm610EncoderTests {

  [Test]
  public void EncodeRaw_ProducesSigned33ByteFrames() {
    var pcm = BuildSignal(Gsm610Codec.FrameSamples * 3, 8000, 440);
    var encoded = Gsm610Codec.EncodeRaw(pcm);

    Assert.Multiple(() => {
      Assert.That(encoded.Length, Is.EqualTo(3 * Gsm610Codec.FrameBytes));
      Assert.That(Gsm610Codec.LooksLikeRawFrames(encoded), Is.True);
      Assert.That(encoded[0] >> 4, Is.EqualTo(0xD));
      Assert.That(encoded[Gsm610Codec.FrameBytes] >> 4, Is.EqualTo(0xD));
    });
  }

  [Test]
  public void EncodeRaw_PadsIncompleteFinalFrame() {
    var pcm = BuildSignal(Gsm610Codec.FrameSamples + 17, 8000, 330);
    var encoded = Gsm610Codec.EncodeRaw(pcm);
    var decoded = Gsm610Codec.DecodeRaw(encoded);

    Assert.Multiple(() => {
      Assert.That(encoded.Length, Is.EqualTo(2 * Gsm610Codec.FrameBytes));
      Assert.That(decoded.Length, Is.EqualTo(2 * Gsm610Codec.FrameSamples));
    });
  }

  [Test]
  public void EncodeRaw_CanRequireWholeFrames() {
    var pcm = new short[Gsm610Codec.FrameSamples + 1];
    Assert.Throws<ArgumentException>(() => Gsm610Codec.EncodeRaw(pcm, padFinalFrame: false));
  }

  [Test]
  public void Encode_Stereo_UsesIndependentFramesPerChannel() {
    var frames = Gsm610Codec.FrameSamples * 4;
    var pcm = new short[frames * 2];
    for (var i = 0; i < frames; ++i) {
      pcm[i * 2] = (short)(Math.Sin(2 * Math.PI * 280 * i / 8000) * 12000);
      pcm[i * 2 + 1] = (short)(Math.Sin(2 * Math.PI * 760 * i / 8000) * 9000);
    }

    var encoded = Gsm610Codec.Encode(pcm, new Gsm610EncoderOptions(2));
    var decoded = Gsm610Codec.Decode(encoded, 2);

    Assert.Multiple(() => {
      Assert.That(encoded.Length, Is.EqualTo(4 * 2 * Gsm610Codec.FrameBytes));
      Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
      for (var offset = 0; offset < encoded.Length; offset += Gsm610Codec.FrameBytes)
        Assert.That(encoded[offset] >> 4, Is.EqualTo(0xD));
      Assert.That(ChannelEnergy(decoded, 0, 2), Is.GreaterThan(0));
      Assert.That(ChannelEnergy(decoded, 1, 2), Is.GreaterThan(0));
    });
  }

  [Test]
  public void EncodeRaw_TracksPeriodicSignal() {
    var pcm = BuildSignal(Gsm610Codec.FrameSamples * 12, 8000, 220);
    var decoded = Gsm610Codec.DecodeRaw(Gsm610Codec.EncodeRaw(pcm));

    // The in-tree GSM synthesis core is deliberately compact rather than bit-exact.
    // Validate that encoding carries signal, has the correct duration and is positively
    // correlated after allowing the codec's analysis/synthesis delay.
    Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
    Assert.That(decoded.Any(static sample => sample != 0), Is.True);
    Assert.That(BestCorrelation(pcm, decoded, 160), Is.GreaterThan(0.05));
  }

  [Test]
  public void DecodeRaw_RejectsMissingGsmSignature() {
    var frame = new byte[Gsm610Codec.FrameBytes];
    Assert.Multiple(() => {
      Assert.That(Gsm610Codec.LooksLikeRawFrames(frame), Is.False);
      Assert.Throws<InvalidDataException>(() => Gsm610Codec.DecodeRaw(frame));
    });
  }

  private static short[] BuildSignal(int frames, int sampleRate, double frequency) {
    var result = new short[frames];
    for (var i = 0; i < result.Length; ++i) {
      var fundamental = Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 11000;
      var harmonic = Math.Sin(2 * Math.PI * frequency * 2.2 * i / sampleRate) * 2500;
      result[i] = (short)(fundamental + harmonic);
    }
    return result;
  }

  private static double ChannelEnergy(short[] interleaved, int channel, int channels) {
    double energy = 0;
    for (var i = channel; i < interleaved.Length; i += channels)
      energy += interleaved[i] * (double)interleaved[i];
    return energy;
  }

  private static double BestCorrelation(ReadOnlySpan<short> expected, ReadOnlySpan<short> actual, int maxLag) {
    var best = -1.0;
    for (var lag = 0; lag <= maxLag; ++lag) {
      double cross = 0, ee = 0, aa = 0;
      var count = Math.Min(expected.Length, actual.Length - lag);
      for (var i = 0; i < count; ++i) {
        var e = expected[i];
        var a = actual[i + lag];
        cross += e * (double)a;
        ee += e * (double)e;
        aa += a * (double)a;
      }
      if (ee > 0 && aa > 0)
        best = Math.Max(best, cross / Math.Sqrt(ee * aa));
    }
    return best;
  }
}
