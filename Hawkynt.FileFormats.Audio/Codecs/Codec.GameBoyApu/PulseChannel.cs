#pragma warning disable CS1591
namespace Codec.GameBoyApu;

/// <summary>
/// A Game Boy pulse (square-wave) channel: CH1 ($FF10-$FF14, with frequency sweep) or CH2
/// ($FF15-$FF19, no sweep). Registers are addressed relative to the channel base:
/// <list type="bullet">
///   <item>reg 0 (NRx0) — CH1 sweep: bits 6-4 period, bit 3 negate, bits 2-0 shift. Unused on CH2.</item>
///   <item>reg 1 (NRx1) — bits 7-6 duty, bits 5-0 length load (counts up to 64).</item>
///   <item>reg 2 (NRx2) — bits 7-4 initial volume, bit 3 envelope add/sub, bits 2-0 period.
///     Bits 7-3 all zero disables the DAC and the channel.</item>
///   <item>reg 3 (NRx3) — frequency low 8 bits.</item>
///   <item>reg 4 (NRx4) — bit 7 trigger, bit 6 length-enable, bits 2-0 frequency high 3 bits.</item>
/// </list>
/// The wave timer reloads with <c>(2048 - frequency) * 4</c> master cycles and advances an
/// 8-step duty pointer, giving a fundamental of <c>131072 / (2048 - frequency)</c> Hz.
/// </summary>
internal sealed class PulseChannel {

  // Duty patterns (one bit per of the 8 phase steps): 12.5%, 25%, 50%, 75%.
  private static readonly byte[] DutyTable = [0b00000001, 0b00000011, 0b00001111, 0b11111100];

  private readonly bool _hasSweep;

  private int _duty;
  private int _frequency;          // 11-bit
  private int _timer;
  private int _dutyStep;

  // Length counter (max 64).
  private int _lengthCounter;
  private bool _lengthEnabled;

  // Envelope.
  private int _initialVolume;
  private bool _envelopeAdd;
  private int _envelopePeriod;
  private int _volume;
  private int _envelopeTimer;

  // Sweep (CH1 only).
  private int _sweepPeriod;
  private bool _sweepNegate;
  private int _sweepShift;
  private int _sweepTimer;
  private bool _sweepEnabled;
  private int _sweepShadow;

  private bool _dacEnabled;
  private bool _enabled;

  public PulseChannel(bool hasSweep) => this._hasSweep = hasSweep;

  public bool Enabled => this._enabled;

  public void Disable() {
    this._enabled = false;
    this._lengthCounter = 0;
  }

  public void Write(int reg, byte value) {
    switch (reg) {
      case 0:
        if (this._hasSweep) {
          this._sweepPeriod = (value >> 4) & 0x07;
          this._sweepNegate = (value & 0x08) != 0;
          this._sweepShift = value & 0x07;
        }
        break;
      case 1:
        this._duty = (value >> 6) & 0x03;
        this._lengthCounter = 64 - (value & 0x3F);
        break;
      case 2:
        this._initialVolume = (value >> 4) & 0x0F;
        this._envelopeAdd = (value & 0x08) != 0;
        this._envelopePeriod = value & 0x07;
        this._dacEnabled = (value & 0xF8) != 0;
        if (!this._dacEnabled)
          this._enabled = false;
        break;
      case 3:
        this._frequency = (this._frequency & 0x0700) | value;
        break;
      case 4:
        this._frequency = (this._frequency & 0x00FF) | ((value & 0x07) << 8);
        this._lengthEnabled = (value & 0x40) != 0;
        if ((value & 0x80) != 0)
          this.Trigger();
        break;
    }
  }

  private void Trigger() {
    if (this._dacEnabled)
      this._enabled = true;
    if (this._lengthCounter == 0)
      this._lengthCounter = 64;
    this._timer = (2048 - this._frequency) * 4;
    this._volume = this._initialVolume;
    this._envelopeTimer = this._envelopePeriod == 0 ? 8 : this._envelopePeriod;

    if (this._hasSweep) {
      this._sweepShadow = this._frequency;
      this._sweepTimer = this._sweepPeriod == 0 ? 8 : this._sweepPeriod;
      this._sweepEnabled = this._sweepPeriod != 0 || this._sweepShift != 0;
      // An immediate overflow check on trigger when the shift is non-zero.
      if (this._sweepShift != 0)
        this.ComputeSweep();
    }
  }

  public void StepTimer() {
    if (--this._timer > 0)
      return;
    this._timer = (2048 - this._frequency) * 4;
    this._dutyStep = (this._dutyStep + 1) & 7;
  }

  public void ClockLength() {
    if (!this._lengthEnabled || this._lengthCounter == 0)
      return;
    if (--this._lengthCounter == 0)
      this._enabled = false;
  }

  public void ClockEnvelope() {
    if (this._envelopePeriod == 0)
      return;
    if (--this._envelopeTimer > 0)
      return;
    this._envelopeTimer = this._envelopePeriod;
    if (this._envelopeAdd && this._volume < 15)
      ++this._volume;
    else if (!this._envelopeAdd && this._volume > 0)
      --this._volume;
  }

  public void ClockSweep() {
    if (!this._hasSweep)
      return;
    if (--this._sweepTimer > 0)
      return;
    this._sweepTimer = this._sweepPeriod == 0 ? 8 : this._sweepPeriod;
    if (!this._sweepEnabled || this._sweepPeriod == 0)
      return;

    var newFreq = this.ComputeSweep();
    if (newFreq <= 2047 && this._sweepShift != 0) {
      this._sweepShadow = newFreq;
      this._frequency = newFreq;
      this.ComputeSweep(); // second overflow check
    }
  }

  // Computes the next sweep frequency; an overflow (> 2047) disables the channel and returns
  // the offending value.
  private int ComputeSweep() {
    var delta = this._sweepShadow >> this._sweepShift;
    var newFreq = this._sweepNegate ? this._sweepShadow - delta : this._sweepShadow + delta;
    if (newFreq > 2047)
      this._enabled = false;
    return newFreq;
  }

  /// <summary>Current DAC output level 0..15, or -1 when the DAC is off (silent).</summary>
  public int Output() {
    if (!this._enabled || !this._dacEnabled)
      return -1;
    var bit = (DutyTable[this._duty] >> this._dutyStep) & 1;
    return bit * this._volume;
  }
}
