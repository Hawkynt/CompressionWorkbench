#pragma warning disable CS1591
using Codec.Cvsd;

namespace Compression.Tests.Codecs.Cvsd;

/// <summary>
/// Pins the CVSD (continuously-variable-slope delta modulation) 1-bit voice codec. The
/// modulator is a delta-sigma feedback loop with syllabic step companding, so correctness is
/// verified by tracking a slow sine (correlation between input and reconstruction), silence
/// stability, the 8-samples-per-byte packing invariant, determinism and the amplitude-driven
/// step adaptation (a louder signal reconstructs to larger swings than a quiet one).
/// </summary>
[TestFixture]
public class CvsdCodecTests {

  private const int SampleRate = 64000; // Bluetooth SCO convention.

  private static short[] Sine(int n, double freq, double amp) {
    var pcm = new short[n];
    for (var i = 0; i < n; ++i)
      pcm[i] = (short)(amp * Math.Sin(2 * Math.PI * freq * i / SampleRate));
    return pcm;
  }

  private static double Correlation(short[] a, short[] b, int warmup) {
    var n = Math.Min(a.Length, b.Length);
    var count = n - warmup;
    double meanA = 0, meanB = 0;
    for (var i = warmup; i < n; ++i) {
      meanA += a[i];
      meanB += b[i];
    }
    meanA /= count;
    meanB /= count;
    double sa = 0, sb = 0, sab = 0;
    for (var i = warmup; i < n; ++i) {
      double da = a[i] - meanA, db = b[i] - meanB;
      sa += da * da;
      sb += db * db;
      sab += da * db;
    }
    return sab / Math.Sqrt(sa * sb);
  }

  [Test]
  public void Encode_PacksOneBitPerSample() {
    var enc = CvsdCodec.Encode(Sine(2000, 300, 8000));
    Assert.That(enc.Length, Is.EqualTo(250)); // 2000 / 8
  }

  [Test]
  public void Encode_RoundsUpPartialByte() {
    var enc = CvsdCodec.Encode(Sine(2001, 300, 8000));
    Assert.That(enc.Length, Is.EqualTo(251));
  }

  [Test]
  public void Decode_ProducesEightSamplesPerByte() {
    var dec = CvsdCodec.Decode(new byte[100]);
    Assert.That(dec.Length, Is.EqualTo(800));
  }

  [TestCase(300.0)]
  [TestCase(800.0)]
  public void RoundTrip_SlowSine_TracksClosely(double freq) {
    var pcm = Sine(2000, freq, 8000);
    var dec = CvsdCodec.Decode(CvsdCodec.Encode(pcm));
    var corr = Correlation(pcm, dec, warmup: 200);
    Assert.That(corr, Is.GreaterThan(0.95), $"CVSD {freq} Hz correlation {corr:F3} too low");
  }

  [Test]
  public void Decode_Silence_StaysNearZero() {
    // All-zero bits track the integrator downward and settle near silence at the step floor.
    var dec = CvsdCodec.Decode(CvsdCodec.Encode(new short[800]));
    foreach (var s in dec)
      Assert.That(Math.Abs((int)s), Is.LessThan(64));
  }

  [Test]
  public void RoundTrip_IsDeterministic() {
    var pcm = Sine(1000, 500, 8000);
    var enc = CvsdCodec.Encode(pcm);
    Assert.That(CvsdCodec.Encode(pcm), Is.EqualTo(enc));
    Assert.That(CvsdCodec.Decode(enc), Is.EqualTo(CvsdCodec.Decode(enc)));
  }

  [Test]
  public void StepAdaptation_LouderSignalReconstructsLargerSwings() {
    var loud = CvsdCodec.Decode(CvsdCodec.Encode(Sine(2000, 300, 30000)));
    var quiet = CvsdCodec.Decode(CvsdCodec.Encode(Sine(2000, 300, 2000)));
    var loudPeak = loud.Max(s => Math.Abs((int)s));
    var quietPeak = quiet.Max(s => Math.Abs((int)s));
    Assert.That(loudPeak, Is.GreaterThan(quietPeak * 3),
      $"step companding failed: loud peak {loudPeak} vs quiet peak {quietPeak}");
  }

  [Test]
  public void Encode_MsbFirstAndLsbFirst_DiffferInPacking_ButSameDecodeWithMatchingFlag() {
    var pcm = Sine(800, 400, 8000);
    var msb = CvsdCodec.Encode(pcm);
    var lsb = CvsdCodec.Encode(pcm, msbFirst: false);
    // Same logical bit stream, opposite packing → byte patterns differ but decode matches.
    Assert.That(lsb, Is.Not.EqualTo(msb));
    Assert.That(CvsdCodec.Decode(lsb, msbFirst: false), Is.EqualTo(CvsdCodec.Decode(msb)));
  }
}
