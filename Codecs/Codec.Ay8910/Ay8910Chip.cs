#pragma warning disable CS1591
namespace Codec.Ay8910;

/// <summary>
/// A General Instrument AY-3-8910 / Yamaha YM2149 (PSG) synthesis core. The chip carries
/// three square-wave tone channels, one noise generator and one hardware envelope
/// generator, all programmed through 16 registers:
/// <list type="bullet">
///   <item>R0-R5 — the three 12-bit tone periods (fine + coarse). The channel toggles every
///     <c>period</c> steps of the clock/16 prescaler, so the tone frequency is
///     <c>clock / (16 * period)</c> (a period of 0 behaves as 1).</item>
///   <item>R6 — the 5-bit noise period; the noise generator runs a 17-bit LFSR clocked at
///     <c>clock / (16 * period)</c>.</item>
///   <item>R7 — the mixer: bits 0-2 disable tone A/B/C, bits 3-5 disable noise A/B/C
///     (a set bit DISABLES — active-low). Bits 6-7 are the I/O port directions and are
///     ignored here.</item>
///   <item>R8-R10 — the per-channel amplitude: bits 0-3 a fixed 4-bit level, bit 4 selects
///     the hardware envelope instead.</item>
///   <item>R11-R12 — the 16-bit envelope period; the envelope steps at
///     <c>clock / (256 * period)</c>.</item>
///   <item>R13 — the envelope shape (continue/attack/alternate/hold), giving the ten
///     documented shapes.</item>
/// </list>
/// <para>The 4-bit fixed levels and the 5-bit envelope levels both index a logarithmic DAC
/// table where each step is roughly 1.41× (≈ +3 dB / 2 in amplitude) the previous one — the
/// documented AY/YM volume curve. The two tables are derived from the same normalised curve.</para>
/// <para><see cref="RenderSamples"/> emits interleaved 16-bit stereo at 44.1 kHz. The default
/// panning is the ZX-Spectrum "ABC" layout (channel A → left, B → centre, C → right); pass a
/// different <see cref="StereoMode"/> to the constructor for mono or the "ACB" variant.</para>
/// </summary>
public sealed class Ay8910Chip {

  /// <summary>Output sample rate of <see cref="RenderSamples"/>.</summary>
  public const int OutputSampleRate = 44100;

  /// <summary>Common PSG input clocks.</summary>
  public const double ZxSpectrumClock = 1_773_400.0; // ZX Spectrum 128 AY clock
  /// <summary>
  /// Defines the msx clock constant value.
  /// </summary>
public const double MsxClock = 1_789_772.5;         // MSX PSG clock

  /// <summary>Stereo panning layouts.</summary>
  public enum StereoMode {
    /// <summary>All channels centred (mono duplicated to both speakers).</summary>
    Mono,
    /// <summary>A→left, B→centre, C→right (the ZX-Spectrum default).</summary>
    Abc,
    /// <summary>A→left, C→centre, B→right.</summary>
    Acb,
  }

  // 16-level (4-bit) logarithmic DAC table, normalised 0..1. Each non-zero step is ~1.41× the
  // previous one (the documented AY curve); MAME's measured AY-3-8910 table is used.
  private static readonly double[] FixedLevels = [
    0.0000, 0.0137, 0.0205, 0.0291, 0.0423, 0.0618, 0.0847, 0.1369,
    0.1691, 0.2647, 0.3527, 0.4499, 0.5704, 0.6873, 0.8482, 1.0000,
  ];

  // 32-level (envelope) table: the YM2149 drives the envelope DAC with 5-bit resolution. The
  // even entries coincide with the 4-bit fixed table; odd entries interpolate logarithmically.
  private static readonly double[] EnvLevels = BuildEnvLevels();

  private static double[] BuildEnvLevels() {
    var table = new double[32];
    for (var i = 0; i < 32; ++i)
      table[i] = i == 0 ? 0.0 : Math.Pow(10.0, (i - 31) * 1.5 / 20.0);
    table[0] = 0.0;
    return table;
  }

  /// <summary>The 4-bit fixed-volume DAC curve (normalised 0..1).</summary>
  public static IReadOnlyList<double> VolumeTable => FixedLevels;

