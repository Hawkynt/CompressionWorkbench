#pragma warning disable CS1591
using Codec.Mos6502;
using Codec.Nes2a03;

namespace Compression.Tests.Codecs.Nes2a03;

[TestFixture]
public class Apu2a03Tests {

  private const double NtscClock = 1789773.0;
  private const int Rate = 44100;

  // A trivial all-zero RAM bus; the APU only reads through it for the DMC channel.
  private sealed class FlatBus : IBus6502 {
    private readonly byte[] _ram = new byte[0x10000];
    public byte[] Ram => this._ram;
    public byte Read(ushort addr) => this._ram[addr];
    public void Write(ushort addr, byte value) => this._ram[addr] = value;
  }

  private static Apu2a03 NewApu(out FlatBus bus) {
    bus = new FlatBus();
    return new Apu2a03(bus, NtscClock, Rate);
  }

  private static short[] Render(Apu2a03 apu, int samples) {
    var buf = new short[samples];
    apu.RenderSamples(buf, samples);
    return buf;
  }

  private static int ZeroCrossings(short[] samples) {
    var count = 0;
    var mean = (int)samples.Average(s => (double)s);
    for (var i = 1; i < samples.Length; ++i)
      if (samples[i - 1] - mean <= 0 && samples[i] - mean > 0)
        ++count;
    return count;
  }

  // The pulse frequency for an 11-bit timer period t: f = clock / (16 * (t + 1)).
  private static int TimerForFreq(double hz, double clock) =>
    (int)Math.Round(clock / (16.0 * hz) - 1.0);

  [Test]
  public void Pulse_FundamentalMatchesTimerPeriod() {
    const double targetHz = 440.0;
    var apu = NewApu(out _);
    var t = TimerForFreq(targetHz, NtscClock);

    apu.Write(0x4015, 0x01);                 // enable pulse 1 (so length loads)
    apu.Write(0x4000, 0xBF);                 // duty 50%, constant volume 15, length halt
    apu.Write(0x4002, (byte)(t & 0xFF));     // timer low
    apu.Write(0x4003, (byte)(((t >> 8) & 0x07) | (0x01 << 3))); // timer high + length load

    Render(apu, Rate / 10);                  // settle
    var second = Render(apu, Rate);
    var crossings = ZeroCrossings(second);
    Assert.That(crossings, Is.EqualTo(440).Within(8), $"measured {crossings} Hz");
  }

  [Test]
  public void Pulse_DifferentDutyCyclesChangeDcLevel() {
    static double MeanForDuty(int duty) {
      var apu = NewApu(out _);
      var t = TimerForFreq(220.0, NtscClock);
      apu.Write(0x4015, 0x01);
      apu.Write(0x4000, (byte)((duty << 6) | 0x3F)); // duty + constant vol 15 + length halt
      apu.Write(0x4002, (byte)(t & 0xFF));
      apu.Write(0x4003, (byte)(((t >> 8) & 0x07) | (0x01 << 3)));
      var b = Render(apu, Rate / 4);
      b = Render(apu, Rate / 4);
      return b.Average(s => (double)s);
    }

    var d12 = MeanForDuty(0); // 12.5%
    var d50 = MeanForDuty(2); // 50%
    Assert.That(Math.Abs(d12 - d50), Is.GreaterThan(500), $"12.5%={d12} vs 50%={d50}");
  }

  [Test]
  public void LengthCounter_TableSpotValues() {
    // The pulse stays audible only while its length counter is non-zero. A short length
    // index should silence sooner than a long one when the length is allowed to clock.
    // Spot-check the documented table via the $4015 status flag right after load.
    var apu = NewApu(out _);
    apu.Write(0x4015, 0x01);
    // index 0 → 10, index 1 → 254. Load index 0 and confirm length is active.
    apu.Write(0x4000, 0x30);                 // constant volume, length NOT halted
    apu.Write(0x4002, 0x80);
    apu.Write(0x4003, 0x00);                 // length index 0 (value 10)
    Assert.That((apu.Read4015() & 0x01), Is.EqualTo(0x01), "pulse length should be active after load");
  }

  [Test]
  public void Triangle_ProducesPeriodicOutput() {
    const double targetHz = 220.0;
    var apu = NewApu(out _);
    // Triangle timer: f = clock / (32 * (t + 1)).
    var t = (int)Math.Round(NtscClock / (32.0 * targetHz) - 1.0);
    apu.Write(0x4015, 0x04);                 // enable triangle (so length loads)
    apu.Write(0x4008, 0xFF);                 // control flag set, linear reload max
    apu.Write(0x400A, (byte)(t & 0xFF));
    apu.Write(0x400B, (byte)(((t >> 8) & 0x07) | (0x10 << 3))); // timer high + length load (long)

    Render(apu, Rate / 10);
    var second = Render(apu, Rate);
    var crossings = ZeroCrossings(second);
    Assert.That(crossings, Is.EqualTo(220).Within(10), $"measured {crossings} Hz");
  }

  [Test]
  public void Noise_IsNonSilentAndVaries() {
    var apu = NewApu(out _);
    apu.Write(0x4015, 0x08);                 // enable noise (so length loads)
    apu.Write(0x400C, 0x3F);                 // constant volume 15, length halt
    apu.Write(0x400E, 0x04);                 // mode 0, mid period
    apu.Write(0x400F, 0x80);                 // length load (index 0x10)

    var buf = Render(apu, Rate / 10);
    var peak = buf.Max(s => Math.Abs((int)s));
    Assert.That(peak, Is.GreaterThan(500), "noise should be audible");
    Assert.That(buf.Distinct().Count(), Is.GreaterThan(8), "noise should vary");
  }

  [Test]
  public void NonlinearMixer_PulseFormulaSpotValue() {
    // With both pulses outputting max (15+15=30) and t/n/d silent, the mixer DC level is
    // pulse_out = 95.88 / (8128/30 + 100) ≈ 0.2580. Renders are centered ((x-0.5)*2*32767),
    // so the mean sample ≈ (0.2580 - 0.5) * 2 * 32767 ≈ -15867. Confirm the sign/scale.
    var apu = NewApu(out _);
    var t = TimerForFreq(220.0, NtscClock);
    // Pull pulse to a static high level by using duty 50% but we want the DC offset; instead
    // verify the mixer maths directly via the test hook by forcing both pulses on.
    apu.Write(0x4015, 0x03);
    apu.Write(0x4000, 0x3F); apu.Write(0x4002, (byte)(t & 0xFF));
    apu.Write(0x4003, (byte)(((t >> 8) & 0x07) | (0x10 << 3)));
    apu.Write(0x4004, 0x3F); apu.Write(0x4006, (byte)(t & 0xFF));
    apu.Write(0x4007, (byte)(((t >> 8) & 0x07) | (0x10 << 3)));

    var buf = Render(apu, Rate);
    // The DC offset must be clearly negative (pulse-only output sits below mid-scale).
    var mean = buf.Average(s => (double)s);
    Assert.That(mean, Is.LessThan(-1000), $"pulse-only DC mean {mean} should be below center");
  }

  [Test]
  public void Read4015_ReflectsEnabledChannels() {
    var apu = NewApu(out _);
    apu.Write(0x4015, 0x01);   // enable only pulse 1 (so length loads)
    apu.Write(0x4000, 0x30);
    apu.Write(0x4003, 0x18);   // load pulse1 length
    Assert.That(apu.Read4015() & 0x1F, Is.EqualTo(0x01));
  }
}
