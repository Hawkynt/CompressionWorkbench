#pragma warning disable CS1591
using Codec.TrackerXmIt;

namespace Compression.Tests.Codecs.TrackerXmIt;

/// <summary>
/// Pins the XM frequency math (XM.TXT linear/Amiga formulas), XM sample delta-decoding, and the
/// envelope interpolation walk.
/// </summary>
[TestFixture]
public class XmPlayerTests {

  [Test]
  public void LinearPeriod_MatchesXmTxtFormula() {
    // XM.TXT: period = 7680 - (note-1)*64 - finetune/2 (note here = absolute note value).
    // C-5 in FT2 is note 49 (relative-note 0 → middle). Verify the closed form directly.
    Assert.That(XmChannel.NoteToPeriod(1, 0, linear: true), Is.EqualTo(7680));
    Assert.That(XmChannel.NoteToPeriod(2, 0, linear: true), Is.EqualTo(7680 - 64));
    Assert.That(XmChannel.NoteToPeriod(1, 16, linear: true), Is.EqualTo(7680 - 8)); // finetune/2
  }

  [Test]
  public void LinearFrequency_AtReferencePeriodIsC5Speed() {
    // freq = 8363 * 2^((4608 - period) / 768). At period 4608 the frequency is exactly 8363 Hz.
    Assert.That(XmChannel.PeriodToFrequency(4608, linear: true), Is.EqualTo(8363.0).Within(1e-6));
  }

  [Test]
  public void LinearFrequency_OneOctaveUpDoublesFrequency() {
    // 12 semitones = 12*64 period units lower → one octave up → double frequency.
    var basePeriod = XmChannel.NoteToPeriod(49, 0, linear: true);
    var octavePeriod = XmChannel.NoteToPeriod(49 + 12, 0, linear: true);
    var f0 = XmChannel.PeriodToFrequency(basePeriod, linear: true);
    var f1 = XmChannel.PeriodToFrequency(octavePeriod, linear: true);
    Assert.That(f1 / f0, Is.EqualTo(2.0).Within(1e-6));
  }

  [Test]
  public void AmigaFrequency_OneOctaveUpDoublesFrequency() {
    var p0 = XmChannel.NoteToPeriod(49, 0, linear: false);
    var p1 = XmChannel.NoteToPeriod(49 + 12, 0, linear: false);
    var f0 = XmChannel.PeriodToFrequency(p0, linear: false);
    var f1 = XmChannel.PeriodToFrequency(p1, linear: false);
    Assert.That(f1 / f0, Is.EqualTo(2.0).Within(0.01));
  }

  [Test]
  public void XmSample_DeltaDecode8Bit_Accumulates() {
    // XM stores 8-bit samples delta-coded: stored 5, -3, 10 → 5, 2, 12 (then <<8 to 16-bit).
    var s = new XmSample();
    s.SetData(new byte[] { 5, unchecked((byte)-3), 10 }, is16: false);
    Assert.That(s.Pcm, Is.EqualTo(new short[] { 5 << 8, 2 << 8, 12 << 8 }));
  }

  [Test]
  public void Envelope_InterpolatesLinearlyBetweenPoints() {
    // A volume envelope rising (0,0) → (4,64) → (8,64): walk tick positions and assert the
    // engine's interpolation hits the segment midpoints exactly.
    var env = new XmEnvelope {
      Enabled = true,
      Points = [(0, 0), (4, 64), (8, 64)],
    };
    Assert.That(XmChannel.InterpolateEnvelopeAt(env, 0), Is.EqualTo(0));
    Assert.That(XmChannel.InterpolateEnvelopeAt(env, 1), Is.EqualTo(16));
    Assert.That(XmChannel.InterpolateEnvelopeAt(env, 2), Is.EqualTo(32));
    Assert.That(XmChannel.InterpolateEnvelopeAt(env, 4), Is.EqualTo(64));
    Assert.That(XmChannel.InterpolateEnvelopeAt(env, 6), Is.EqualTo(64));
    Assert.That(XmChannel.InterpolateEnvelopeAt(env, 100), Is.EqualTo(64)); // past end clamps
  }
}
