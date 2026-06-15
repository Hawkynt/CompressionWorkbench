#pragma warning disable CS1591
using Codec.TrackerXmIt;

namespace Compression.Tests.Codecs.TrackerXmIt;

/// <summary>Spot-checks the IT resonant low-pass filter coefficients and impulse response.</summary>
[TestFixture]
public class ItFilterTests {

  [Test]
  public void FullyOpenNoResonance_IsPassThrough() {
    var f = new ItFilter();
    f.Set(cutoff: 127, resonance: 0, sampleRate: 44100);
    Assert.That(f.Active, Is.False);
    Assert.That(f.Process(0.5f), Is.EqualTo(0.5f)); // inactive → unchanged
  }

  [Test]
  public void LowCutoff_AttenuatesAndCoefficientsSumToUnityGainAtDc() {
    var f = new ItFilter();
    f.Set(cutoff: 20, resonance: 0, sampleRate: 44100);
    Assert.That(f.Active, Is.True);

    var (b0, b1, b2, a1, a2) = f.Coefficients;
    // A low-pass biquad has unity DC gain: (b0+b1+b2) / (1 + a1 + a2) ≈ 1.
    var dcGain = (b0 + b1 + b2) / (1.0 + a1 + a2);
    Assert.That(dcGain, Is.EqualTo(1.0).Within(1e-6));
  }

  [Test]
  public void LowCutoff_SmoothsAStepAndSettlesToDcValue() {
    var f = new ItFilter();
    f.Set(cutoff: 10, resonance: 0, sampleRate: 44100);
    f.Reset();

    // A step from 0 to 1: the first output sample is heavily attenuated (low-pass), and the
    // output settles toward the unity DC value over time.
    var first = f.Process(1.0f);
    Assert.That(first, Is.LessThan(0.2f), "low-pass strongly attenuates the initial step edge");

    float last = first;
    for (var i = 0; i < 4000; ++i)
      last = f.Process(1.0f);
    Assert.That(last, Is.EqualTo(1.0f).Within(0.05f)); // settles toward DC gain ≈ 1
  }
}