  /// <summary>The 5-bit envelope DAC curve (normalised 0..1).</summary>
  public static IReadOnlyList<double> EnvelopeTable => EnvLevels;

  private readonly double _clock;
  private readonly StereoMode _stereo;
  private readonly double _cyclesPerSample;
  private double _cycleAccumulator;

  private readonly byte[] _registers = new byte[16];

  // Tone generators.
  private readonly int[] _tonePeriod = new int[3];
  private readonly int[] _toneCounter = new int[3];
  private readonly int[] _toneOutput = new int[3]; // 0/1

  // Noise generator.
  private int _noisePeriod;
  private int _noiseCounter;
  private int _noiseShift = 1;
  private int _noiseOutput; // 0/1

  // Envelope generator.
  private int _envPeriod;
  private int _envCounter;
  private int _envStep;        // 0..31 position within the current ramp
  private int _envVolume;      // current 0..31 level
  private bool _envHolding;
  // Shape decode (R13).
  private bool _envContinue, _envAttack, _envAlternate, _envHold;

  /// <summary>
  /// Initializes a new instance of <see cref="Ay8910Chip"/>.
  /// </summary>
public Ay8910Chip(double clock = ZxSpectrumClock, StereoMode stereo = StereoMode.Abc) {
    this._clock = clock;
    this._stereo = stereo;
    // Generators run at the clock/16 prescaler rate.
    this._cyclesPerSample = clock / 16.0 / OutputSampleRate;
  }

  /// <summary>Reads back a register (0..15); registers above 13 read as last-written.</summary>
  public byte ReadReg(int reg) => this._registers[reg & 0x0F];

  /// <summary>Writes one PSG register (<paramref name="reg"/> 0..15).</summary>
  public void WriteReg(int reg, byte value) {
    reg &= 0x0F;
    this._registers[reg] = value;
    switch (reg) {
      case 0: case 1: this._tonePeriod[0] = this.Period12(0); break;
      case 2: case 3: this._tonePeriod[1] = this.Period12(2); break;
      case 4: case 5: this._tonePeriod[2] = this.Period12(4); break;
      case 6: this._noisePeriod = value & 0x1F; break;
      case 11: case 12: this._envPeriod = this._registers[11] | (this._registers[12] << 8); break;
      case 13: this.LatchEnvelopeShape(value & 0x0F); break;
    }
  }

  private int Period12(int fineReg) {
    var period = this._registers[fineReg] | ((this._registers[fineReg + 1] & 0x0F) << 8);
    return period == 0 ? 1 : period;
  }

  private void LatchEnvelopeShape(int shape) {
    this._envContinue = (shape & 0x08) != 0;
    this._envAttack = (shape & 0x04) != 0;
    this._envAlternate = (shape & 0x02) != 0;
    this._envHold = (shape & 0x01) != 0;
    // A write to R13 always restarts the envelope from the top of the ramp.
    this._envStep = 0;
    this._envHolding = false;
    this._envCounter = 0;
    this._envVolume = this._envAttack ? 0 : 31;
  }

  /// <summary>
  /// Renders <paramref name="count"/> interleaved stereo frames (left, right) into
  /// <paramref name="buffer"/> at <see cref="OutputSampleRate"/>. The buffer must hold at
  /// least <c>2 * count</c> samples.
  /// </summary>
  public void RenderSamples(Span<short> buffer, int count) {
    for (var i = 0; i < count; ++i) {
      this._cycleAccumulator += this._cyclesPerSample;
      var steps = (int)this._cycleAccumulator;
      this._cycleAccumulator -= steps;
      for (var s = 0; s < steps; ++s)
        this.StepOneCycle();

      var (left, right) = this.Mix();
      buffer[i * 2] = left;
      buffer[i * 2 + 1] = right;
    }
  }

  /// <summary>
  /// Advances the generators by one clock/16 prescaler tick. Exposed for hosts (e.g. the Sunsoft
  /// 5B expansion in an NSF player) that drive the PSG from their own master clock rather than
  /// the built-in <see cref="RenderSamples"/> resampler.
  /// </summary>
  public void StepPrescaler() => this.StepOneCycle();

