#pragma warning disable CS1591
using Codec.Mos6502;

namespace Codec.Nes2a03;

/// <summary>
/// The 2A03 delta-modulation channel. It plays back a 1-bit delta-coded sample fetched from
/// CPU memory through the <see cref="IBus6502"/> bus: each set bit nudges a 7-bit output
/// counter up by 2, each clear bit down by 2 (clamped to 0..127). The playback rate is one
/// of 16 tabulated timer periods; the sample address ($C000 + addr*64) and length
/// (length*16 + 1 bytes) come from the registers, with an optional loop.
/// </summary>
internal sealed class ApuDmcChannel {

  // NTSC DMC rate table (CPU-clock periods per output bit), one per 4-bit rate index.
  private static readonly int[] RateTable = [
    428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54,
  ];

  private readonly IBus6502 _bus;

  private bool _enabled;
  private bool _loopFlag;
  private int _timerPeriod = RateTable[0];
  private int _timerValue;

  private int _outputLevel;         // 7-bit delta counter, 0..127

  private int _sampleAddress;       // configured start address
  private int _sampleLength;        // configured length in bytes

  private int _currentAddress;
  private int _bytesRemaining;

  private int _shiftRegister;
  private int _bitsRemaining;
  private bool _silence = true;

  public ApuDmcChannel(IBus6502 bus) => this._bus = bus;

  public bool Active => this._bytesRemaining > 0;

  public void SetEnabled(bool value) {
    this._enabled = value;
    if (!value) {
      this._bytesRemaining = 0;
      return;
    }
    if (this._bytesRemaining == 0)
      this.RestartSample();
  }

  public void Write(int reg, byte value) {
    switch (reg) {
      case 0: // $4010 — loop flag + rate index (IRQ flag ignored for NSF playback)
        this._loopFlag = (value & 0x40) != 0;
        this._timerPeriod = RateTable[value & 0x0F];
        break;
      case 1: // $4011 — direct load of the 7-bit output level
        this._outputLevel = value & 0x7F;
        break;
      case 2: // $4012 — sample address: $C000 + value * 64
        this._sampleAddress = 0xC000 + (value << 6);
        break;
      case 3: // $4013 — sample length: value * 16 + 1 bytes
        this._sampleLength = (value << 4) + 1;
        break;
    }
  }

  private void RestartSample() {
    this._currentAddress = this._sampleAddress;
    this._bytesRemaining = this._sampleLength;
  }

  public void ClockTimer() {
    if (this._timerValue > 0) {
      --this._timerValue;
      return;
    }
    this._timerValue = this._timerPeriod;
    this.ClockOutput();
  }

  private void ClockOutput() {
    if (!this._silence) {
      if ((this._shiftRegister & 0x01) != 0) {
        if (this._outputLevel <= 125)
          this._outputLevel += 2;
      } else {
        if (this._outputLevel >= 2)
          this._outputLevel -= 2;
      }
    }
    this._shiftRegister >>= 1;

    if (this._bitsRemaining > 0)
      --this._bitsRemaining;
    if (this._bitsRemaining == 0) {
      this._bitsRemaining = 8;
      this.FillSampleBuffer();
    }
  }

  private void FillSampleBuffer() {
    if (this._bytesRemaining == 0) {
      this._silence = true;
      return;
    }
    this._silence = false;
    this._shiftRegister = this._bus.Read((ushort)this._currentAddress);
    this._currentAddress = this._currentAddress >= 0xFFFF ? 0x8000 : this._currentAddress + 1;
    --this._bytesRemaining;
    if (this._bytesRemaining == 0 && this._loopFlag && this._enabled)
      this.RestartSample();
  }

  /// <summary>Current channel output, the 7-bit delta counter (0..127).</summary>
  public int Output() => this._outputLevel;

  // Exposed for unit testing the delta stepping.
  internal int OutputLevel => this._outputLevel;
}
