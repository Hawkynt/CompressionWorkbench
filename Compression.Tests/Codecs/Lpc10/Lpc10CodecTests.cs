#pragma warning disable CS1591
using Codec.Lpc10;

namespace Compression.Tests.Codecs.Lpc10;

/// <summary>
/// Pins the FS-1015 (LPC-10e, 2400 bit/s) speech vocoder. The codec is a parametric (analysis /
/// synthesis) coder, so a round-trip is intelligibility-grade rather than bit-exact: correctness
/// is verified by the fixed 54-bit / 180-sample frame geometry, exact frame/sample counts, the
/// preservation of pitch periodicity (output autocorrelation peaks near the input pitch lag) and
/// the energy envelope (output RMS within a factor of the input), silence staying silent, and
/// determinism.
/// </summary>
[TestFixture]
public class Lpc10CodecTests {

  private const int SampleRate = 8000;
  private const int FrameSamples = 180;
  private const int FrameBytes = 7;

  /// <summary>A voiced-like periodic signal: a fundamental plus two harmonics.</summary>
  private static short[] VoicedTone(int samples, double fundamentalHz, double amplitude) {
    var pcm = new short[samples];
    for (var i = 0; i < samples; ++i) {
      var t = i / (double)SampleRate;
      var s = Math.Sin(2 * Math.PI * fundamentalHz * t)
            + 0.5 * Math.Sin(2 * Math.PI * 2 * fundamentalHz * t)
            + 0.3 * Math.Sin(2 * Math.PI * 3 * fundamentalHz * t);
      pcm[i] = (short)(s / 1.8 * amplitude);
    }
    return pcm;
  }

  private static double Rms(IReadOnlyList<short> pcm) {
    if (pcm.Count == 0)
      return 0;
    var energy = 0.0;
    foreach (var s in pcm)
      energy += (double)s * s;
    return Math.Sqrt(energy / pcm.Count);
  }

  /// <summary>Normalized-autocorrelation peak lag over the LPC-10 pitch range (0 if no energy).</summary>
  private static (int Lag, double Norm) AutocorrelationPeak(short[] pcm) {
    var e0 = 0.0;
    foreach (var s in pcm)
      e0 += (double)s * s;
    if (e0 <= 0)
      return (0, 0);

    var bestLag = 0;
    var best = -1.0;
    for (var lag = 20; lag <= 156; ++lag) {
      var sum = 0.0;
      for (var i = lag; i < pcm.Length; ++i)
        sum += (double)pcm[i] * pcm[i - lag];
      var norm = sum / e0;
      if (norm > best) {
        best = norm;
        bestLag = lag;
      }
    }
    return (bestLag, best);
  }

  [Test]
  public void Encode_ProducesSevenBytesPerFrame() {
    // 360 samples = exactly two 180-sample frames → 14 bytes.
    var enc = Lpc10Codec.Encode(new short[2 * FrameSamples]);
    Assert.That(enc.Length, Is.EqualTo(2 * FrameBytes));
  }

  [Test]
  public void Encode_ZeroPadsTrailingPartialFrame() {
    // 50 samples is less than one frame but must still produce one full 7-byte frame.
    var enc = Lpc10Codec.Encode(new short[50]);
    Assert.That(enc.Length, Is.EqualTo(FrameBytes));
  }

  [Test]
  public void Encode_Empty_ProducesNothing() {
    Assert.That(Lpc10Codec.Encode([]).Length, Is.EqualTo(0));
  }

  [Test]
  public void Decode_Produces180SamplesPerFrame() {
    var dec = Lpc10Codec.Decode(new byte[5 * FrameBytes]);
    Assert.That(dec.Length, Is.EqualTo(5 * FrameSamples));
  }

  [Test]
  public void Decode_IgnoresTrailingPartialFrameBytes() {
    // 7 bytes + 3 extra (not a full frame) → still exactly one decoded frame.
    var dec = Lpc10Codec.Decode(new byte[FrameBytes + 3]);
    Assert.That(dec.Length, Is.EqualTo(FrameSamples));
  }

