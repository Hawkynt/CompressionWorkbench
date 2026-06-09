#pragma warning disable CS1591
using Codec.Nes2a03.Expansion;

namespace Compression.Tests.Codecs.Nes2a03;

[TestFixture]
public class ExpansionAudioTests {

  private const double NtscClock = 1789773.0;

  // ── VRC6 ────────────────────────────────────────────────────────────────────

  [Test]
  public void Vrc6_Pulse_OutputsVolumeWhenStepWithinDuty() {
    var chip = new Vrc6Audio();
    // $9000 = MDDD VVVV: mode 0, duty 7 (always within range for steps 0..7), volume 12.
    chip.Write(0x9000, 0x7C);
    chip.Write(0x9001, 0x02);        // period low
    chip.Write(0x9002, 0x80);        // enable, period high 0

    // With duty 7 the 16-step counter (15..0) is ≤ 7 for half the cycle. Clock enough to see
    // a non-zero output appear.
    var sawNonZero = false;
    for (var i = 0; i < 200; ++i) {
      chip.ClockOneCpuCycle();
      if (chip.Pulse1Output == 12)
        sawNonZero = true;
    }
    Assert.That(sawNonZero, Is.True, "duty-7 pulse should output its volume during the duty window");
  }

  [Test]
  public void Vrc6_Pulse_ModeBitForcesVolumeRegardlessOfDuty() {
    var chip = new Vrc6Audio();
    // Mode bit set, duty 0, volume 9 → always output 9 while enabled.
    chip.Write(0x9000, 0x89);
    chip.Write(0x9001, 0x10);
    chip.Write(0x9002, 0x80);        // enable
    chip.ClockOneCpuCycle();
    Assert.That(chip.Pulse1Output, Is.EqualTo(9));
  }

  [Test]
  public void Vrc6_Sawtooth_AccumulatorStepsByRateAndResets() {
    var chip = new Vrc6Audio();
    // Rate 0x08, period 0 so the divider underflows every clock. After the documented 14-stage
    // cycle the accumulator resets to zero.
    chip.Write(0xB000, 0x08);        // rate
    chip.Write(0xB001, 0x00);        // period low
    chip.Write(0xB002, 0x80);        // enable, period high 0

    // The accumulator adds the rate on even stages; six adds (0x08 * 6 = 0x30) then resets.
    // Walk one full 14-stage cycle and confirm the peak then the reset to zero.
    var peak = 0;
    for (var i = 0; i < 14; ++i) {
      chip.ClockOneCpuCycle();
      peak = Math.Max(peak, chip.SawAccumulator);
    }
    Assert.That(peak, Is.EqualTo(0x30), "six adds of rate 0x08 → accumulator peak 0x30");
    // The 14th step resets the accumulator.
    Assert.That(chip.SawAccumulator, Is.EqualTo(0), "stage 14 resets the accumulator");
  }

  [Test]
  public void Vrc6_Sawtooth_OutputIsHighFiveBitsOfAccumulator() {
    var chip = new Vrc6Audio();
    chip.Write(0xB000, 0x20);        // rate 0x20 → after one add accumulator = 0x20
    chip.Write(0xB001, 0x00);
    chip.Write(0xB002, 0x80);
    chip.ClockOneCpuCycle();         // stage 0 (even) adds 0x20
    Assert.That(chip.SawAccumulator, Is.EqualTo(0x20));
    Assert.That(chip.SawOutput, Is.EqualTo(0x20 >> 3), "saw output is the top 5 bits");
  }

  // ── MMC5 ────────────────────────────────────────────────────────────────────

  [Test]
  public void Mmc5_Pulse_ProducesVolumeOutput() {
    var chip = new Mmc5Audio(NtscClock);
    chip.Write(0x5015, 0x01);        // enable pulse 1
    chip.Write(0x5000, 0xBF);        // duty 50% + constant volume 15 + halt
    chip.Write(0x5002, 0x40);        // period low
    chip.Write(0x5003, 0x18);        // period high + length load

    var sawVolume = false;
    for (var i = 0; i < 1000; ++i) {
      chip.ClockOneCpuCycle();
      if (chip.Pulse1Output == 15)
        sawVolume = true;
    }
    Assert.That(sawVolume, Is.True, "MMC5 pulse should output its constant volume during the duty high phase");
  }

  [Test]
  public void Mmc5_Pcm_WriteLoadsDac() {
    var chip = new Mmc5Audio(NtscClock);
    chip.Write(0x5010, 0x00);        // write mode
    chip.Write(0x5011, 0x7A);        // raw PCM byte
    Assert.That(chip.Pcm, Is.EqualTo(0x7A));
    // A write of 0 raises IRQ on hardware and must not change the DAC.
    chip.Write(0x5011, 0x00);
    Assert.That(chip.Pcm, Is.EqualTo(0x7A));
  }

  // ── Sunsoft 5B (AY reuse) ────────────────────────────────────────────────────

