#pragma warning disable CS1591
using Codec.GameBoyApu;

namespace Compression.Tests.Codecs.GbApu;

[TestFixture]
public class GameBoyApuTests {

  private const int Rate = 44100;

  private static GameBoyApu PoweredApu() {
    var apu = new GameBoyApu(Rate);
    apu.Write(0xFF26, 0x80); // NR52 power on
    return apu;
  }

  // Renders the given number of seconds and returns the left and right channels separately.
  private static (short[] Left, short[] Right) Render(GameBoyApu apu, double seconds) {
    var frames = (int)(seconds * Rate);
    var buf = new short[frames * 2];
    apu.RenderSamples(buf, frames);
    var left = new short[frames];
    var right = new short[frames];
    for (var f = 0; f < frames; ++f) { left[f] = buf[f * 2]; right[f] = buf[f * 2 + 1]; }
    return (left, right);
  }

  // Upward zero crossings over a sample window → fundamental frequency in Hz.
  private static double FundamentalHz(short[] samples, double seconds) {
    // Centre the signal around its mean so a DC-offset square still crosses.
    double mean = 0;
    foreach (var s in samples) mean += s;
    mean /= samples.Length;
    var crossings = 0;
    for (var i = 1; i < samples.Length; ++i)
      if (samples[i - 1] - mean <= 0 && samples[i] - mean > 0) ++crossings;
    return crossings / seconds;
  }

  // Configures CH2 (pulse, no sweep) with a frequency value, duty, full volume, both sides.
  private static void SetupPulse2(GameBoyApu apu, int freq, int duty = 2, byte envelope = 0xF0) {
    apu.Write(0xFF25, 0x22);                  // NR51: CH2 to both L and R
    apu.Write(0xFF16, (byte)(duty << 6));     // NR21 duty
    apu.Write(0xFF17, envelope);              // NR22 envelope (vol 15, no decay)
    apu.Write(0xFF18, (byte)(freq & 0xFF));   // NR23 freq lo
    apu.Write(0xFF19, (byte)(0x80 | ((freq >> 8) & 0x07))); // NR24 trigger + freq hi
  }

  [Test]
  public void Pulse_FrequencyFollowsFormula() {
    // Target ~440 Hz: f = 131072/(2048-x) → x = 2048 - 131072/440.
    var x = (int)Math.Round(2048 - 131072.0 / 440.0);
    var expected = 131072.0 / (2048 - x);

    var apu = PoweredApu();
    SetupPulse2(apu, x);
    var (left, _) = Render(apu, 0.5);
    var hz = FundamentalHz(left, 0.5);
    Assert.That(hz, Is.EqualTo(expected).Within(20), $"measured {hz} Hz, expected {expected}");
  }

  [Test]
  public void Pulse_HigherFrequencyValueRaisesPitch() {
    var lowX = (int)Math.Round(2048 - 131072.0 / 220.0);
    var highX = (int)Math.Round(2048 - 131072.0 / 880.0);

    var apuLow = PoweredApu(); SetupPulse2(apuLow, lowX);
    var apuHigh = PoweredApu(); SetupPulse2(apuHigh, highX);
    var lowHz = FundamentalHz(Render(apuLow, 0.5).Left, 0.5);
    var highHz = FundamentalHz(Render(apuHigh, 0.5).Left, 0.5);
    Assert.That(highHz, Is.GreaterThan(lowHz * 2 - 100));
  }

  [Test]
  public void Pulse_DutyAffectsMeanLevel() {
    // 12.5% duty spends less time high than 75% duty → lower average level.
    var x = (int)Math.Round(2048 - 131072.0 / 440.0);
    var apuLow = PoweredApu(); SetupPulse2(apuLow, x, duty: 0);  // 12.5%
    var apuHigh = PoweredApu(); SetupPulse2(apuHigh, x, duty: 3); // 75%

    double MeanAbs(short[] s) { double m = 0; foreach (var v in s) m += v; return m / s.Length; }
    var lowMean = MeanAbs(Render(apuLow, 0.2).Left);
    var highMean = MeanAbs(Render(apuHigh, 0.2).Left);
    Assert.That(highMean, Is.GreaterThan(lowMean));
  }

  [Test]
  public void Pulse_EnvelopeDecaysToSilence() {
    // Envelope: initial volume 15, decreasing, fast period 1 → fully decays in 15/64 s.
    var x = (int)Math.Round(2048 - 131072.0 / 440.0);
    var apu = PoweredApu();
    SetupPulse2(apu, x, envelope: 0xF1); // vol 15, decrease, period 1

    // Measure the AC swing (peak-to-peak) at the start versus the end. A decayed-but-enabled
    // pulse sits at a constant DC level (digital 0), so its swing — not its absolute value —
    // collapses to near zero.
    var (left, _) = Render(apu, 0.5);
    int Swing(int from, int to) {
      int lo = int.MaxValue, hi = int.MinValue;
      for (var i = from; i < to; ++i) { lo = Math.Min(lo, left[i]); hi = Math.Max(hi, left[i]); }
      return hi - lo;
    }
    var firstSwing = Swing(0, Rate / 20);
    var lastSwing = Swing(left.Length - Rate / 20, left.Length);
    Assert.That(firstSwing, Is.GreaterThan(1000), "should start audible");
    Assert.That(lastSwing, Is.LessThan(firstSwing / 4), "swing should decay near silent");
  }

