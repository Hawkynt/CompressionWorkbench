#pragma warning disable CS1591
using Codec.Ay8910;

namespace Codec.Nes2a03.Expansion;

/// <summary>
/// The Sunsoft 5B expansion sound: a Yamaha YM2149F (an AY-3-8910 PSG variant) — three square
/// tone channels, a noise generator and a hardware envelope. It is wired as a thin adapter over
/// <see cref="Ay8910Chip"/>, reusing that core's tone/noise/envelope generators and logarithmic
/// DAC table verbatim.
/// <para>The NSF register interface is a two-stage latch: a write to <c>$C000-$DFFF</c> selects
/// the 4-bit internal register, and a write to <c>$E000-$FFFF</c> stores the data byte into it.
/// The 5B runs the YM2149 with its SEL pin held low, so the generators clock at the master
/// clock / 16 — exactly the prescaler the <see cref="Ay8910Chip"/> already uses, so the NES CPU
/// clock is passed straight through.</para>
/// <para>References: NESdev wiki <i>Sunsoft 5B audio</i>. The 5B is documented as very loud
/// relative to the 2A03; NSFPlay's relative master places a full-scale 5B around 0.5 of the
/// mixer's full scale. Output is pre-scaled into the 2A03 mixer's ~0..1 domain.</para>
/// </summary>
internal sealed class Sunsoft5BAudio : IExpansionAudio {

  private readonly Ay8910Chip _psg;
  private int _latchedRegister;

  // The PSG generators run at clock/16; the expansion mixer clocks this chip once per CPU cycle,
  // so divide down by 16 to drive a single prescaler step.
  private int _prescaleDivider;

  public Sunsoft5BAudio(double clockHz) =>
    this._psg = new Ay8910Chip(clockHz, Ay8910Chip.StereoMode.Mono);

  public bool HandlesWrite(ushort addr) => addr >= 0xC000;

  public void Write(ushort addr, byte value) {
    if (addr is >= 0xC000 and <= 0xDFFF)
      this._latchedRegister = value & 0x0F;
    else if (addr >= 0xE000)
      this._psg.WriteReg(this._latchedRegister, value);
  }

  public bool TryRead(ushort addr, out byte value) {
    value = 0;
    return false;
  }

  public void ClockOneCpuCycle() {
    if (++this._prescaleDivider < 16)
      return;
    this._prescaleDivider = 0;
    this._psg.StepPrescaler();
  }

  // The three channels sum to a 0..3 linear range; the 5B's documented loudness places its full
  // scale near 0.5 of the 2A03 mixer's, so a per-channel ~0.17 master.
  private const double MixScale = 0.5 / 3.0;

  public float Output() => (float)(this._psg.MixMonoLinear() * MixScale);

  // ── test hooks ───────────────────────────────────────────────────────────────
  internal byte ReadReg(int reg) => this._psg.ReadReg(reg);
}
