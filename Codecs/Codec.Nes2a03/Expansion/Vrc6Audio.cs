#pragma warning disable CS1591
namespace Codec.Nes2a03.Expansion;

/// <summary>
/// The Konami VRC6 expansion sound unit: two variable-duty pulse channels plus one sawtooth.
/// <list type="bullet">
///   <item><b>Pulse 1/2</b> (<c>$9000-$9002</c> / <c>$A000-$A002</c>): a 12-bit period divider
///     advancing a 16-step duty counter (15→0). <c>$x000 = MDDD VVVV</c> — mode bit (output the
///     4-bit volume regardless of duty), 3-bit duty, 4-bit volume. Output is the volume when the
///     current step ≤ duty, else 0. <c>$x002</c> bit 7 enables the channel.</item>
///   <item><b>Sawtooth</b> (<c>$B000-$B002</c>): a 6-bit accumulator rate added to an 8-bit
///     accumulator on every other divider clock; after six adds the seventh clock resets the
///     accumulator. The high 5 bits of the accumulator form the DAC output.</item>
///   <item><b>$9003</b>: <c>.... .ABH</c> — halt (H) freezes all dividers; the 4×/8× shift bits
///     (A/B) right-shift the 12-bit periods, raising pitch.</item>
/// </list>
/// <para>References: NESdev wiki <i>VRC6 audio</i>; mix level from NSFPlay's VRC6 master
/// (pulses and saw mix linearly, comparable to a 2A03 pulse). Each pulse spans 0..15 and the saw
/// 0..31; the combined unit is scaled into the 2A03 mixer's ~0..1 domain in <see cref="Output"/>.</para>
/// </summary>
internal sealed class Vrc6Audio : IExpansionAudio {

  // ── pulse ────────────────────────────────────────────────────────────────────
  private sealed class Pulse {
    public bool ModeVolume;     // mode bit: ignore duty, output volume
    public int Duty;            // 0..7
    public int Volume;          // 0..15
    public int Period;          // 12-bit
    public bool Enabled;
    public int Divider;
    public int Step;            // 16-step duty counter (counts 15..0)

    public void Write(int reg, byte value) {
      switch (reg) {
        case 0:
          this.ModeVolume = (value & 0x80) != 0;
          this.Duty = (value >> 4) & 0x07;
          this.Volume = value & 0x0F;
          break;
        case 1:
          this.Period = (this.Period & 0xF00) | value;
          break;
        case 2:
          this.Period = (this.Period & 0x0FF) | ((value & 0x0F) << 8);
          this.Enabled = (value & 0x80) != 0;
          if (!this.Enabled)
            this.Step = 0;
          break;
      }
    }

    public void Clock(int shift) {
      if (!this.Enabled)
        return;
      if (this.Divider > 0) {
        --this.Divider;
        return;
      }
      this.Divider = this.Period >> shift;
      this.Step = (this.Step - 1) & 0x0F;
    }

    public int Output() {
      if (!this.Enabled)
        return 0;
      if (this.ModeVolume)
        return this.Volume;
      // Output the volume while the 16-step counter is at or below the duty threshold.
      return this.Step <= this.Duty ? this.Volume : 0;
    }
  }

  // ── sawtooth ────────────────────────────────────────────────────────────────
  private sealed class Saw {
    public int Rate;            // 6-bit accumulator rate
    public int Period;          // 12-bit
    public bool Enabled;
    public int Divider;
    public int Accumulator;     // 8-bit
    public int Stage;           // 0..13: an accumulate event fires every 2 divider clocks

    public void Write(int reg, byte value) {
      switch (reg) {
        case 0:
          this.Rate = value & 0x3F;
          break;
        case 1:
          this.Period = (this.Period & 0xF00) | value;
          break;
        case 2:
          this.Period = (this.Period & 0x0FF) | ((value & 0x0F) << 8);
          this.Enabled = (value & 0x80) != 0;
          if (!this.Enabled) {
            this.Accumulator = 0;
            this.Stage = 0;
          }
          break;
      }
    }

    public void Clock(int shift) {
      if (!this.Enabled)
        return;
      if (this.Divider > 0) {
        --this.Divider;
        return;
      }
      this.Divider = this.Period >> shift;
      // An accumulate event fires every other divider clock (a 14-clock cycle = 7 events). The
      // first six events add the rate (accumulator → 6·rate); the seventh resets it to zero.
      if ((this.Stage & 1) == 0) {
        if (this.Stage == 12)
          this.Accumulator = 0;
        else
          this.Accumulator = (this.Accumulator + this.Rate) & 0xFF;
      }
      this.Stage = (this.Stage + 1) % 14;
    }

    public int Output() => this.Enabled ? (this.Accumulator >> 3) & 0x1F : 0;
  }

  private readonly Pulse _pulse1 = new();
  private readonly Pulse _pulse2 = new();
  private readonly Saw _saw = new();
  private bool _halt;
  private int _freqShift;       // 0, 4 or 8 from the $9003 scaling bits

  public bool HandlesWrite(ushort addr) => addr is
    >= 0x9000 and <= 0x9003 or
    >= 0xA000 and <= 0xA002 or
    >= 0xB000 and <= 0xB002;

  public void Write(ushort addr, byte value) {
    switch (addr & 0xF000) {
      case 0x9000:
        if ((addr & 0x0003) == 0x0003) {
          this._halt = (value & 0x01) != 0;
          // Bit 1 → 16× (4-bit shift), bit 2 → 256× (8-bit shift).
          this._freqShift = (value & 0x04) != 0 ? 8 : (value & 0x02) != 0 ? 4 : 0;
        } else {
          this._pulse1.Write(addr & 0x0003, value);
        }
        break;
      case 0xA000:
        this._pulse2.Write(addr & 0x0003, value);
        break;
      case 0xB000:
        this._saw.Write(addr & 0x0003, value);
        break;
    }
  }

  public bool TryRead(ushort addr, out byte value) {
    value = 0;
    return false;
  }

  public void ClockOneCpuCycle() {
    if (this._halt)
      return;
    this._pulse1.Clock(this._freqShift);
    this._pulse2.Clock(this._freqShift);
    this._saw.Clock(this._freqShift);
  }

  // NSFPlay mixes the VRC6 unit linearly with the APU. A pulse maxes at 15 and the saw at 31;
  // their combined peak (61) is normalised so a full-scale VRC6 is roughly as loud as a 2A03
  // pulse pair, sitting at ~0.42 of full scale per NSFPlay's relative master.
  private const float MixScale = 0.42f / 61.0f;

  public float Output() =>
    (this._pulse1.Output() + this._pulse2.Output() + this._saw.Output()) * MixScale;

  // ── test hooks ───────────────────────────────────────────────────────────────
  internal int SawAccumulator => this._saw.Accumulator;
  internal int SawOutput => this._saw.Output();
  internal int Pulse1Output => this._pulse1.Output();
  internal int Pulse2Output => this._pulse2.Output();
}