  [Test]
  public void Wave_PlaysCraftedPattern() {
    var apu = PoweredApu();
    apu.Write(0xFF25, 0x44);          // NR51: CH3 to both sides
    apu.Write(0xFF1A, 0x80);          // NR30 DAC on
    apu.Write(0xFF1C, 0x20);          // NR32 output level 100%
    // Wave RAM: a square — first 16 samples 0x0, next 16 0xF.
    for (var i = 0; i < 8; ++i) apu.Write((ushort)(0xFF30 + i), 0x00);
    for (var i = 8; i < 16; ++i) apu.Write((ushort)(0xFF30 + i), 0xFF);
    // The waveform repeats once per 32-sample table, giving a fundamental of 65536/(2048-x) Hz.
    var x = (int)Math.Round(2048 - 65536.0 / 440.0);
    apu.Write(0xFF1D, (byte)(x & 0xFF));
    apu.Write(0xFF1E, (byte)(0x80 | ((x >> 8) & 0x07))); // trigger

    var (left, _) = Render(apu, 0.5);
    var peak = left.Max(s => Math.Abs((int)s));
    Assert.That(peak, Is.GreaterThan(500), "wave channel should be audible");
    var hz = FundamentalHz(left, 0.5);
    Assert.That(hz, Is.EqualTo(440).Within(30), $"measured {hz} Hz");
  }

  [Test]
  public void Noise_ProducesBroadbandOutput() {
    var apu = PoweredApu();
    apu.Write(0xFF25, 0x88);   // NR51: CH4 to both sides
    apu.Write(0xFF20, 0x00);   // NR41 length
    apu.Write(0xFF21, 0xF0);   // NR42 vol 15
    apu.Write(0xFF22, 0x44);   // NR43 shift 4, 15-bit, divisor 4
    apu.Write(0xFF23, 0x80);   // NR44 trigger

    var (left, _) = Render(apu, 0.2);
    var peak = left.Max(s => Math.Abs((int)s));
    Assert.That(peak, Is.GreaterThan(500), "noise should be audible");
  }

  [Test]
  public void Noise_7BitModeRepeatsSooner() {
    // 7-bit mode has a much shorter LFSR period than 15-bit. Compare the number of distinct
    // run-lengths / energy is hard to assert exactly; instead assert both modes are audible and
    // the 7-bit output has a measurably more periodic (tonal) character via more zero crossings.
    GameBoyApu Build(bool sevenBit) {
      var apu = PoweredApu();
      apu.Write(0xFF25, 0x88);
      apu.Write(0xFF21, 0xF0);
      apu.Write(0xFF22, (byte)(0x40 | (sevenBit ? 0x08 : 0x00))); // shift 4, width, divisor 0
      apu.Write(0xFF23, 0x80);
      return apu;
    }

    var (left15, _) = Render(Build(false), 0.1);
    var (left7, _) = Render(Build(true), 0.1);
    // 7-bit mode is strongly periodic so it crosses far more regularly: its crossing rate is
    // significantly higher relative to the longer 15-bit sequence over the same window.
    var c15 = FundamentalHz(left15, 0.1);
    var c7 = FundamentalHz(left7, 0.1);
    Assert.That(left7.Max(s => Math.Abs((int)s)), Is.GreaterThan(500));
    Assert.That(left15.Max(s => Math.Abs((int)s)), Is.GreaterThan(500));
    Assert.That(c7, Is.GreaterThan(c15), $"7-bit ({c7}) should be more periodic than 15-bit ({c15})");
  }

  [Test]
  public void Sweep_OverflowSilencesChannel() {
    var apu = PoweredApu();
    apu.Write(0xFF25, 0x11);     // NR51: CH1 both sides
    // Sweep: period 1, increase (negate=0), shift 1 → freq doubles each step, overflows quickly.
    apu.Write(0xFF10, 0x11);     // NR10: period 1, add, shift 1
    apu.Write(0xFF11, 0x80);     // NR11 duty 2
    apu.Write(0xFF12, 0xF0);     // NR12 vol 15
    apu.Write(0xFF13, 0x00);     // NR13 freq lo
    apu.Write(0xFF14, 0x87);     // NR14 trigger, freq hi = 7 → freq 0x700, large
    var (left, _) = Render(apu, 0.5);
    // After the sweep overflows the channel disables; the tail should be silent.
    var tailPeak = 0;
    for (var i = left.Length - Rate / 10; i < left.Length; ++i) tailPeak = Math.Max(tailPeak, Math.Abs(left[i]));
    Assert.That(tailPeak, Is.EqualTo(0), "sweep overflow should silence CH1");
  }

  [Test]
  public void Nr51_RoutesChannelToOneSideOnly() {
    var apu = PoweredApu();
    var x = (int)Math.Round(2048 - 131072.0 / 440.0);
    // Route CH2 to LEFT only (bit 5), nothing to right.
    apu.Write(0xFF25, 0x20);
    apu.Write(0xFF16, 0x80);
    apu.Write(0xFF17, 0xF0);
    apu.Write(0xFF18, (byte)(x & 0xFF));
    apu.Write(0xFF19, (byte)(0x80 | ((x >> 8) & 0x07)));

    var (left, right) = Render(apu, 0.2);
    var leftPeak = left.Max(s => Math.Abs((int)s));
    var rightSpread = right.Max() - right.Min();
    Assert.That(leftPeak, Is.GreaterThan(500), "left should carry CH2");
    Assert.That(rightSpread, Is.EqualTo(0), "right should be flat (CH2 not routed)");
  }

  [Test]
  public void Nr52_PowerOff_SilencesEverything() {
    var apu = PoweredApu();
    var x = (int)Math.Round(2048 - 131072.0 / 440.0);
    SetupPulse2(apu, x);
    apu.Write(0xFF26, 0x00); // power off
    var (left, _) = Render(apu, 0.1);
    Assert.That(left.Max(s => Math.Abs((int)s)), Is.EqualTo(0));
  }
}