  [Test]
  public void Sunsoft5B_KeyWriteReachesAyChip() {
    var chip = new Sunsoft5BAudio(NtscClock);
    // Latch register 0 (tone A fine period) via $C000, write data via $E000.
    chip.Write(0xC000, 0x00);
    chip.Write(0xE000, 0x55);
    Assert.That(chip.ReadReg(0), Is.EqualTo(0x55));

    // Program tone A with volume and produce a non-silent output.
    chip.Write(0xC000, 0x00); chip.Write(0xE000, 0x40); // fine period
    chip.Write(0xC000, 0x01); chip.Write(0xE000, 0x00); // coarse period
    chip.Write(0xC000, 0x07); chip.Write(0xE000, 0x3E); // mixer: tone A enabled
    chip.Write(0xC000, 0x08); chip.Write(0xE000, 0x0F); // channel A volume 15

    var peak = 0f;
    for (var i = 0; i < 20000; ++i) {
      chip.ClockOneCpuCycle();
      peak = Math.Max(peak, chip.Output());
    }
    Assert.That(peak, Is.GreaterThan(0f), "an enabled AY tone channel should produce output");
  }

  // ── Namco 163 ────────────────────────────────────────────────────────────────

  [Test]
  public void Namco163_RamReadbackThroughDataPort() {
    var chip = new Namco163Audio();
    // Address $10 with auto-increment, write three bytes, then read them back.
    chip.Write(0xF800, 0x90);        // address $10, auto-increment
    chip.Write(0x4800, 0x11);
    chip.Write(0x4800, 0x22);
    chip.Write(0x4800, 0x33);
    Assert.That(chip.ReadRam(0x10), Is.EqualTo(0x11));
    Assert.That(chip.ReadRam(0x11), Is.EqualTo(0x22));
    Assert.That(chip.ReadRam(0x12), Is.EqualTo(0x33));

    chip.Write(0xF800, 0x10);        // address $10, no auto-increment
    Assert.That(chip.TryRead(0x4800, out var v), Is.True);
    Assert.That(v, Is.EqualTo(0x11));
  }

  [Test]
  public void Namco163_ChannelCountFromRegister7F() {
    var chip = new Namco163Audio();
    // Register $7F bits 6-4 = active channel count - 1. Value 0x30 → C=3 → 4 active channels.
    chip.Write(0xF800, 0x7F);
    chip.Write(0x4800, 0x3F);        // C=3 in bits 6-4, volume 15
    Assert.That(chip.ActiveChannelCount, Is.EqualTo(4));
  }

  [Test]
  public void Namco163_WaveChannelProducesOutput() {
    var chip = new Namco163Audio();
    // One active channel (channel 8 at $78-$7F). Fill a 4-sample ramp wave at RAM $00.
    // Two 4-bit samples per byte: byte0 = 0xF0 → samples 0,15; byte1 = 0x80 → samples 0,8.
    chip.Write(0xF800, 0x80);        // address $00, auto-increment
    chip.Write(0x4800, 0xF0);
    chip.Write(0x4800, 0x80);

    // Channel 8 registers: freq, phase, length/freq-high, wave addr, volume/channels.
    void Reg(int addr, byte val) { chip.Write(0xF800, (byte)addr); chip.Write(0x4800, val); }
    Reg(0x78, 0x00);                 // freq low
    Reg(0x7A, 0x10);                 // freq mid (non-zero so phase advances)
    Reg(0x7C, 0xF0);                 // length bits 7-2 = 0x3C → 256-240 = ... small wave
    Reg(0x7E, 0x00);                 // wave address 0
    Reg(0x7F, 0x0F);                 // C=0 (1 channel), volume 15

    var nonZero = false;
    for (var i = 0; i < 5000; ++i) {
      chip.ClockOneCpuCycle();
      if (chip.ChannelOutput(7) != 0)
        nonZero = true;
    }
    Assert.That(nonZero, Is.True, "an N163 wave channel with a non-zero sample should output");
  }

  // ── VRC7 (Ym2413 reuse) ──────────────────────────────────────────────────────

  [Test]
  public void Vrc7_PatchWriteAndKeyOnProducesOutput() {
    var chip = new Vrc7Audio(NtscClock);
    // Program channel 0 with melodic instrument 1, max volume, set F-num/block, key on.
    chip.Write(0x9010, 0x30); chip.Write(0x9030, 0x10); // $30: instrument 1, volume 0 (loudest)
    chip.Write(0x9010, 0x10); chip.Write(0x9030, 0xA0); // $10: F-num low
    chip.Write(0x9010, 0x20); chip.Write(0x9030, 0x15); // $20: block 2 + key on

    var nonZero = false;
    for (var i = 0; i < 200000 && !nonZero; ++i) {
      chip.ClockOneCpuCycle();
      if (Math.Abs(chip.Output()) > 0f)
        nonZero = true;
    }
    Assert.That(nonZero, Is.True, "a keyed VRC7 FM channel should produce non-zero output");
  }
}
