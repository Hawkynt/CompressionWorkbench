#pragma warning disable CS1591
namespace Codec.Nes2a03.Expansion;

/// <summary>
/// The Famicom Disk System (RP2C33) expansion sound: one 64-step, 6-bit wavetable channel whose
/// pitch is bent by a modulation unit (a 32-entry table of 3-bit counter steps), with a volume
/// envelope, a modulation-depth envelope and a 2-bit master volume.
/// <para>Written from <c>docs/FDS-EXPANSION-SOUND-SPEC.md</c> and the NESdev wiki page
/// <i>FDS audio</i> (revision 22987, September 2025), which is the hardware reference that
/// specification defers to. Where the two disagree the wiki's later hardware findings are used;
/// the specification's §9 lists each such decision.</para>
/// <list type="bullet">
///   <item>The wave and modulation units are ticked every 16 CPU cycles. On each tick the
///     modulated pitch (20 bits) is added to a 24-bit wave accumulator whose top 6 bits select
///     the wave RAM entry, and the 12-bit modulation pitch is added to an 18-bit modulation
///     accumulator whose bits 17..13 select the table entry (bit 12 is the "ghost" bit, so every
///     entry is applied on two consecutive steps).</item>
///   <item>The envelopes tick every <c>8 × (speed + 1) × (master + 1)</c> CPU cycles while the
///     wave runs, the envelopes are not halted and the master envelope speed is non-zero.</item>
///   <item>The volume gain reaching the output is latched while the wave position is 0 (the
///     PWM comparator latch) and clamped to 32; a direct write of gain 0 mutes at once.</item>
/// </list>
/// </summary>
internal sealed class FdsAudio : IExpansionAudio {

  /// <summary>CPU cycles between two ticks of the wave and modulation units.</summary>
  private const int UnitTickCycles = 16;

  private const uint WaveAccumulatorMask = 0xFF_FFFF;   // 24 bits
  private const int WavePositionShift = 18;             // bits 23..18 = wave RAM index
  private const int ModAccumulatorMask = 0x3_FFFF;      // 18 bits
  private const int ModFractionMask = 0x0FFF;           // carry out of bit 11 steps the table
  private const int ModStep = 0x1000;                   // one 6-bit position step (incl. ghost bit)
  private const int ModTableIndexShift = 13;            // bits 17..13 = table index

  /// <summary>Envelope gains stop rising here; the volume gain is clamped here at the output.</summary>
  private const int GainCeiling = 32;

  /// <summary>Power-on master envelope speed as initialised by the FDS BIOS.</summary>
  private const int BiosMasterEnvelopeSpeed = 0xE8;

  /// <summary>Mod-table entry → mod-counter adjustment; entry 4 resets the counter instead.</summary>
  private static readonly int[] ModCounterSteps = [0, 1, 2, 4, 0, -4, -2, -1];
  private const int ModResetEntry = 4;

  /// <summary>
  /// Output scale: the chip peak (<c>63 × 32 × 1</c> = 2016) maps to ≈0.55 of the 2A03 mixer's
  /// full scale, the level the player has always used (specification §6). The NESdev wiki's
  /// measurement (FDS peak ≈ 2.4 × one APU square at full volume, ≈0.36) is recorded there as an
  /// alternative; the established ratio is kept so existing renders do not change loudness.
  /// </summary>
  private const float PeakMixerLevel = 0.55f;
  private const int PeakLevel = 63 * GainCeiling;

  /// <summary><c>$4089</c> bits 1..0: 2/2, 2/3, 2/4, 2/5, pre-multiplied by the output scale.</summary>
  private static readonly float[] MasterVolumeScale = [
    PeakMixerLevel / PeakLevel * (2f / 2f),
    PeakMixerLevel / PeakLevel * (2f / 3f),
    PeakMixerLevel / PeakLevel * (2f / 4f),
    PeakMixerLevel / PeakLevel * (2f / 5f),
  ];

  /// <summary>Volume or modulation envelope: one 6-bit gain driven by a cycle timer.</summary>
  private struct Envelope {
    public bool Disabled;   // bit 7: envelope off, gain written directly
    public bool Increase;   // bit 6
    public int Speed;       // bits 5..0
    public int Gain;        // 0..63
    public int Timer;       // CPU cycles until the next tick

