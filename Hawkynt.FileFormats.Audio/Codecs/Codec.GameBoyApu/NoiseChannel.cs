#pragma warning disable CS1591
namespace Codec.GameBoyApu;

/// <summary>
/// The Game Boy noise channel (CH4, $FF20-$FF23). It generates pseudo-random output from a
/// 15-bit linear-feedback shift register. Registers relative to $FF20:
/// <list type="bullet">
///   <item>reg 0 (NR41) — bits 5-0 length load (counts up to 64).</item>
///   <item>reg 1 (NR42) — bits 7-4 initial volume, bit 3 envelope add/sub, bits 2-0 period.
///     Bits 7-3 all zero disables the DAC and the channel.</item>
///   <item>reg 2 (NR43) — bits 7-4 clock shift, bit 3 width mode (1 = 7-bit LFSR), bits 2-0
///     divisor code selecting the base divisor {8,16,32,48,64,80,96,112}.</item>
///   <item>reg 3 (NR44) — bit 7 trigger, bit 6 length-enable.</item>
/// </list>
/// On each timer reload the LFSR is clocked: <c>bit = (lfsr ^ (lfsr &gt;&gt; 1)) &amp; 1</c> is fed
/// back into bit 14 (and, in 7-bit mode, also bit 6). The output is the inverted low bit gated
/// by the envelope volume.
/// </summary>
internal sealed class NoiseChannel {

  private static readonly int[] Divisors = [8, 16, 32, 48, 64, 80, 96, 112];

  private int _clockShift;
  private bool _widthMode7;
  private int _divisorCode;
  private int _timer;
  private int _lfsr = 0x7FFF;

  private int _lengthCounter;     // max 64
  private bool _lengthEnabled;

  private int _initialVolume;
  private bool _envelopeAdd;
  private int _envelopePeriod;
  private int _volume;
  private int _envelopeTimer;

  private bool _dacEnabled;
  private bool _enabled;

  public bool Enabled => this._enabled;

  public void Disable() {
    this._enabled = false;
    this._lengthCounter = 0;
  }

  public void Write(int reg, byte value) {
    switch (reg) {
      case 0:
        this._lengthCounter = 64 - (value & 0x3F);
        break;
      case 1:
        this._initialVolume = (value >> 4) & 0x0F;
        this._envelopeAdd = (value & 0x08) != 0;
        this._envelopePeriod = value & 0x07;
        this._dacEnabled = (value & 0xF8) != 0;
        if (!this._dacEnabled)
          this._enabled = false;
        break;
      case 2:
        this._clockShift = (value >> 4) & 0x0F;
        this._widthMode7 = (value & 0x08) != 0;
        this._divisorCode = value & 0x07;
        break;
      case 3:
        this._lengthEnabled = (value & 0x40) != 0;
        if ((value & 0x80) != 0)
          this.Trigger();
        break;
    }
  }

  private int Period() => Divisors[this._divisorCode] << this._clockShift;

  private void Trigger() {
    if (this._dacEnabled)
      this._enabled = true;
    if (this._lengthCounter == 0)
      this._lengthCounter = 64;
    this._timer = this.Period();
    this._volume = this._initialVolume;
    this._envelopeTimer = this._envelopePeriod == 0 ? 8 : this._envelopePeriod;
    this._lfsr = 0x7FFF;
  }

  public void StepTimer() {
    if (--this._timer > 0)
      return;
    this._timer = this.Period();

    var feedback = (this._lfsr ^ (this._lfsr >> 1)) & 1;
    this._lfsr = (this._lfsr >> 1) | (feedback << 14);
    if (this._widthMode7) {
      this._lfsr &= ~(1 << 6);
      this._lfsr |= feedback << 6;
    }
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

  /// <summary>Current DAC output level 0..15, or -1 when the DAC is off (silent). Output is the inverted LFSR low bit.</summary>
  public int Output() {
    if (!this._enabled || !this._dacEnabled)
      return -1;
    return (~this._lfsr & 1) * this._volume;
  }
}
