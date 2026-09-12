#pragma warning disable CS1591
namespace Codec.Nes2a03;

/// <summary>
/// One of the 2A03's two square-wave channels. Carries an 11-bit timer, a 4-step duty
/// sequencer (one of four duty patterns), a volume/envelope generator, a length counter
/// and a sweep unit. The two channels differ only in how the sweep computes the negated
/// target period: pulse 1 negates as <c>-change-1</c> (one's complement), pulse 2 as
/// <c>-change</c> (two's complement).
/// </summary>
internal sealed class ApuPulseChannel {

  // The four duty cycles, each an 8-step bit pattern (12.5%, 25%, 50%, 75% — the last is
  // 25% inverted). Bit order matches the hardware sequencer's output for step 0..7.
  private static readonly byte[][] DutyTable = [
    [0, 1, 0, 0, 0, 0, 0, 0], // 12.5%
    [0, 1, 1, 0, 0, 0, 0, 0], // 25%
    [0, 1, 1, 1, 1, 0, 0, 0], // 50%
    [1, 0, 0, 1, 1, 1, 1, 1], // 25% negated (75%)
  ];

  private readonly bool _isPulse1;

  private int _duty;
  private int _dutyStep;

  private int _timerPeriod;
  private int _timerValue;

  // Envelope generator.
  private bool _envelopeStart;
  private int _envelopeDivider;
  private int _envelopeDecay;
  private bool _envelopeLoop;       // also the length-counter halt flag
  private bool _constantVolume;
  private int _volume;              // envelope period / constant volume nibble

  // Length counter.
  private bool _enabled;
  private int _lengthCounter;

  // Sweep unit.
  private bool _sweepEnabled;
  private int _sweepPeriod;
  private bool _sweepNegate;
  private int _sweepShift;
  private bool _sweepReload;
  private int _sweepDivider;

  public ApuPulseChannel(bool isPulse1) => this._isPulse1 = isPulse1;

  public bool Enabled {
    get => this._enabled;
    set {
      this._enabled = value;
      if (!value)
        this._lengthCounter = 0;
    }
  }

  public bool LengthActive => this._lengthCounter > 0;

  public void Write(int reg, byte value) {
    switch (reg) {
      case 0: // $4000 / $4004 — duty, length halt / envelope loop, constant volume, volume
        this._duty = (value >> 6) & 0x03;
        this._envelopeLoop = (value & 0x20) != 0;
        this._constantVolume = (value & 0x10) != 0;
        this._volume = value & 0x0F;
        break;
      case 1: // $4001 / $4005 — sweep
        this._sweepEnabled = (value & 0x80) != 0;
        this._sweepPeriod = (value >> 4) & 0x07;
        this._sweepNegate = (value & 0x08) != 0;
        this._sweepShift = value & 0x07;
        this._sweepReload = true;
        break;
      case 2: // $4002 / $4006 — timer low
        this._timerPeriod = (this._timerPeriod & 0x700) | value;
        break;
      case 3: // $4003 / $4007 — timer high, length load
        this._timerPeriod = (this._timerPeriod & 0x0FF) | ((value & 0x07) << 8);
        if (this._enabled)
          this._lengthCounter = ApuLengthTable.Lookup(value >> 3);
        this._dutyStep = 0;
        this._envelopeStart = true;
        break;
    }
  }

  /// <summary>Clocks the 11-bit timer (called at the APU rate); advances the duty sequencer.</summary>
  public void ClockTimer() {
    if (this._timerValue == 0) {
      this._timerValue = this._timerPeriod;
      this._dutyStep = (this._dutyStep + 1) & 0x07;
    } else
      --this._timerValue;
  }

  /// <summary>Quarter-frame clock: drives the envelope generator.</summary>
  public void ClockEnvelope() {
    if (this._envelopeStart) {
      this._envelopeStart = false;
      this._envelopeDecay = 15;
      this._envelopeDivider = this._volume;
      return;
    }
    if (this._envelopeDivider > 0) {
      --this._envelopeDivider;
      return;
    }
    this._envelopeDivider = this._volume;
    if (this._envelopeDecay > 0)
      --this._envelopeDecay;
    else if (this._envelopeLoop)
      this._envelopeDecay = 15;
  }

  /// <summary>Half-frame clock: drives the length counter.</summary>
  public void ClockLength() {
    if (!this._envelopeLoop && this._lengthCounter > 0)
      --this._lengthCounter;
  }

  /// <summary>Half-frame clock: drives the sweep unit.</summary>
  public void ClockSweep() {
    if (this._sweepDivider == 0 && this._sweepEnabled && this._sweepShift > 0 && !this.IsMuted) {
      var target = this.SweepTarget();
      if (target <= 0x7FF)
        this._timerPeriod = target;
    }
    if (this._sweepDivider == 0 || this._sweepReload) {
      this._sweepDivider = this._sweepPeriod;
      this._sweepReload = false;
    } else
      --this._sweepDivider;
  }

  private int SweepTarget() {
    var change = this._timerPeriod >> this._sweepShift;
    if (!this._sweepNegate)
      return this._timerPeriod + change;
    // Pulse 1 uses one's complement (subtract change + 1); pulse 2 uses two's complement.
    return this._timerPeriod - change - (this._isPulse1 ? 1 : 0);
  }

  // Muted when the timer is below 8 or the (positive) sweep would overflow $7FF.
  private bool IsMuted =>
    this._timerPeriod < 8 || (!this._sweepNegate && this.SweepTarget() > 0x7FF);

  /// <summary>Current channel output, 0..15.</summary>
  public int Output() {
    if (this._lengthCounter == 0 || this.IsMuted || DutyTable[this._duty][this._dutyStep] == 0)
      return 0;
    return this._constantVolume ? this._volume : this._envelopeDecay;
  }
}
