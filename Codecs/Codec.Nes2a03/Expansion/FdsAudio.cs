#pragma warning disable CS1591
namespace Codec.Nes2a03.Expansion;

/// <summary>
/// The Famicom Disk System (FDS) RP2C33 wavetable sound channel: one 64-step, 6-bit main wave
/// driven by a frequency modulator unit, each with its own volume/pitch envelope, plus a 2-bit
/// master volume.
/// <list type="bullet">
///   <item><b>$4040-$407F</b>: 64 entries of 6-bit (0..63) main wave RAM (writable only while the
///     wave write-enable bit of <c>$4089</c> is set).</item>
///   <item><b>$4080</b>: volume envelope — bit 7 disable (use gain directly), bit 6 direction,
///     bits 0-5 speed/gain. <b>$4084</b>: identical for the modulator envelope.</item>
///   <item><b>$4082/$4083</b>: 12-bit main pitch; <c>$4083</c> bit 7 halts the wave, bit 6
///     disables the volume envelope.</item>
///   <item><b>$4085</b>: writes the 7-bit signed modulation counter directly.</item>
///   <item><b>$4086/$4087</b>: 12-bit modulator pitch; <c>$4087</c> bit 7 halts the modulator.</item>
///   <item><b>$4088</b>: writes a 3-bit modulation-table entry at the current modulator position
///     (only while the modulator is halted), advancing the position.</item>
///   <item><b>$4089</b>: bits 0-1 master volume (×2/2, 2/3, 2/4, 2/5), bit 7 wave write-enable.</item>
///   <item><b>$408A</b>: master envelope speed multiplier.</item>
/// </list>
/// <para>The modulator phase, the BIAS table (0,+1,+2,+4,reset,-4,-2,-1), the pitch-modulation
/// integer formula and the master-volume divisors are ported from NSFPlay
/// (<c>xgm/devices/Sound/nes_fds.cpp</c>, Brad Smith / Brezza). The 22-bit phase accumulators are
/// indexed by their top 6 bits. Output is pre-scaled into the 2A03 mixer's ~0..1 domain.</para>
/// </summary>
internal sealed class FdsAudio : IExpansionAudio {

  private const int Twav = 0;
  private const int Tmod = 1;

  private readonly int[] _waveMain = new int[64];   // 0..63
  private readonly int[] _waveMod = new int[64];    // 0..7

  private readonly uint[] _phase = new uint[2];     // 22-bit phase accumulators
  private readonly int[] _freq = new int[2];        // 12-bit pitches (main, mod)

  private bool _waveHalt;
  private bool _modHalt;
  private bool _envDisableMain;                     // $4083 bit 6
  private bool _waveWriteEnable;                    // $4089 bit 7

  private int _modCounter;                          // mod_pos, 7-bit signed-coded (0..127)
  private int _masterVol;                           // 0..3

  // Envelopes: speed/direction/disable for the volume (EVOL) and modulator (EMOD) gains.
  private readonly bool[] _envDisable = new bool[2];
  private readonly bool[] _envUp = new bool[2];
  private readonly int[] _envSpeed = new int[2];
  private readonly int[] _envGain = new int[2];     // direct gain when envelope disabled
  private readonly int[] _envOut = new int[2];      // 0..32 active level
  private readonly int[] _envTimer = new int[2];
  private int _masterEnvSpeed = 0xFF;               // $408A

  private const int Evol = 0;
  private const int Emod = 1;

  public FdsAudio() {
    this._envOut[Evol] = 32;
    this._envOut[Emod] = 32;
  }

  public bool HandlesWrite(ushort addr) => addr is >= 0x4040 and <= 0x408A;

  public void Write(ushort addr, byte value) {
    if (addr is >= 0x4040 and <= 0x407F) {
      if (this._waveWriteEnable)
        this._waveMain[addr - 0x4040] = value & 0x3F;
      return;
    }

    switch (addr) {
      case 0x4080:
        this._envDisable[Evol] = (value & 0x80) != 0;
        this._envUp[Evol] = (value & 0x40) != 0;
        this._envSpeed[Evol] = value & 0x3F;
        this._envGain[Evol] = value & 0x3F;
        if (this._envDisable[Evol])
          this._envOut[Evol] = Math.Min(32, this._envGain[Evol]);
        this._envTimer[Evol] = 0;
        break;
      case 0x4082:
        this._freq[Twav] = (this._freq[Twav] & 0xF00) | value;
        break;
      case 0x4083:
        this._freq[Twav] = (this._freq[Twav] & 0x0FF) | ((value & 0x0F) << 8);
        this._waveHalt = (value & 0x80) != 0;
        this._envDisableMain = (value & 0x40) != 0;
        if (this._waveHalt)
          this._phase[Twav] = 0;
        break;
      case 0x4084:
        this._envDisable[Emod] = (value & 0x80) != 0;
        this._envUp[Emod] = (value & 0x40) != 0;
        this._envSpeed[Emod] = value & 0x3F;
        this._envGain[Emod] = value & 0x3F;
        if (this._envDisable[Emod])
          this._envOut[Emod] = Math.Min(32, this._envGain[Emod]);
        this._envTimer[Emod] = 0;
        break;
      case 0x4085:
        // Writing the modulation counter directly (7-bit, stored in the 0..127 wrap domain).
        this._modCounter = value & 0x7F;
        break;
      case 0x4086:
        this._freq[Tmod] = (this._freq[Tmod] & 0xF00) | value;
        break;
      case 0x4087:
        this._freq[Tmod] = (this._freq[Tmod] & 0x0FF) | ((value & 0x0F) << 8);
        this._modHalt = (value & 0x80) != 0;
        break;
      case 0x4088:
        // While halted, each write records a 3-bit mod-table entry at the current position and
        // advances the modulator phase by one step (top-6-bit increment).
        if (this._modHalt) {
          this._waveMod[(this._phase[Tmod] >> 16) & 0x3F] = value & 0x07;
          this._phase[Tmod] = (this._phase[Tmod] + 0x010000) & 0x3FFFFF;
        }
        break;
      case 0x4089:
        this._masterVol = value & 0x03;
        this._waveWriteEnable = (value & 0x80) != 0;
        break;
      case 0x408A:
        this._masterEnvSpeed = value;
        break;
    }
  }

