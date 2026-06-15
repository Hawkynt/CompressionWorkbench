#pragma warning disable CS1591
namespace Codec.Nes2a03.Expansion;

/// <summary>
/// The Nintendo MMC5 mapper's expansion sound: two pulse channels (identical to the 2A03 pulses
/// but with no sweep unit and no ultrasonic muting) plus a raw 8-bit PCM channel.
/// <list type="bullet">
///   <item><b>Pulse 1/2</b> (<c>$5000-$5003</c> / <c>$5004-$5007</c>): same duty/envelope/length
///     layout as the APU pulse; <c>$5001/$5005</c> (sweep) are unimplemented. The envelope and
///     length counters run from a fixed 240 Hz clock rather than the APU frame sequencer.</item>
///   <item><b>PCM</b> (<c>$5010</c> mode, <c>$5011</c> data): in write mode a byte to
///     <c>$5011</c> is loaded straight onto the 8-bit DAC (a write of 0 is ignored — it raises an
///     IRQ on hardware). Read mode is not used by NSF tunes.</item>
///   <item><b>$5015</b>: bits 0-1 enable the two pulses.</item>
/// </list>
/// <para>References: NESdev wiki <i>MMC5 audio</i> — the pulses are "equivalent in volume to the
/// corresponding APU channels", so they are mixed through the same nonlinear pulse table; the PCM
/// is equivalent to the APU DMC level. Output is pre-scaled into the 2A03 mixer's ~0..1 domain.</para>
/// </summary>
internal sealed class Mmc5Audio : IExpansionAudio {

  private sealed class Pulse {
    public int Duty;
    public int DutyStep;
    public int Period;
    public int Divider;

    public bool EnvelopeLoop;     // also length halt
    public bool ConstantVolume;
    public int Volume;
    public bool EnvelopeStart;
    public int EnvelopeDivider;
    public int EnvelopeDecay;

    public bool Enabled;
    public int LengthCounter;

    private static readonly byte[][] DutyTable = [
      [0, 1, 0, 0, 0, 0, 0, 0],
      [0, 1, 1, 0, 0, 0, 0, 0],
      [0, 1, 1, 1, 1, 0, 0, 0],
      [1, 0, 0, 1, 1, 1, 1, 1],
    ];

    public void Write(int reg, byte value) {
      switch (reg) {
        case 0:
          this.Duty = (value >> 6) & 0x03;
          this.EnvelopeLoop = (value & 0x20) != 0;
          this.ConstantVolume = (value & 0x10) != 0;
          this.Volume = value & 0x0F;
          break;
        case 1: // sweep — not implemented on the MMC5
          break;
        case 2:
          this.Period = (this.Period & 0x700) | value;
          break;
        case 3:
          this.Period = (this.Period & 0x0FF) | ((value & 0x07) << 8);
          if (this.Enabled)
            this.LengthCounter = ApuLengthTable.Lookup(value >> 3);
          this.DutyStep = 0;
          this.EnvelopeStart = true;
          break;
      }
    }

    public void ClockTimer() {
      if (this.Divider == 0) {
        this.Divider = this.Period;
        this.DutyStep = (this.DutyStep + 1) & 0x07;
      } else {
        --this.Divider;
      }
    }

    public void ClockEnvelope() {
      if (this.EnvelopeStart) {
        this.EnvelopeStart = false;
        this.EnvelopeDecay = 15;
        this.EnvelopeDivider = this.Volume;
        return;
      }
      if (this.EnvelopeDivider > 0) {
        --this.EnvelopeDivider;
        return;
      }
      this.EnvelopeDivider = this.Volume;
      if (this.EnvelopeDecay > 0)
        --this.EnvelopeDecay;
      else if (this.EnvelopeLoop)
        this.EnvelopeDecay = 15;
    }

    public void ClockLength() {
      if (!this.EnvelopeLoop && this.LengthCounter > 0)
        --this.LengthCounter;
    }

