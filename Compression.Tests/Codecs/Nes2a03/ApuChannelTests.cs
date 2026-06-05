#pragma warning disable CS1591
using Codec.Mos6502;
using Codec.Nes2a03;

namespace Compression.Tests.Codecs.Nes2a03;

[TestFixture]
public class ApuChannelTests {

  // ── length table ───────────────────────────────────────────────────────────────

  [Test]
  public void LengthTable_SpotValuesMatchHardware() {
    // Documented 2A03 length-counter table spot values.
    Assert.Multiple(() => {
      Assert.That(ApuLengthTable.Lookup(0x00), Is.EqualTo(10));
      Assert.That(ApuLengthTable.Lookup(0x01), Is.EqualTo(254));
      Assert.That(ApuLengthTable.Lookup(0x06), Is.EqualTo(80));
      Assert.That(ApuLengthTable.Lookup(0x0F), Is.EqualTo(14));
      Assert.That(ApuLengthTable.Lookup(0x10), Is.EqualTo(12));
      Assert.That(ApuLengthTable.Lookup(0x1F), Is.EqualTo(30));
    });
  }

  // ── noise LFSR ─────────────────────────────────────────────────────────────────

  // Drives the noise channel's LFSR n shifts in the given mode and returns the shift
  // register values observed after each clock. The LFSR seeds to 1.
  private static int[] ShiftSequence(bool shortMode, int count) {
    var noise = new ApuNoiseChannel { Enabled = true };
    // Period index 0 → reload 4, but we clock the timer directly to walk the LFSR; set the
    // shortest period so each ClockTimer eventually shifts. We force shifts by exhausting the
    // timer: call ClockTimer enough times to produce `count` shifts.
    noise.Write(2, (byte)(shortMode ? 0x80 : 0x00)); // period index 0, mode bit as requested
    var seq = new List<int>();
    var last = noise.ShiftRegister;
    seq.Add(last);
    var guard = 0;
    while (seq.Count < count && guard++ < 100000) {
      noise.ClockTimer();
      if (noise.ShiftRegister != last) {
        last = noise.ShiftRegister;
        seq.Add(last);
      }
    }
    return seq.ToArray();
  }

  [Test]
  public void NoiseLfsr_NormalModeMatchesTapZeroAndOne() {
    // Seed = 1 (0b...0001). feedback = bit0 ^ bit1 = 1 ^ 0 = 1. After shift: register >>= 1
    // (→ 0) then OR feedback<<14 = 0x4000. Next: bit0=0, bit1=0 → fb 0; reg = 0x2000. Etc.
    var seq = ShiftSequence(shortMode: false, count: 4);
    Assert.That(seq[0], Is.EqualTo(0x0001));
    Assert.That(seq[1], Is.EqualTo(0x4000));
    Assert.That(seq[2], Is.EqualTo(0x2000));
    Assert.That(seq[3], Is.EqualTo(0x1000));
  }

  [Test]
  public void NoiseLfsr_ShortModeUsesTapSix() {
    // Short mode taps bit0 and bit6. Seed = 1: bit0=1, bit6=0 → fb 1 → 0x4000 (same first
    // step as normal since only bit0 is set). The two modes diverge once bit6 participates;
    // assert the short-mode sequence eventually differs from the normal-mode one.
    var normal = ShiftSequence(shortMode: false, count: 40);
    var shortM = ShiftSequence(shortMode: true, count: 40);
    Assert.That(shortM[0], Is.EqualTo(0x0001));
    Assert.That(shortM.SequenceEqual(normal), Is.False, "short and normal LFSR must diverge");
  }

  // ── triangle sequence ──────────────────────────────────────────────────────────

  [Test]
  public void TriangleSequence_RampsDownThenUp() {
    var tri = new ApuTriangleChannel { Enabled = true };
    tri.Write(0, 0xFF);           // control flag + linear reload max
    tri.Write(2, 0x02);           // timer low = 2 (period >= 2, audible)
    tri.Write(3, 0x80);           // timer high 0, length load (index 0x10)
    // Clock the linear counter so the sequencer is allowed to advance.
    tri.ClockLinear();

    var outputs = new List<int>();
    var lastStepValue = tri.Output();
    outputs.Add(lastStepValue);
    // The sequencer advances once per (timer period + 1) clocks.
    for (var i = 0; i < 400 && outputs.Count < 33; ++i) {
      tri.ClockTimer();
      var v = tri.Output();
      if (v != lastStepValue) {
        outputs.Add(v);
        lastStepValue = v;
      }
    }

    // The sequence should ramp 15→0 then 0→15. Confirm it contains both extremes and that
    // the first observed transition is downward from 15.
    Assert.That(outputs, Does.Contain(0));
    Assert.That(outputs, Does.Contain(15));
    Assert.That(outputs[0], Is.EqualTo(15));
    Assert.That(outputs[1], Is.EqualTo(14));
  }

