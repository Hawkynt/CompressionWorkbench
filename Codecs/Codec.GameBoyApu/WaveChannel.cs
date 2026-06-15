#pragma warning disable CS1591
namespace Codec.GameBoyApu;

/// <summary>
/// The Game Boy wave channel (CH3, $FF1A-$FF1E) plus its 32×4-bit wave RAM ($FF30-$FF3F, two
/// nibbles per byte, high nibble first). Registers relative to $FF1A:
/// <list type="bullet">
///   <item>reg 0 (NR30) — bit 7 DAC power.</item>
///   <item>reg 1 (NR31) — length load (counts up to 256).</item>
///   <item>reg 2 (NR32) — bits 6-5 output level: 0=mute, 1=100%, 2=50%, 3=25% (right-shift 0/1/2).</item>
///   <item>reg 3 (NR33) — frequency low 8 bits.</item>
///   <item>reg 4 (NR34) — bit 7 trigger, bit 6 length-enable, bits 2-0 frequency high 3 bits.</item>
/// </list>
/// The wave timer reloads with <c>(2048 - frequency) * 2</c> master cycles and steps a 32-entry
/// sample pointer, so the channel plays its 32-sample table at <c>65536 / (2048 - frequency)</c> Hz.
/// </summary>
internal sealed class WaveChannel {

  private readonly byte[] _waveRam = new byte[16];

  private bool _dacEnabled;
  private int _outputLevel;       // shift amount: 4 (mute), 0, 1, 2
  private int _frequency;
  private int _timer;
  private int _position;
  private int _sampleBuffer;

  private int _lengthCounter;     // max 256
  private bool _lengthEnabled;
  private bool _enabled;

  public bool Enabled => this._enabled;

  public void Disable() {
    this._enabled = false;
    this._lengthCounter = 0;
  }

  public byte ReadWaveRam(int index) => this._waveRam[index & 0x0F];
  public void WriteWaveRam(int index, byte value) => this._waveRam[index & 0x0F] = value;

  public void Write(int reg, byte value) {
    switch (reg) {
      case 0:
        this._dacEnabled = (value & 0x80) != 0;
        if (!this._dacEnabled)
          this._enabled = false;
        break;
      case 1:
        this._lengthCounter = 256 - value;
        break;
      case 2:
        this._outputLevel = (value >> 5) & 0x03;
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
      this._lengthCounter = 256;
    this._timer = (2048 - this._frequency) * 2;
    this._position = 0;
  }

  public void StepTimer() {
    if (--this._timer > 0)
      return;
    this._timer = (2048 - this._frequency) * 2;
    this._position = (this._position + 1) & 31;
    var sample = this._waveRam[this._position >> 1];
    this._sampleBuffer = (this._position & 1) == 0 ? (sample >> 4) : (sample & 0x0F);
  }

  public void ClockLength() {
    if (!this._lengthEnabled || this._lengthCounter == 0)
      return;
    if (--this._lengthCounter == 0)
      this._enabled = false;
  }

  /// <summary>Current DAC output level 0..15, or -1 when the DAC is off (silent).</summary>
  public int Output() {
    if (!this._enabled || !this._dacEnabled)
      return -1;
    if (this._outputLevel == 0)
      return 0; // muted output level: DAC is on but the sample is shifted to zero
    return this._sampleBuffer >> (this._outputLevel - 1);
  }
}
