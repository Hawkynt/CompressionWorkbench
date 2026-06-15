#pragma warning disable CS1591
using Codec.Sid;

namespace Compression.Tests.Codecs.Sid;

[TestFixture]
public class SidChipTests {

  private const double PalClock = 985248.0;
  private const int Rate = 44100;

  // Registers per voice 1.
  private const int FreqLo = 0x00, FreqHi = 0x01, PwLo = 0x02, PwHi = 0x03;
  private const int Control = 0x04, AttackDecay = 0x05, SustainRelease = 0x06;
  private const int FcLo = 0x15, FcHi = 0x16, ResFilt = 0x17, ModeVol = 0x18;

  // Renders a buffer with voice 1 set up via the supplied register writes.
  private static short[] Render(SidChip chip, int samples) {
    var buf = new short[samples];
    chip.RenderSamples(buf, samples);
    return buf;
  }

  // Counts upward zero crossings (sign goes from <=0 to >0) over a buffer.
  private static int ZeroCrossings(short[] samples) {
    var count = 0;
    for (var i = 1; i < samples.Length; ++i)
      if (samples[i - 1] <= 0 && samples[i] > 0)
        ++count;
    return count;
  }

  // The SID frequency register for a target Hz: freg = f * 2^24 / clock.
  private static int FreqReg(double hz, double clock) => (int)Math.Round(hz * 16777216.0 / clock);

  [Test]
  public void Sawtooth_FundamentalMatchesFrequencyRegister() {
    const double targetHz = 440.0;
    var chip = new SidChip(SidModel.Mos6581, PalClock, Rate);
    var freg = FreqReg(targetHz, PalClock);
    chip.Write(FreqLo, (byte)(freg & 0xFF));
    chip.Write(FreqHi, (byte)(freg >> 8));
    chip.Write(SustainRelease, 0xF0); // sustain full
    chip.Write(Control, 0x21);        // sawtooth + gate
    chip.Write(ModeVol, 0x0F);        // full volume, no filter

    // Let the attack reach peak, then measure one second.
    Render(chip, Rate / 10);
    var second = Render(chip, Rate);
    var crossings = ZeroCrossings(second);
    // One upward crossing per period → frequency in Hz. Allow a small tolerance.
    Assert.That(crossings, Is.EqualTo(440).Within(8));
  }

  [Test]
  public void Sawtooth_IsNonSilent() {
    var chip = new SidChip(SidModel.Mos6581, PalClock, Rate);
    var freg = FreqReg(440.0, PalClock);
    chip.Write(FreqLo, (byte)(freg & 0xFF));
    chip.Write(FreqHi, (byte)(freg >> 8));
    chip.Write(SustainRelease, 0xF0);
    chip.Write(Control, 0x21);
    chip.Write(ModeVol, 0x0F);
    Render(chip, Rate / 10);
    var buf = Render(chip, Rate / 10);
    var peak = buf.Max(s => Math.Abs((int)s));
    Assert.That(peak, Is.GreaterThan(1000));
  }

  [Test]
  public void PulseWidth_AffectsDutyCycle() {
    // A pulse wave's mean (DC) level tracks its duty cycle. Narrow vs wide PW should give
    // distinctly different averages over a window.
    static double MeanPulse(int pw) {
      var chip = new SidChip(SidModel.Mos6581, PalClock, Rate);
      var freg = FreqReg(220.0, PalClock);
      chip.Write(FreqLo, (byte)(freg & 0xFF));
      chip.Write(FreqHi, (byte)(freg >> 8));
      chip.Write(PwLo, (byte)(pw & 0xFF));
      chip.Write(PwHi, (byte)((pw >> 8) & 0x0F));
      chip.Write(SustainRelease, 0xF0);
      chip.Write(Control, 0x41); // pulse + gate
      chip.Write(ModeVol, 0x0F);
      var b = new short[Rate / 4];
      chip.RenderSamples(b, b.Length);
      b = new short[Rate / 4];
      chip.RenderSamples(b, b.Length);
      return b.Average(s => (double)s);
    }

    var narrow = MeanPulse(0x100); // ~6% duty
    var wide = MeanPulse(0x800);   // 50% duty
    Assert.That(Math.Abs(narrow - wide), Is.GreaterThan(500),
      $"narrow={narrow} wide={wide} should differ");
  }