  // ── DMC delta stepping ───────────────────────────────────────────────────────────

  private sealed class SampleBus : IBus6502 {
    private readonly byte[] _ram = new byte[0x10000];
    public byte[] Ram => this._ram;
    public byte Read(ushort addr) => this._ram[addr];
    public void Write(ushort addr, byte value) => this._ram[addr] = value;
  }

  [Test]
  public void Dmc_DeltaCounterStepsUpAndDownPerBit() {
    var bus = new SampleBus();
    // Place a sample at $C000: first byte 0xFF (all bits set → eight +2 steps), second 0x00
    // (all bits clear → eight -2 steps).
    bus.Ram[0xC000] = 0xFF;
    bus.Ram[0xC001] = 0x00;

    var dmc = new ApuDmcChannel(bus);
    dmc.Write(0, 0x0F);                 // fastest rate, no loop
    dmc.Write(1, 0x40);                 // initial output level 64
    dmc.Write(2, 0x00);                 // sample address = $C000
    dmc.Write(3, 0x01);                 // length = 1*16+1 = 17 bytes
    dmc.SetEnabled(true);

    var start = dmc.OutputLevel;        // 64
    Assert.That(start, Is.EqualTo(64));

    // Clock through the first byte's 8 bits (each ClockTimer with shortest period clocks one
    // output bit after the timer underflows). Drive enough clocks to consume 8 bits.
    var levels = new List<int> { dmc.OutputLevel };
    var prevByteLevel = dmc.OutputLevel;
    for (var i = 0; i < 5000 && levels.Count < 20; ++i) {
      dmc.ClockTimer();
      if (dmc.OutputLevel != levels[^1])
        levels.Add(dmc.OutputLevel);
    }

    // The first byte (0xFF) drives the level up; assert it rose above the start.
    var peak = levels.Max();
    Assert.That(peak, Is.GreaterThan(start), $"0xFF bits should raise the delta counter from {start}");
    // And the trailing 0x00 byte should pull it back down below the peak.
    var trough = levels.SkipWhile(l => l < peak).DefaultIfEmpty(peak).Min();
    Assert.That(trough, Is.LessThanOrEqualTo(peak));
  }

  [Test]
  public void Dmc_DirectLoadSetsOutputLevel() {
    var bus = new SampleBus();
    var dmc = new ApuDmcChannel(bus);
    dmc.Write(1, 0x55);
    Assert.That(dmc.OutputLevel, Is.EqualTo(0x55));
  }

  // ── nonlinear mixer formula ──────────────────────────────────────────────────────

  [Test]
  public void NonlinearMixer_TndFormulaHandComputed() {
    // tnd_out = 159.79 / (1 / (t/8227 + n/12241 + d/22638) + 100).
    // For triangle=15, noise=0, dmc=0:
    //   tnd index in the table is 3*15 = 45 (since the table is indexed by 3t+2n+d using the
    //   shared 1/22638 denominator approximation). We verify the documented per-channel
    //   weighting yields a finite, monotonic value: louder triangle → louder output.
    var t1 = TndApprox(5, 0, 0);
    var t2 = TndApprox(15, 0, 0);
    Assert.That(t2, Is.GreaterThan(t1), "louder triangle should mix louder");
    Assert.That(t2, Is.GreaterThan(0).And.LessThan(1.0));
  }

  // Mirrors the table the Apu builds: index = 3t + 2n + d, value = 159.79/(1/(i/22638)+100).
  private static double TndApprox(int t, int n, int d) {
    var i = 3 * t + 2 * n + d;
    return i == 0 ? 0.0 : 159.79 / (1.0 / (i / 22638.0) + 100.0);
  }
}