    // No sweep and no ultrasonic muting: silence only when length expired or duty step low.
    public int Output() {
      if (this.LengthCounter == 0 || DutyTable[this.Duty][this.DutyStep] == 0)
        return 0;
      return this.ConstantVolume ? this.Volume : this.EnvelopeDecay;
    }
  }

  private readonly Pulse _pulse1 = new();
  private readonly Pulse _pulse2 = new();
  private int _pcm;             // 8-bit raw DAC value
  private bool _pcmReadMode;

  private readonly double _clockHz;
  private readonly double _quarterFramePeriod;  // 240 Hz frame clock
  private double _frameCounter;
  private int _frameStep;

  // The 2A03 nonlinear pulse table, replicated so the equivalent-volume MMC5 pulses mix exactly
  // like APU pulses. pulse_out = 95.88 / (8128/(p1+p2) + 100), spanning ~0..0.26.
  private readonly float[] _pulseTable = new float[31];

  public Mmc5Audio(double clockHz) {
    this._clockHz = clockHz;
    this._quarterFramePeriod = clockHz / 240.0;
    this._pulseTable[0] = 0f;
    for (var i = 1; i < this._pulseTable.Length; ++i)
      this._pulseTable[i] = (float)(95.88 / (8128.0 / i + 100.0));
  }

  public bool HandlesWrite(ushort addr) => addr is
    >= 0x5000 and <= 0x5007 or 0x5010 or 0x5011 or 0x5015;

  public void Write(ushort addr, byte value) {
    switch (addr) {
      case >= 0x5000 and <= 0x5003:
        this._pulse1.Write(addr - 0x5000, value);
        break;
      case >= 0x5004 and <= 0x5007:
        this._pulse2.Write(addr - 0x5004, value);
        break;
      case 0x5010:
        this._pcmReadMode = (value & 0x01) != 0;
        break;
      case 0x5011:
        // In write mode a non-zero byte is loaded onto the DAC; a write of 0 raises an IRQ on
        // hardware and does not change the level.
        if (!this._pcmReadMode && value != 0)
          this._pcm = value;
        break;
      case 0x5015:
        this._pulse1.Enabled = (value & 0x01) != 0;
        if (!this._pulse1.Enabled) this._pulse1.LengthCounter = 0;
        this._pulse2.Enabled = (value & 0x02) != 0;
        if (!this._pulse2.Enabled) this._pulse2.LengthCounter = 0;
        break;
    }
  }

  public bool TryRead(ushort addr, out byte value) {
    if (addr == 0x5011) {
      value = (byte)this._pcm;
      return true;
    }
    value = 0;
    return false;
  }

  public void ClockOneCpuCycle() {
    this._pulse1.ClockTimer();
    this._pulse2.ClockTimer();

    this._frameCounter += 1.0;
    if (this._frameCounter < this._quarterFramePeriod)
      return;
    this._frameCounter -= this._quarterFramePeriod;
    // Fixed 240 Hz: quarter-frame (envelope) every tick, half-frame (length) every other.
    this._pulse1.ClockEnvelope();
    this._pulse2.ClockEnvelope();
    if ((this._frameStep & 1) == 1) {
      this._pulse1.ClockLength();
      this._pulse2.ClockLength();
    }
    this._frameStep = (this._frameStep + 1) & 0x03;
  }

  // PCM is equivalent to the APU DMC level (0..127 there). The MMC5 DAC is 8-bit (0..255), so the
  // documented "twice as loud" high bit; scale it to the same ~0..1 domain as the pulse table peak.
  private const float PcmScale = 0.26f / 255.0f;

  public float Output() {
    var pulseSum = this._pulse1.Output() + this._pulse2.Output();
    return this._pulseTable[pulseSum] + this._pcm * PcmScale;
  }

  // ── test hooks ───────────────────────────────────────────────────────────────
  internal int Pulse1Output => this._pulse1.Output();
  internal int Pcm => this._pcm;
}