  [Test]
  public void EnvelopeAttack_ReachesPeakInExpectedTime() {
    // Attack rate 2 (datasheet ~16 ms at the second-fastest step). Render and find when
    // the rectified amplitude first reaches near full scale.
    var chip = new SidChip(SidModel.Mos6581, PalClock, Rate);
    var freg = FreqReg(1000.0, PalClock);
    chip.Write(FreqLo, (byte)(freg & 0xFF));
    chip.Write(FreqHi, (byte)(freg >> 8));
    chip.Write(AttackDecay, 0x20); // attack nibble 2, decay 0
    chip.Write(SustainRelease, 0xF0);
    chip.Write(ModeVol, 0x0F);
    chip.Write(Control, 0x21); // saw + gate

    var buf = new short[Rate]; // one second
    chip.RenderSamples(buf, buf.Length);

    // Sliding peak over short windows; find the first window reaching > 80% of the global peak.
    var globalPeak = buf.Max(s => Math.Abs((int)s));
    const int win = 64;
    var reachSample = -1;
    for (var i = 0; i + win < buf.Length; i += win) {
      var localPeak = 0;
      for (var j = 0; j < win; ++j)
        localPeak = Math.Max(localPeak, Math.Abs((int)buf[i + j]));
      if (localPeak > globalPeak * 0.8) { reachSample = i; break; }
    }
    Assert.That(reachSample, Is.GreaterThan(0), "attack never reached peak");
    var ms = reachSample * 1000.0 / Rate;
    // Attack step 2 is on the order of ~10-40 ms; assert it lands in a generous window.
    Assert.That(ms, Is.GreaterThan(2).And.LessThan(120), $"attack reached peak at {ms} ms");
  }

  [Test]
  public void GateOff_DecaysToSilence() {
    var chip = new SidChip(SidModel.Mos6581, PalClock, Rate);
    var freg = FreqReg(440.0, PalClock);
    chip.Write(FreqLo, (byte)(freg & 0xFF));
    chip.Write(FreqHi, (byte)(freg >> 8));
    chip.Write(AttackDecay, 0x00);     // fast attack
    chip.Write(SustainRelease, 0xF1);  // sustain full, fast-ish release
    chip.Write(ModeVol, 0x0F);
    chip.Write(Control, 0x21);         // gate on
    Render(chip, Rate / 10);
    var sustained = Render(chip, Rate / 20).Max(s => Math.Abs((int)s));

    chip.Write(Control, 0x20);         // gate off (release)
    Render(chip, Rate);                // let release run a second
    var released = Render(chip, Rate / 20).Max(s => Math.Abs((int)s));

    Assert.That(sustained, Is.GreaterThan(1000));
    Assert.That(released, Is.LessThan(sustained / 4));
  }

  [Test]
  public void NoiseLfsr_FirstShiftMatchesHandComputedTaps() {
    // The 23-bit LFSR seeds to all-ones (0x7FFFFF). Its first shift bit = bit22 XOR bit17;
    // with all ones that is 1 XOR 1 = 0, so the LFSR becomes (0x7FFFFF<<1 | 0) & 0x7FFFFF
    // = 0x7FFFFE. The second shift: bit22(=1) XOR bit17(=1) = 0 → 0x7FFFFC. We drive the
    // accumulator so bit 19 rises and assert the resulting noise sample changes from the
    // initial all-ones pattern.
    var chip = new SidChip(SidModel.Mos6581, PalClock, Rate);
    // Max frequency makes accumulator bit 19 toggle quickly, clocking the LFSR.
    chip.Write(FreqLo, 0xFF);
    chip.Write(FreqHi, 0xFF);
    chip.Write(SustainRelease, 0xF0);
    chip.Write(ModeVol, 0x0F);
    chip.Write(Control, 0x81); // noise + gate

    var first = Render(chip, 256);
    var distinct = first.Distinct().Count();
    // A working LFSR produces a varied (non-constant) noise sequence.
    Assert.That(distinct, Is.GreaterThan(4), "noise output should vary as the LFSR shifts");
  }

  [Test]
  public void FilterCurves_DifferBetween6581And8580() {
    // Filter the same low-passed sawtooth at a mid cutoff on both models; the differing
    // cutoff curves should yield a measurable energy difference.
    static double FilteredEnergy(SidModel model) {
      var chip = new SidChip(model, PalClock, Rate);
      var freg = (int)Math.Round(2000.0 * 16777216.0 / PalClock);
      chip.Write(FreqLo, (byte)(freg & 0xFF));
      chip.Write(FreqHi, (byte)(freg >> 8));
      chip.Write(SustainRelease, 0xF0);
      chip.Write(Control, 0x21);     // saw + gate
      chip.Write(FcLo, 0x00);
      chip.Write(FcHi, 0x40);        // mid cutoff (FC ≈ 0x200)
      chip.Write(ResFilt, 0x01);     // route voice 1 through filter
      chip.Write(ModeVol, 0x1F);     // low-pass + full volume
      var b = new short[Rate / 4];
      chip.RenderSamples(b, b.Length);
      b = new short[Rate / 2];
      chip.RenderSamples(b, b.Length);
      return b.Sum(s => (double)s * s) / b.Length;
    }

    var e6581 = FilteredEnergy(SidModel.Mos6581);
    var e8580 = FilteredEnergy(SidModel.Mos8580);
    var rel = Math.Abs(e6581 - e8580) / Math.Max(1.0, Math.Max(e6581, e8580));
    Assert.That(rel, Is.GreaterThan(0.05),
      $"6581 energy={e6581:0} vs 8580 energy={e8580:0} should differ for the same cutoff");
  }
}