    public void Write(byte value, int masterSpeed) {
      this.Disabled = (value & 0x80) != 0;
      this.Increase = (value & 0x40) != 0;
      this.Speed = value & 0x3F;
      if (this.Disabled)
        this.Gain = value & 0x3F;
      this.ResetTimer(masterSpeed);
    }

    public void ResetTimer(int masterSpeed) => this.Timer = 8 * (this.Speed + 1) * (masterSpeed + 1);

    public void Clock(int masterSpeed) {
      if (this.Disabled || --this.Timer > 0)
        return;

      this.ResetTimer(masterSpeed);
      if (this.Increase) {
        if (this.Gain < GainCeiling)
          ++this.Gain;
      } else if (this.Gain > 0)
        --this.Gain;
    }
  }

  private readonly byte[] _waveRam = new byte[64];
  private readonly byte[] _modTable = new byte[32];

  private Envelope _volume;
  private Envelope _modDepth;
  private int _masterEnvelopeSpeed = BiosMasterEnvelopeSpeed;
  private int _masterVolume;

  private int _wavePitch;          // 12-bit, $4082/$4083
  private uint _waveAccumulator;   // 24-bit
  private bool _waveHalted;        // $4083 bit 7
  private bool _envelopesHalted;   // $4083 bit 6

  private int _modPitch;           // 12-bit, $4086/$4087
  private int _modAccumulator;     // 18-bit
  private int _modCounter;         // signed 7-bit, −64..63
  private bool _modHalted;         // $4087 bit 7
  private bool _modForceCarry;     // $4087 bit 6

  private bool _waveWriteEnabled;  // $4089 bit 7
  private float _heldOutput;       // output frozen while wave RAM is write-enabled
  private int _latchedVolume;      // volume gain seen by the output, clamped to 32
  private int _tickDivider;

  public bool HandlesWrite(ushort addr) => addr is >= 0x4040 and <= 0x408A;

  public void Write(ushort addr, byte value) {
    if (addr <= 0x407F) {
      if (this._waveWriteEnabled)
        this._waveRam[addr - 0x4040] = (byte)(value & 0x3F);
      return;
    }

    switch (addr) {
      case 0x4080:
        this._volume.Write(value, this._masterEnvelopeSpeed);
        if (this._volume.Disabled && this._volume.Gain == 0)
          this._latchedVolume = 0;   // muting takes effect immediately
        break;
      case 0x4082:
        this._wavePitch = (this._wavePitch & 0xF00) | value;
        break;
      case 0x4083:
        this._wavePitch = (this._wavePitch & 0x0FF) | ((value & 0x0F) << 8);
        this._waveHalted = (value & 0x80) != 0;
        if (this._waveHalted)
          this._waveAccumulator = 0;
        this._envelopesHalted = (value & 0x40) != 0;
        if (this._envelopesHalted) {
          this._volume.ResetTimer(this._masterEnvelopeSpeed);
          this._modDepth.ResetTimer(this._masterEnvelopeSpeed);
        }
        break;
      case 0x4084:
        this._modDepth.Write(value, this._masterEnvelopeSpeed);
        break;
      case 0x4085:
        this._modCounter = SignExtend7(value);
        break;
      case 0x4086:
        this._modPitch = (this._modPitch & 0xF00) | value;
        break;
      case 0x4087:
        this._modPitch = (this._modPitch & 0x0FF) | ((value & 0x0F) << 8);
        this._modForceCarry = (value & 0x40) != 0;
        this._modHalted = (value & 0x80) != 0;
        if (this._modHalted)
          this._modAccumulator &= ~ModFractionMask;   // the table position is kept
        break;
      case 0x4088:
        if (!this._modHalted)
          break;
        // One write fills the 32-entry slot at the current position, i.e. two 6-bit positions.
        this._modTable[this._modAccumulator >> ModTableIndexShift] = (byte)(value & 0x07);
        this._modAccumulator = (this._modAccumulator + 2 * ModStep) & ModAccumulatorMask;
        break;
      case 0x4089:
        var enable = (value & 0x80) != 0;
        if (enable && !this._waveWriteEnabled)
          this._heldOutput = this.LiveOutput();
        this._waveWriteEnabled = enable;
        this._masterVolume = value & 0x03;
        break;
      case 0x408A:
        this._masterEnvelopeSpeed = value;
        this._volume.ResetTimer(value);
        this._modDepth.ResetTimer(value);
        break;
    }
  }

