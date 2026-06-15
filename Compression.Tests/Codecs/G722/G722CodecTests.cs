#pragma warning disable CS1591
using Codec.G722;

namespace Compression.Tests.Codecs.G722;

/// <summary>
/// Pins the ITU-T G.722 64 kbit/s sub-band ADPCM codec. The reference is a
/// backward-adaptive two-band predictor behind a 24-tap QMF, so correctness is verified by
/// encode→decode round-trip fidelity on band-limited input (G.722 is lossy) plus exact
/// byte/sample counts, silence stability and determinism. The QMF introduces a group delay
/// of roughly one filter length, which the fidelity measurement compensates for by aligning
/// the reconstructed signal to its best lag.
/// </summary>
[TestFixture]
public class G722CodecTests {

  private const int SampleRate = 16000;

  private static short[] Sine(int n, double freq, double amp = 10000) {
    var pcm = new short[n];
    for (var i = 0; i < n; ++i)
      pcm[i] = (short)(amp * Math.Sin(2 * Math.PI * freq * i / SampleRate));
    return pcm;
  }

  // Round-trip SNR after compensating for the QMF group delay (best lag in 0..24).
  private static double RoundTripSnr(short[] pcm, int warmup = 100) {
    var dec = G722Codec.Decode(G722Codec.Encode(pcm));
    var n = Math.Min(pcm.Length, dec.Length);

    var bestLag = 0;
    var bestErr = double.MaxValue;
    for (var lag = 0; lag <= 24; ++lag) {
      double err = 0;
      for (var i = warmup; i < n - lag; ++i) {
        double d = pcm[i] - dec[i + lag];
        err += d * d;
      }
      if (err < bestErr) {
        bestErr = err;
        bestLag = lag;
      }
    }

    double signal = 0, noise = 0;
    for (var i = warmup; i < n - bestLag; ++i) {
      double d = pcm[i] - dec[i + bestLag];
      noise += d * d;
      signal += (double)pcm[i] * pcm[i];
    }
    return 10 * Math.Log10(signal / noise);
  }

  [Test]
  public void Encode_ProducesOneBytePerTwoSamples() {
    var enc = G722Codec.Encode(Sine(4000, 500));
    Assert.That(enc.Length, Is.EqualTo(2000));
  }

  [Test]
  public void Encode_OddTrailingSample_IsDropped() {
    var enc = G722Codec.Encode(Sine(4001, 500));
    Assert.That(enc.Length, Is.EqualTo(2000));
  }

  [Test]
  public void Decode_ProducesTwoSamplesPerByte() {
    var dec = G722Codec.Decode(new byte[500]);
    Assert.That(dec.Length, Is.EqualTo(1000));
  }

  [TestCase(300.0, 35.0)]
  [TestCase(1000.0, 35.0)]
  [TestCase(3000.0, 30.0)]
  public void RoundTrip_BandLimitedSine_MeetsSnrThreshold(double freq, double minSnr) {
    var snr = RoundTripSnr(Sine(4000, freq));
    Assert.That(snr, Is.GreaterThan(minSnr), $"G.722 {freq} Hz SNR {snr:F1} dB below {minSnr} dB");
  }

  [Test]
  public void Decode_Silence_StaysNearZero() {
    var dec = G722Codec.Decode(G722Codec.Encode(new short[2000]));
    foreach (var s in dec)
      Assert.That(Math.Abs((int)s), Is.LessThan(64));
  }

  [Test]
  public void RoundTrip_IsDeterministic() {
    var enc = G722Codec.Encode(Sine(2000, 800));
    Assert.That(G722Codec.Decode(enc), Is.EqualTo(G722Codec.Decode(enc)));
    Assert.That(G722Codec.Encode(Sine(2000, 800)), Is.EqualTo(enc));
  }

  [Test]
  public void RoundTrip_PreservesSampleCount() {
    var pcm = Sine(3000, 600);
    var dec = G722Codec.Decode(G722Codec.Encode(pcm));
    Assert.That(dec.Length, Is.EqualTo(pcm.Length));
  }
}