  public bool TryRead(ushort addr, out byte value) {
    if (addr is >= 0x4040 and <= 0x407F) {
      value = (byte)this._waveMain[addr - 0x4040];
      return true;
    }
    value = 0;
    return false;
  }

  public void ClockOneCpuCycle() {
    this.ClockEnvelope(Evol);
    this.ClockEnvelope(Emod);
    this.ClockModulator();
    this.ClockWave();
  }

  // ── envelopes ────────────────────────────────────────────────────────────────
  private void ClockEnvelope(int which) {
    if (this._envDisable[which] || this._waveHalt)
      return;
    // Period per NSFPlay: ((speed+1) * master_env_speed) << 3 CPU cycles per ±1 gain step.
    var period = ((this._envSpeed[which] + 1) * this._masterEnvSpeed) << 3;
    if (period <= 0)
      return;
    if (++this._envTimer[which] < period)
      return;
    this._envTimer[which] = 0;
    if (this._envUp[which]) {
      if (this._envOut[which] < 32)
        ++this._envOut[which];
    } else if (this._envOut[which] > 0) {
      --this._envOut[which];
    }
  }

  // ── modulator ────────────────────────────────────────────────────────────────
  private static readonly int[] Bias = [0, 1, 2, 4, 0, -4, -2, -1];

  private void ClockModulator() {
    if (this._modHalt || this._freq[Tmod] == 0)
      return;
    // Accumulate without masking first so a position crossing at the 22-bit wrap boundary is not
    // lost; the table index is masked to 6 bits per read, the phase masked back afterwards.
    var start = this._phase[Tmod] >> 16;
    var raw = this._phase[Tmod] + (uint)this._freq[Tmod];
    var end = raw >> 16;
    for (var p = start; p < end; ++p) {
      var wv = this._waveMod[p & 0x3F];
      if (wv == 4)
        this._modCounter = 0;
      else
        this._modCounter = (this._modCounter + Bias[wv]) & 0x7F;
    }
    this._phase[Tmod] = raw & 0x3FFFFF;
  }

  /// <summary>
  /// The NSFPlay pitch-modulation integer formula: signed counter × modulator-envelope gain,
  /// asymmetric rounding, range-wrap to [-64,192), then folded against the main pitch (×pitch,
  /// ÷64 with round-half-up). Returns the signed frequency offset added to the wave pitch.
  /// </summary>
  private int ModulationOffset() {
    var pos = this._modCounter < 64 ? this._modCounter : this._modCounter - 128;
    var temp = pos * this._envOut[Emod];
    var rem = temp & 0x0F;
    temp >>= 4;
    if (rem > 0 && (temp & 0x80) == 0)
      temp += pos < 0 ? -1 : 2;
    while (temp >= 192) temp -= 256;
    while (temp < -64) temp += 256;
    temp = this._freq[Twav] * temp;
    rem = temp & 0x3F;
    temp >>= 6;
    if (rem >= 32)
      temp += 1;
    return temp;
  }

  // ── wave ─────────────────────────────────────────────────────────────────────
  private void ClockWave() {
    if (this._waveHalt)
      return;
    var f = this._freq[Twav] + this.ModulationOffset();
    this._phase[Twav] = (uint)((this._phase[Twav] + (uint)f) & 0x3FFFFF);
  }

  // Master volume divisors: ×2/2, 2/3, 2/4, 2/5 (NSFPlay MASTER[]).
  private static readonly double[] MasterFactor = [2.0 / 2.0, 2.0 / 3.0, 2.0 / 4.0, 2.0 / 5.0];

  // Peak: wave 63 × env 32 × master 1.0 = 2016. NSFPlay's FDS sits a little hotter than a single
  // 2A03 pulse; ~0.55 of full scale at peak per its relative master.
  private const float MixScale = 0.55f / 2016.0f;

  public float Output() {
    var vol = this._envDisableMain
      ? Math.Min(32, this._envGain[Evol])
      : this._envOut[Evol];
    var sample = this._waveMain[(this._phase[Twav] >> 16) & 0x3F] * vol;
    return (float)(sample * MasterFactor[this._masterVol] * MixScale);
  }

  // ── test hooks ───────────────────────────────────────────────────────────────
  internal int ModCounter => this._modCounter;
  internal int ModulationOffsetForTest() => this.ModulationOffset();
  internal uint WavePhase => this._phase[Twav];
  internal int WaveIndex => (int)((this._phase[Twav] >> 16) & 0x3F);
}