  public bool TryRead(ushort addr, out byte value) {
    if (addr is >= 0x4040 and <= 0x407F) {
      // Write-protected RAM reads back the entry at the current wave position; bits 7..6 are
      // open bus (the $40 address high byte).
      var index = this._waveWriteEnabled ? addr - 0x4040 : this.WaveIndex;
      value = (byte)(0x40 | this._waveRam[index]);
      return true;
    }
    value = 0;
    return false;
  }

  public void ClockOneCpuCycle() {
    if (!this._waveHalted && !this._envelopesHalted && this._masterEnvelopeSpeed != 0) {
      this._volume.Clock(this._masterEnvelopeSpeed);
      this._modDepth.Clock(this._masterEnvelopeSpeed);
    }

    if (++this._tickDivider < UnitTickCycles)
      return;

    this._tickDivider = 0;
    this.TickUnits();
  }

  private void TickUnits() {
    if (!this._waveHalted)
      this._waveAccumulator = (this._waveAccumulator + (uint)this.ModulatedPitch()) & WaveAccumulatorMask;

    if (this.WaveIndex == 0)
      this._latchedVolume = Math.Min(this._volume.Gain, GainCeiling);

    if (this._modHalted)
      return;

    var carry = (this._modAccumulator & ModFractionMask) + this._modPitch > ModFractionMask;
    var next = this._modAccumulator + this._modPitch;
    if (this._modForceCarry && !carry) {
      carry = true;
      next += ModStep;
    }

    if (carry) {
      var entry = this._modTable[this._modAccumulator >> ModTableIndexShift];
      this._modCounter = entry == ModResetEntry ? 0 : SignExtend7(this._modCounter + ModCounterSteps[entry]);
    }

    this._modAccumulator = next & ModAccumulatorMask;
  }

  /// <summary>
  /// The wave pitch bent by the modulation unit (NESdev pseudocode): counter × gain, rounded
  /// up when positive with a non-zero low nibble, re-centred on 64, then multiplied by the
  /// 12-bit pitch. 64 means "unmodulated"; the result is a 20-bit accumulator increment.
  /// </summary>
  private int ModulatedPitch() {
    var temp = this._modCounter * this._modDepth.Gain;
    if ((temp & 0x0F) != 0 && (temp & 0x800) == 0)
      temp += 0x20;
    temp = ((temp + 0x400) >> 4) & 0xFF;
    return (this._wavePitch * temp) & 0xF_FFFF;
  }

  private float LiveOutput()
    => this._waveRam[this.WaveIndex] * this._latchedVolume * MasterVolumeScale[this._masterVolume];

  public float Output() => this._waveWriteEnabled ? this._heldOutput : this.LiveOutput();

  private static int SignExtend7(int value) => ((value & 0x7F) ^ 0x40) - 0x40;

  // ── test hooks ───────────────────────────────────────────────────────────────

  /// <summary>Mod counter as a signed value (−64..63).</summary>
  internal int ModCounter => this._modCounter;

  /// <summary>Pitch offset of the modulation, in 12-bit pitch units (floor of the exact 1/64 step).</summary>
  internal int ModulationOffsetForTest() => (this.ModulatedPitch() - (this._wavePitch << 6)) >> 6;

  /// <summary>The 20-bit wave accumulator increment for the current pitch, counter and gain.</summary>
  internal int ModulatedPitchForTest() => this.ModulatedPitch();

  /// <summary>The 24-bit wave accumulator.</summary>
  internal uint WavePhase => this._waveAccumulator;

  /// <summary>The wave RAM entry currently selected (accumulator bits 23..18).</summary>
  internal int WaveIndex => (int)(this._waveAccumulator >> WavePositionShift);

  /// <summary>The 18-bit modulation accumulator.</summary>
  internal int ModPhase => this._modAccumulator;

  /// <summary>The modulation table position including the ghost bit (0..63).</summary>
  internal int ModTablePosition => this._modAccumulator >> 12;

  /// <summary>The 3-bit entry stored in table slot <paramref name="slot"/> (0..31).</summary>
  internal int ModTableEntry(int slot) => this._modTable[slot];

  internal int VolumeGain => this._volume.Gain;
  internal int ModGain => this._modDepth.Gain;
  internal int LatchedVolume => this._latchedVolume;
}
