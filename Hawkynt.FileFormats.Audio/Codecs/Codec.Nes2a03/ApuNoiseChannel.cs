#pragma warning disable CS1591
namespace Codec.Nes2a03;

/// <summary>
/// The 2A03 noise channel. A 15-bit linear-feedback shift register, clocked from a 16-entry
/// period table, produces pseudo-random output. In normal mode the feedback taps are bits 0
/// and 1; in the alternate "short" mode (mode flag set) they are bits 0 and 6, giving a much
/// shorter, more tonal sequence. Output is gated by a length counter and scaled by the same
/// volume/envelope generator as the pulse channels.
/// </summary>
internal sealed class ApuNoiseChannel {

  // NTSC noise period table (timer reload values), one per 4-bit period index.
  private static readonly int[] PeriodTable = [
    4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068,
  ];

  private int _timerPeriod;
  private int _timerValue;
  private int _shiftRegister = 1; // seeded to 1 on reset, as on hardware
  private bool _modeFlag;

  // Envelope generator (identical to the pulse channel's).
  private bool _envelopeStart;
  private int _envelopeDivider;
  private int _envelopeDecay;
  private bool _envelopeLoop;
  private bool _constantVolume;
  private int _volume;

  private bool _enabled;
  private int _lengthCounter;

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
      case 0: // $400C — length halt / envelope loop, constant volume, volume
        this._envelopeLoop = (value & 0x20) != 0;
        this._constantVolume = (value & 0x10) != 0;
        this._volume = value & 0x0F;
        break;
      case 2: // $400E — mode flag + period index
        this._modeFlag = (value & 0x80) != 0;
        this._timerPeriod = PeriodTable[value & 0x0F];
        break;
      case 3: // $400F — length load
        if (this._enabled)
          this._lengthCounter = ApuLengthTable.Lookup(value >> 3);
        this._envelopeStart = true;
        break;
    }
  }

  public void ClockTimer() {
    if (this._timerValue > 0) {
      --this._timerValue;
      return;
    }
    this._timerValue = this._timerPeriod;
    var tapBit = this._modeFlag ? 6 : 1;
    var feedback = (this._shiftRegister & 0x01) ^ ((this._shiftRegister >> tapBit) & 0x01);
    this._shiftRegister >>= 1;
    this._shiftRegister |= feedback << 14;
  }

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

  public void ClockLength() {
    if (!this._envelopeLoop && this._lengthCounter > 0)
      --this._lengthCounter;
  }

  /// <summary>Current channel output, 0..15. Silent when LFSR bit 0 is set or length expired.</summary>
  public int Output() {
    if (this._lengthCounter == 0 || (this._shiftRegister & 0x01) != 0)
      return 0;
    return this._constantVolume ? this._volume : this._envelopeDecay;
  }

  // Exposed for unit testing of the LFSR sequence.
  internal int ShiftRegister => this._shiftRegister;
}
