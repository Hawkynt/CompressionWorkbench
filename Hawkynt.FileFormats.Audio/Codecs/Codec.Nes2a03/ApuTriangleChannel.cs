#pragma warning disable CS1591
namespace Codec.Nes2a03;

/// <summary>
/// The 2A03 triangle channel. An 11-bit timer steps a 32-entry sequencer that ramps from
/// 15 down to 0 and back up to 15, producing a symmetric triangle. Output is gated by both
/// a linear counter (clocked at the quarter-frame rate, with a reload/control flag) and a
/// length counter (clocked at the half-frame rate).
/// </summary>
internal sealed class ApuTriangleChannel {

  // The 32-step triangle sequence: 15..0 then 0..15.
  private static readonly byte[] Sequence = [
    15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
    0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
  ];

  private int _timerPeriod;
  private int _timerValue;
  private int _sequenceStep;

  private bool _enabled;
  private int _lengthCounter;

  private bool _controlFlag;        // also the length-counter halt flag
  private int _linearReload;
  private int _linearCounter;
  private bool _linearReloadFlag;

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
      case 0: // $4008 — control flag + linear counter reload value
        this._controlFlag = (value & 0x80) != 0;
        this._linearReload = value & 0x7F;
        break;
      case 2: // $400A — timer low
        this._timerPeriod = (this._timerPeriod & 0x700) | value;
        break;
      case 3: // $400B — timer high + length load
        this._timerPeriod = (this._timerPeriod & 0x0FF) | ((value & 0x07) << 8);
        if (this._enabled)
          this._lengthCounter = ApuLengthTable.Lookup(value >> 3);
        this._linearReloadFlag = true;
        break;
    }
  }

  public void ClockTimer() {
    // The sequencer only advances when both gating counters are non-zero.
    if (this._lengthCounter == 0 || this._linearCounter == 0)
      return;
    if (this._timerValue == 0) {
      this._timerValue = this._timerPeriod;
      this._sequenceStep = (this._sequenceStep + 1) & 0x1F;
    } else
      --this._timerValue;
  }

  /// <summary>Quarter-frame clock: drives the linear counter.</summary>
  public void ClockLinear() {
    if (this._linearReloadFlag)
      this._linearCounter = this._linearReload;
    else if (this._linearCounter > 0)
      --this._linearCounter;
    if (!this._controlFlag)
      this._linearReloadFlag = false;
  }

  /// <summary>Half-frame clock: drives the length counter.</summary>
  public void ClockLength() {
    if (!this._controlFlag && this._lengthCounter > 0)
      --this._lengthCounter;
  }

  /// <summary>Current channel output, 0..15.</summary>
  public int Output() {
    // A period below 2 produces an ultrasonic tone; emit the mid level to avoid a pop.
    if (this._timerPeriod < 2)
      return 7;
    return Sequence[this._sequenceStep];
  }
}