  /// <summary>
  /// The current mono output as the linear sum of the three channels' DAC levels, each in the
  /// normalised 0..1 range (so the full-scale sum is 0..3). Hosts that mix the PSG against other
  /// chips at a documented relative level use this instead of the stereo <see cref="Mix"/>.
  /// </summary>
  public double MixMonoLinear() {
    var mixer = this._registers[7];
    var sum = 0.0;
    for (var ch = 0; ch < 3; ++ch) {
      var toneEnabled = (mixer & (1 << ch)) == 0;
      var noiseEnabled = (mixer & (1 << (ch + 3))) == 0;
      var tone = !toneEnabled || this._toneOutput[ch] != 0;
      var noise = !noiseEnabled || this._noiseOutput != 0;
      if (!(tone && noise))
        continue;
      var ampReg = this._registers[8 + ch];
      sum += (ampReg & 0x10) != 0 ? EnvLevels[this._envVolume] : FixedLevels[ampReg & 0x0F];
    }
    return sum;
  }

  private void StepOneCycle() {
    for (var ch = 0; ch < 3; ++ch) {
      if (--this._toneCounter[ch] > 0)
        continue;
      this._toneCounter[ch] = this._tonePeriod[ch];
      this._toneOutput[ch] ^= 1;
    }

    if (--this._noiseCounter <= 0) {
      this._noiseCounter = this._noisePeriod == 0 ? 1 : this._noisePeriod;
      // 17-bit LFSR, taps at bits 0 and 3 (the documented AY noise polynomial).
      var feedback = (this._noiseShift ^ (this._noiseShift >> 3)) & 1;
      this._noiseShift = (this._noiseShift >> 1) | (feedback << 16);
      this._noiseOutput = this._noiseShift & 1;
    }

    this.StepEnvelope();
  }

  private void StepEnvelope() {
    if (this._envHolding)
      return;
    var period = this._envPeriod == 0 ? 1 : this._envPeriod;
    if (--this._envCounter > 0)
      return;
    this._envCounter = period;
    this.AdvanceEnvelope();
  }

  private void AdvanceEnvelope() {
    ++this._envStep;
    if (this._envStep > 31) {
      // One full 32-step ramp complete; decide what the shape does next.
      if (!this._envContinue) {
        // Shapes 0-7 with continue=0: one decay (or attack) then hold at 0.
        this._envHolding = true;
        this._envVolume = 0;
        return;
      }
      this._envStep = 0;
      if (this._envHold) {
        this._envHolding = true;
        // Hold at the level reached: alternate decides the held value.
        if (this._envAlternate)
          this._envAttack = !this._envAttack;
        this._envVolume = this._envAttack ? 0 : 31;
        return;
      }
      if (this._envAlternate)
        this._envAttack = !this._envAttack;
    }
    this._envVolume = this._envAttack ? this._envStep : 31 - this._envStep;
  }

  private (short Left, short Right) Mix() {
    var mixer = this._registers[7];
    var chans = new double[3];
    for (var ch = 0; ch < 3; ++ch) {
      var toneEnabled = (mixer & (1 << ch)) == 0;
      var noiseEnabled = (mixer & (1 << (ch + 3))) == 0;
      var tone = !toneEnabled || this._toneOutput[ch] != 0;
      var noise = !noiseEnabled || this._noiseOutput != 0;
      var on = tone && noise;

      var ampReg = this._registers[8 + ch];
      var level = (ampReg & 0x10) != 0
        ? EnvLevels[this._envVolume]
        : FixedLevels[ampReg & 0x0F];
      chans[ch] = on ? level : 0.0;
    }

    double left, right;
    switch (this._stereo) {
      case StereoMode.Abc:
        left = chans[0] + chans[1] * 0.5;
        right = chans[2] + chans[1] * 0.5;
        break;
      case StereoMode.Acb:
        left = chans[0] + chans[2] * 0.5;
        right = chans[1] + chans[2] * 0.5;
        break;
      default: // Mono
        left = right = (chans[0] + chans[1] + chans[2]) / 3.0;
        break;
    }

    return (Scale(left), Scale(right));
  }

  // Each channel can contribute up to full scale; scale by ~0.46 to keep two summed channels
  // (the centre channel adds to both sides) inside 16-bit headroom.
  private static short Scale(double value) {
    var v = value * 0.46 * short.MaxValue;
    return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
  }
}