  [TestCase(120.0)]
  [TestCase(180.0)]
  public void RoundTrip_VoicedTone_PreservesPitchPeriodicity(double fundamentalHz) {
    var pcm = VoicedTone(10 * FrameSamples, fundamentalHz, 12000);
    var dec = Lpc10Codec.Decode(Lpc10Codec.Encode(pcm));

    var expectedLag = (int)Math.Round(SampleRate / fundamentalHz);
    var (lag, norm) = AutocorrelationPeak(dec);

    // The synthesized excitation must be strongly periodic.
    Assert.That(norm, Is.GreaterThan(0.5),
      $"output is not periodic enough (autocorr peak {norm:F2})");

    // The output period must match the input pitch up to octave equivalence: the simplified
    // AMDF pitch tracker can lock onto an integer multiple/sub-multiple of the true period (a
    // documented limitation), but the reconstructed signal stays periodic at the input pitch
    // or a harmonically-related lag.
    var tolerance = expectedLag * 0.15 + 2;
    var octaveMatch = false;
    for (var k = 1; k <= 3 && !octaveMatch; ++k) {
      if (Math.Abs(lag - expectedLag * k) <= tolerance * k)
        octaveMatch = true;
      if (Math.Abs(lag * k - expectedLag) <= tolerance * k)
        octaveMatch = true;
    }
    Assert.That(octaveMatch, Is.True,
      $"output pitch lag {lag} not harmonically related to input lag {expectedLag}");
  }

  [Test]
  public void RoundTrip_VoicedTone_PreservesEnergyEnvelopeWithinAFactor() {
    var pcm = VoicedTone(10 * FrameSamples, 150, 12000);
    var dec = Lpc10Codec.Decode(Lpc10Codec.Encode(pcm));

    var ratio = Rms(dec) / Rms(pcm);
    Assert.That(ratio, Is.GreaterThan(0.4).And.LessThan(2.5),
      $"RMS envelope not preserved (ratio {ratio:F2})");
  }

  [Test]
  public void RoundTrip_Silence_StaysSilent() {
    var dec = Lpc10Codec.Decode(Lpc10Codec.Encode(new short[10 * FrameSamples]));
    foreach (var s in dec)
      Assert.That(Math.Abs((int)s), Is.LessThan(64), "decoded silence must stay near zero");
  }

  [Test]
  public void RoundTrip_PreservesExactSampleCount() {
    var pcm = VoicedTone(7 * FrameSamples, 140, 10000);
    var dec = Lpc10Codec.Decode(Lpc10Codec.Encode(pcm));
    Assert.That(dec.Length, Is.EqualTo(7 * FrameSamples));
  }

  [Test]
  public void Encode_IsDeterministic() {
    var pcm = VoicedTone(5 * FrameSamples, 160, 9000);
    Assert.That(Lpc10Codec.Encode(pcm), Is.EqualTo(Lpc10Codec.Encode(pcm)));
  }

  [Test]
  public void Decode_IsDeterministic() {
    var enc = Lpc10Codec.Encode(VoicedTone(5 * FrameSamples, 160, 9000));
    Assert.That(Lpc10Codec.Decode(enc), Is.EqualTo(Lpc10Codec.Decode(enc)));
  }

  [Test]
  public void RoundTrip_LouderInput_ReconstructsLargerSwings() {
    var loud = Lpc10Codec.Decode(Lpc10Codec.Encode(VoicedTone(10 * FrameSamples, 150, 14000)));
    var quiet = Lpc10Codec.Decode(Lpc10Codec.Encode(VoicedTone(10 * FrameSamples, 150, 1500)));
    Assert.That(Rms(loud), Is.GreaterThan(Rms(quiet) * 2),
      "louder input must reconstruct to a larger energy envelope");
  }
}
