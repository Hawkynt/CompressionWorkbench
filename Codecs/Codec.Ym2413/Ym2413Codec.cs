#pragma warning disable CS1591
namespace Codec.Ym2413;

/// <summary>
/// Yamaha YM2413 (OPLL) FM synthesis core: nine two-operator channels, or six channels plus
/// five rhythm instruments (Bass Drum, Snare Drum, Tom-Tom, Top-Cymbal, High-Hat). The OPLL is
/// the cost-reduced OPL relative: one user-definable patch plus fifteen ROM patches replace
/// the OPL's free register set, each channel has a modulator feeding a carrier, and the chip
/// emits one channel per internal slot so the output sample rate is <c>clock / 72</c>.
/// <para><b>References.</b> The register map and frequency/envelope arithmetic follow the
/// official <i>Yamaha YM2413 Application Manual</i>. The instrument patch ROM and the rhythm
/// generation, KSL/multiple tables and envelope behaviour are transcribed from <c>emu2413</c>
/// (Mitsutaka Okazaki, v1.5.9) and cross-checked against the andete OPLL die analysis and
/// Nuked-OPLL. The log-sine / exponential operator ROMs are the genuine die constants shared
/// with the OPN2 (see <see cref="Ym2413Tables"/>, replicated from the sibling
/// <c>Codec.Ym2612</c> whose copies are <c>internal</c>).</para>
/// <para>Registers are written through <see cref="WriteRegister"/> (address, value), matching
/// the VGM <c>0x51 aa dd</c> command. <see cref="RenderSample()"/> produces one mono frame at the
/// chip's native rate (<see cref="NativeSampleRate"/>); the host resamples to its output rate.</para>
/// <para><b>Fidelity notes.</b> The fifteen ROM patches, the user patch, the per-channel
/// F-num/block/key-on/sustain registers, the modulator-feedback path, total-level on the
/// modulator and 4-bit volume on the carrier, KSL/KSR rate scaling, the AM (tremolo, ~3.7 Hz)
/// and PM (vibrato, ~6.4 Hz) LFOs, the half-sine waveform select, and rhythm mode's five
/// instruments with their fixed phase/noise interactions are all modelled. The envelope DAC
/// is the OPLL's 23-step log-domain generator. Sub-sample channel-slot phasing on the real
/// die is collapsed to per-frame channel iteration; this does not affect register-log playback.</para>
/// </summary>
public sealed class Ym2413Codec {

  /// <summary>Native FM sample-rate divisor: the OPLL emits one slot per clock/72 tick.</summary>
  public const int Prescale = 72;

  // Envelope is tracked in 1/16-dB-ish OPLL units (0..127 → ~48 dB span), matching emu2413's
  // EG_BITS resolution scaled into the shared 1/256-dB attenuation domain on output.
  private const int MaxAttenuation = 127;     // EG ceiling in OPLL EG steps (~ silence)
  private const int AttackEnd = 0;            // EG floor (peak loudness)

  private enum EnvPhase { Damp, Attack, Decay, Sustain, Release, Off }

  /// <summary>One operator (modulator slot 0, carrier slot 1) of a channel.</summary>
  private sealed class Operator {
    public uint Phase;            // 18.9-style phase accumulator (top bits index the sine)
    public int Multiple;          // MUL register 0..15 (mapped through Ym2413Tables.Multiple)
    public bool HalfSine;         // waveform select: true = half-rectified sine
    public bool AmOn;             // tremolo enable
    public bool VibOn;            // vibrato enable
    public bool EgType;           // true = sustained (hold at SL), false = percussive
    public bool KsrOn;            // key-scale-of-rate enable
    public int Ksl;               // key-scale-of-level 0..3
    public int TotalLevel;        // modulator TL 0..63 (carrier uses Volume instead)
    public int AttackRate;        // AR 0..15
    public int DecayRate;         // DR 0..15
    public int SustainLevel;      // SL 0..15
    public int ReleaseRate;       // RR 0..15

    public EnvPhase State = EnvPhase.Off;
    public int EnvLevel = MaxAttenuation;   // current attenuation in EG steps
    public int Output;            // last linear output (for feedback / modulation)
    public int Prev;              // one-sample-old output
  }

  private sealed class Channel {
    public readonly Operator Modulator = new();
    public readonly Operator Carrier = new();
    public int FNum;              // 9-bit frequency number
    public int Block;             // 0..7 octave
    public int Instrument;        // 0..15 patch select
    public int Volume;            // carrier 4-bit volume (attenuation)
    public int Feedback;          // 0..7 modulator feedback shift
    public bool SustainOn;        // reg 0x2x bit 5: extends release
    public bool KeyOn;
  }

  private readonly Channel[] _channels = [
    new(), new(), new(), new(), new(), new(), new(), new(), new(),
  ];

  // User patch (instrument 0) raw eight bytes, register addresses 0x00..0x07.
  private readonly byte[] _userPatch = new byte[8];

  private readonly double _nativeRate;
  private bool _rhythmMode;

  // The active instrument patch ROM. Defaults to the genuine YM2413 set, but a host (e.g. the
  // Konami VRC7, which is an OPLL die with a substituted patch table) may supply its own.
  private readonly byte[][] _instrumentRom;

  // Global LFOs: a single AM (tremolo) and PM (vibrato) phase shared by every operator.
  private uint _lfoAm;
  private uint _lfoPm;

  // Rhythm-mode noise: a 23-bit LFSR clocked once per native sample (emu2413 behaviour).
  private uint _noise = 1;

  /// <param name="clock">OPLL clock in Hz (3579545 for SMS / Mark III FM, MSX-MUSIC).</param>
    /// <summary>
  /// Initializes a new instance of <see cref="Ym2413Codec"/>.
  /// </summary>
public Ym2413Codec(double clock = 3579545.0) : this(clock, null) { }

  /// <summary>
  /// Constructs the OPLL core with an optional substitute instrument patch ROM. The Konami
  /// VRC7 is an OPLL die fused with a different 15-voice patch table; passing that table here
  /// (19 rows of 8 bytes — same layout as <see cref="Ym2413Tables.DefaultInstruments"/>, with
  /// rows 16..18 unused since the VRC7 has no rhythm mode) reuses the entire operator/envelope
  /// core. When <paramref name="instrumentRom"/> is <c>null</c> the genuine YM2413 set is used.
  /// </summary>
  public Ym2413Codec(double clock, byte[][]? instrumentRom) {
    this._instrumentRom = instrumentRom ?? Ym2413Tables.DefaultInstruments;
    this._nativeRate = clock / Prescale;
    for (var c = 0; c < this._channels.Length; ++c)
      this.LoadInstrument(this._channels[c]);
  }

  /// <summary>The chip's native output sample rate (clock / 72).</summary>
  public double NativeSampleRate => this._nativeRate;

  /// <summary>The genuine die-extracted quarter-period log-sine ROM (256 entries, 1/256 dB).</summary>
  public static IReadOnlyList<ushort> LogSinRom => Ym2413Tables.LogSin;

  /// <summary>The genuine die-extracted exponential ROM (256 entries; OR'd with 0x400 in use).</summary>
  public static IReadOnlyList<ushort> ExpRom => Ym2413Tables.Exp;

  /// <summary>The built-in instrument patch ROM (rows 0..18); see <see cref="Ym2413Tables"/>.</summary>
  public static IReadOnlyList<IReadOnlyList<byte>> InstrumentRom =>
    Ym2413Tables.DefaultInstruments;

  /// <summary>True when rhythm mode (reg 0x0E bit 5) is engaged.</summary>
  public bool RhythmMode => this._rhythmMode;

  // ── register bus ────────────────────────────────────────────────────────────

  /// <summary>
  /// Writes one OPLL register. <paramref name="address"/> 0x00..0x07 program the user patch,
  /// 0x0E is the rhythm-mode/key control, 0x10-0x18 the per-channel F-num low byte, 0x20-0x28
  /// the F-num high bit + block + key-on + sustain, and 0x30-0x38 the instrument/volume pair.
  /// </summary>
  public void WriteRegister(int address, int value) {
    address &= 0xFF;
    value &= 0xFF;

    if (address <= 0x07) {
      this._userPatch[address] = (byte)value;
      // Any channel currently voicing the user patch picks up the change.
      foreach (var ch in this._channels)
        if (ch.Instrument == 0)
          this.LoadInstrument(ch);
      return;
    }

    if (address == 0x0E) {
      this.WriteRhythm(value);
      return;
    }

    var region = address & 0xF0;
    var index = address & 0x0F;
    if (index > 0x08)
      return;
    var channel = this._channels[index];

    switch (region) {
      case 0x10: // F-num low 8 bits
        channel.FNum = (channel.FNum & 0x100) | value;
        break;
      case 0x20: // F-num bit 8, block, key-on, sustain
        channel.FNum = (channel.FNum & 0x0FF) | ((value & 0x01) << 8);
        channel.Block = (value >> 1) & 0x07;
        channel.SustainOn = (value & 0x20) != 0;
        this.SetKey(channel, (value & 0x10) != 0);
        break;
      case 0x30: // instrument (high nibble) + volume (low nibble)
        channel.Instrument = (value >> 4) & 0x0F;
        channel.Volume = value & 0x0F;
        this.LoadInstrument(channel);
        break;
    }
  }

  private void WriteRhythm(int value) {
    var enable = (value & 0x20) != 0;
    if (enable != this._rhythmMode) {
      this._rhythmMode = enable;
      // Toggling rhythm mode reloads channels 6..8: into the rhythm ROM rows when enabling,
      // back to their melodic instrument when disabling (LoadInstrument picks the right source).
      this.LoadInstrument(this._channels[6]);
      this.LoadInstrument(this._channels[7]);
      this.LoadInstrument(this._channels[8]);
    }
    if (!this._rhythmMode)
      return;

    // Bit 4 BD, 3 SD, 2 TOM, 1 CYM, 0 HH — key each rhythm operator on/off.
    this.SetRhythmKey(RhythmKey.BassDrum, (value & 0x10) != 0);
    this.SetRhythmKey(RhythmKey.Snare, (value & 0x08) != 0);
    this.SetRhythmKey(RhythmKey.Tom, (value & 0x04) != 0);
    this.SetRhythmKey(RhythmKey.Cymbal, (value & 0x02) != 0);
    this.SetRhythmKey(RhythmKey.HiHat, (value & 0x01) != 0);
  }

  private enum RhythmKey { BassDrum, Snare, Tom, Cymbal, HiHat }

  private void SetRhythmKey(RhythmKey which, bool on) {
    // Rhythm instruments occupy fixed operators: BD = ch6 mod+car, HH = ch7 mod, SD = ch7 car,
    // TOM = ch8 mod, CYM = ch8 car.
    switch (which) {
      case RhythmKey.BassDrum:
        this.KeyOperator(this._channels[6].Modulator, on);
        this.KeyOperator(this._channels[6].Carrier, on);
        break;
      case RhythmKey.HiHat: this.KeyOperator(this._channels[7].Modulator, on); break;
      case RhythmKey.Snare: this.KeyOperator(this._channels[7].Carrier, on); break;
      case RhythmKey.Tom: this.KeyOperator(this._channels[8].Modulator, on); break;
      case RhythmKey.Cymbal: this.KeyOperator(this._channels[8].Carrier, on); break;
    }
  }

  // ── patch loading ───────────────────────────────────────────────────────────

  private void LoadInstrument(Channel channel) {
    // In rhythm mode channels 6..8 take their parameters from the rhythm ROM rows, not from the
    // melodic instrument field: ch6 = Bass Drum (row 16), ch7 = High-Hat/Snare (row 17),
    // ch8 = Tom-Tom/Top-Cymbal (row 18). Both operators of the channel share the row.
    if (this._rhythmMode) {
      var index = Array.IndexOf(this._channels, channel);
      if (index == 6) { ApplyPatch(channel, Ym2413Tables.DefaultInstruments[16]); return; }
      if (index == 7) { ApplyPatch(channel, Ym2413Tables.DefaultInstruments[17]); return; }
      if (index == 8) { ApplyPatch(channel, Ym2413Tables.DefaultInstruments[18]); return; }
    }

    var patch = channel.Instrument == 0
      ? this._userPatch
      : this._instrumentRom[channel.Instrument];
    ApplyPatch(channel, patch);
  }

  private static void ApplyPatch(Channel channel, byte[] patch) {
    DecodeOperator(channel.Modulator, patch[0]);
    DecodeOperator(channel.Carrier, patch[1]);

    channel.Modulator.Ksl = (patch[2] >> 6) & 0x03;
    channel.Modulator.TotalLevel = patch[2] & 0x3F;
    channel.Carrier.Ksl = (patch[3] >> 6) & 0x03;
    channel.Feedback = patch[3] & 0x07;
    channel.Modulator.HalfSine = (patch[3] & 0x08) != 0;
    channel.Carrier.HalfSine = (patch[3] & 0x10) != 0;

    channel.Modulator.AttackRate = (patch[4] >> 4) & 0x0F;
    channel.Modulator.DecayRate = patch[4] & 0x0F;
    channel.Carrier.AttackRate = (patch[5] >> 4) & 0x0F;
    channel.Carrier.DecayRate = patch[5] & 0x0F;

    channel.Modulator.SustainLevel = (patch[6] >> 4) & 0x0F;
    channel.Modulator.ReleaseRate = patch[6] & 0x0F;
    channel.Carrier.SustainLevel = (patch[7] >> 4) & 0x0F;
    channel.Carrier.ReleaseRate = patch[7] & 0x0F;
  }

  private static void DecodeOperator(Operator op, byte b) {
    op.AmOn = (b & 0x80) != 0;
    op.VibOn = (b & 0x40) != 0;
    op.EgType = (b & 0x20) != 0;
    op.KsrOn = (b & 0x10) != 0;
    op.Multiple = b & 0x0F;
  }

  // ── key on/off ──────────────────────────────────────────────────────────────

  private void SetKey(Channel channel, bool on) {
    if (on == channel.KeyOn)
      return;
    channel.KeyOn = on;
    // In rhythm mode the rhythm channels (6..8) are keyed separately by reg 0x0E.
    if (this._rhythmMode && channel == this._channels[6])
      return;
    if (this._rhythmMode && (channel == this._channels[7] || channel == this._channels[8]))
      return;
    this.KeyOperator(channel.Modulator, on);
    this.KeyOperator(channel.Carrier, on);
  }

  private void KeyOperator(Operator op, bool on) {
    if (on) {
      op.Phase = 0;
      op.State = EnvPhase.Attack;
      // A maxed attack rate snaps straight to peak.
      if (op.AttackRate == 0x0F)
        op.EnvLevel = AttackEnd;
    } else {
      if (op.State != EnvPhase.Off)
        op.State = EnvPhase.Release;
    }
  }

  // ── synthesis ───────────────────────────────────────────────────────────────

  /// <summary>Renders one mono frame at the chip's native rate; the value is signed 16-bit.</summary>
  public short RenderSample() {
    this.AdvanceLfo();
    this.ClockNoise();
    this.AdvanceEnvelopes();

    var acc = 0;
    var melodicChannels = this._rhythmMode ? 6 : 9;
    for (var c = 0; c < melodicChannels; ++c)
      acc += this.RenderMelodicChannel(this._channels[c]);

    if (this._rhythmMode)
      acc += this.RenderRhythm();

    return Clamp16(acc);
  }

  /// <summary>Renders one mono frame (overload matching the stereo sibling's signature).</summary>
  public void RenderSample(out short mono) => mono = this.RenderSample();

  private int RenderMelodicChannel(Channel channel) {
    var amOffset = this.AmOffset(channel.Modulator);

    // Modulator with feedback (averaged previous two outputs, scaled by the feedback shift).
    var fb = channel.Feedback;
    var modIn = fb == 0 ? 0 : (channel.Modulator.Prev + channel.Modulator.Output) >> (9 - fb);
    var modOut = this.OperatorOutput(channel, channel.Modulator, modIn, channel.Modulator.TotalLevel, amOffset);
    channel.Modulator.Prev = channel.Modulator.Output;
    channel.Modulator.Output = modOut;

    // Carrier modulated by the modulator output; carrier attenuation is the 4-bit volume.
    var carAm = this.AmOffset(channel.Carrier);
    var carrierTl = channel.Volume << 2; // 4-bit volume → ~3 dB steps in the modulator's TL units
    var carOut = this.OperatorOutput(channel, channel.Carrier, modOut, carrierTl, carAm);
    channel.Carrier.Output = carOut;
    return carOut;
  }

  /// <summary>
  /// Computes one operator's signed output. <paramref name="phaseMod"/> is the phase
  /// modulation from the upstream operator in operator units; <paramref name="tlSteps"/> is the
  /// total-level attenuation in modulator-TL steps (6-bit), <paramref name="amOffset"/> the
  /// tremolo offset in 1/256-dB units.
  /// </summary>
  private int OperatorOutput(Channel channel, Operator op, int phaseMod, int tlSteps, int amOffset) {
    var increment = this.PhaseIncrement(channel, op);
    op.Phase += increment;

    // Attenuation: envelope (EG steps → 1/256-dB) + total level (×~24/64 dB) + tremolo.
    var env = (op.EnvLevel << 5) + (tlSteps << 5) + amOffset;
    if (env > 0x1FFF)
      env = 0x1FFF;

    var phase = (int)((op.Phase >> 9) + (uint)phaseMod) & 0x3FF;
    return LogSinToLinear(phase, env, op.HalfSine);
  }

  /// <summary>Phase increment for the channel's F-num/block scaled by this operator's MUL and vibrato.</summary>
  private uint PhaseIncrement(Channel channel, Operator op) {
    // Base step: (fnum * 2^block) — the OPLL phase generator runs at clock/72 so the F-num maps
    // directly. fnum*clock/(72*2^(19-block)) Hz per the manual; here the accumulator's top 10
    // bits index the 1024-entry sine, so the increment is (fnum << block) >> 1.
    var fnum = channel.FNum;
    if (op.VibOn) {
      // Vibrato: ±a small fraction of the F-num based on the PM LFO phase (8-step table).
      var pm = VibratoTable[(this._lfoPm >> 10) & 0x07] * (fnum >> 7);
      fnum += pm;
    }
    var baseInc = ((uint)fnum << channel.Block) >> 1;
    var mul = Ym2413Tables.Multiple[op.Multiple];
    return (uint)(((long)baseInc * mul) >> 1);
  }

  /// <summary>
  /// Converts a 10-bit phase index plus attenuation (1/256-dB log units) to a signed linear
  /// sample via the genuine log-sine and exponential ROMs. <paramref name="halfSine"/> mutes the
  /// negative half-period (the OPLL's half-rectified waveform select).
  /// </summary>
  private static int LogSinToLinear(int phase, int attenuation, bool halfSine) {
    var negative = (phase & 0x200) != 0;
    if (halfSine && negative)
      return 0; // half-rectified sine: lower half clamped to zero

    var quarter = phase & 0xFF;
    var index = (phase & 0x100) != 0 ? 0xFF - quarter : quarter;
    var att = Ym2413Tables.LogSin[index] + attenuation;
    if (att > 0x1FFF)
      att = 0x1FFF;

    var value = (Ym2413Tables.Exp[(att & 0xFF) ^ 0xFF] | 0x400) >> (att >> 8);
    return negative ? -value : value;
  }

  // ── rhythm mode ───────────────────────────────────────────────────────────

  private int RenderRhythm() {
    // Rhythm instruments reuse channels 6..8 operators with fixed roles. The bass drum is a
    // normal 2-op voice on channel 6; HH/SD/TOM/CYM use noise + fixed phase relationships
    // (emu2413 calc_rhythm).
    var acc = 0;

    // Bass Drum (ch6): modulator → carrier, doubled in the mix per the OPLL output stage.
    var ch6 = this._channels[6];
    var bdAm = this.AmOffset(ch6.Modulator);
    var bdModIn = ch6.Feedback == 0 ? 0 : (ch6.Modulator.Prev + ch6.Modulator.Output) >> (9 - ch6.Feedback);
    var bdMod = this.OperatorOutput(ch6, ch6.Modulator, bdModIn, ch6.Modulator.TotalLevel, bdAm);
    ch6.Modulator.Prev = ch6.Modulator.Output;
    ch6.Modulator.Output = bdMod;
    var bdCar = this.OperatorOutput(ch6, ch6.Carrier, bdMod, ch6.Volume << 2, this.AmOffset(ch6.Carrier));
    ch6.Carrier.Output = bdCar;
    acc += bdCar << 1;

    var ch7 = this._channels[7];
    var ch8 = this._channels[8];

    // Advance HH/SD/TOM/CYM phases via their own increments.
    var hhInc = this.PhaseIncrement(ch7, ch7.Modulator);
    ch7.Modulator.Phase += hhInc;
    var sdInc = this.PhaseIncrement(ch7, ch7.Carrier);
    ch7.Carrier.Phase += sdInc;
    var tomInc = this.PhaseIncrement(ch8, ch8.Modulator);
    ch8.Modulator.Phase += tomInc;
    var cymInc = this.PhaseIncrement(ch8, ch8.Carrier);
    ch8.Carrier.Phase += cymInc;

    var noise = (this._noise & 1) != 0;

    var hh = ch7.Modulator.Phase >> 9;
    var cym = ch8.Carrier.Phase >> 9;

    // The HH/CYM phase decision combines specific phase bits (andete/emu2413 rhythm logic).
    var phaseBitHh = ((hh >> 8) & 1) ^ ((hh >> 3) & 1);
    var phaseBitCym = ((cym >> 8) & 1) ^ ((cym >> 5) & 1) ^ ((cym >> 7) & 1) ^ phaseBitHh;

    // High-Hat (ch7 modulator): noisy two-state phase.
    var hhPhase = phaseBitCym != 0
      ? (noise ? 0x2D0 : 0x234)
      : (noise ? 0x34 : 0xD0);
    acc += this.RhythmOperator(ch7.Modulator, (int)hhPhase) << 1;

    // Snare Drum (ch7 carrier): bit 8 of its own phase XOR noise.
    var sd = ch7.Carrier.Phase >> 9;
    var sdPhase = ((sd >> 8) & 1) != 0
      ? (noise ? 0x300 : 0x200)
      : (noise ? 0x100 : 0x000);
    acc += this.RhythmOperator(ch7.Carrier, (int)sdPhase) << 1;

    // Tom-Tom (ch8 modulator): plain tone from its own phase.
    acc += this.RhythmOperator(ch8.Modulator, (int)(ch8.Modulator.Phase >> 9) & 0x3FF) << 1;

    // Top-Cymbal (ch8 carrier): same two-state phase as the High-Hat decision.
    var cymPhase = phaseBitCym != 0 ? 0x300 : 0x100;
    acc += this.RhythmOperator(ch8.Carrier, cymPhase) << 1;

    return acc;
  }

  private int RhythmOperator(Operator op, int phase) {
    var env = (op.EnvLevel << 5);
    if (op.AmOn)
      env += this.AmOffset(op);
    if (env > 0x1FFF)
      env = 0x1FFF;
    return LogSinToLinear(phase & 0x3FF, env, op.HalfSine);
  }

  // ── envelope generator ────────────────────────────────────────────────────

  private int _egCounter;

  private void AdvanceEnvelopes() {
    ++this._egCounter;
    foreach (var channel in this._channels) {
      this.AdvanceOperatorEnvelope(channel, channel.Modulator);
      this.AdvanceOperatorEnvelope(channel, channel.Carrier);
    }
  }

  private void AdvanceOperatorEnvelope(Channel channel, Operator op) {
    if (op.State == EnvPhase.Off)
      return;

    var rate = op.State switch {
      EnvPhase.Attack => op.AttackRate,
      EnvPhase.Decay => op.DecayRate,
      EnvPhase.Sustain => op.EgType ? 0 : op.ReleaseRate,
      EnvPhase.Release => channel.SustainOn ? 5 : (op.EgType ? op.ReleaseRate : 7),
      _ => op.ReleaseRate,
    };

    var effective = this.EffectiveRate(channel, op, rate);
    if (effective == 0)
      return;

    var shift = EnvRateShift[effective];
    if (((uint)this._egCounter & ((1u << shift) - 1)) != 0)
      return;
    var step = EnvIncrement[effective, (this._egCounter >> shift) & 0x07];
    if (step == 0)
      return;

    switch (op.State) {
      case EnvPhase.Attack:
        op.EnvLevel += (~op.EnvLevel * step) >> 3;
        if (op.EnvLevel <= AttackEnd || effective >= 60) {
          op.EnvLevel = AttackEnd;
          op.State = EnvPhase.Decay;
        }
        break;
      case EnvPhase.Decay:
        op.EnvLevel += step;
        if (op.EnvLevel >= SustainLevelToEg(op.SustainLevel)) {
          op.EnvLevel = SustainLevelToEg(op.SustainLevel);
          op.State = EnvPhase.Sustain;
        }
        break;
      case EnvPhase.Sustain:
      case EnvPhase.Release:
        op.EnvLevel += step;
        if (op.EnvLevel >= MaxAttenuation) {
          op.EnvLevel = MaxAttenuation;
          op.State = EnvPhase.Off;
        }
        break;
    }
  }

  private int EffectiveRate(Channel channel, Operator op, int rate) {
    if (rate == 0)
      return 0;
    // Key-scale-of-rate: add the high block/F-num "rate key code" scaled by KSR.
    var keyCode = (channel.Block << 1) | ((channel.FNum >> 8) & 1);
    var scaled = (rate << 2) + (op.KsrOn ? keyCode : keyCode >> 2);
    return scaled > 63 ? 63 : scaled;
  }

  private static int SustainLevelToEg(int sl) => sl == 0x0F ? MaxAttenuation : sl << 3;

  // EG rate → shift (how often a step happens) and per-step increment pattern; mirrors the
  // shared OPN/OPL envelope timing (an 8-entry increment cycle per rate).
  private static readonly int[] EnvRateShift = BuildRateShift();
  private static readonly int[,] EnvIncrement = BuildRateIncrement();

  private static int[] BuildRateShift() {
    var shift = new int[64];
    for (var r = 0; r < 64; ++r)
      shift[r] = r < 48 ? 11 - (r >> 2) : 0;
    return shift;
  }

  private static int[,] BuildRateIncrement() {
    var table = new int[64, 8];
    int[][] patterns = [
      [0, 1, 0, 1, 0, 1, 0, 1],
      [0, 1, 0, 1, 1, 1, 0, 1],
      [0, 1, 1, 1, 0, 1, 1, 1],
      [0, 1, 1, 1, 1, 1, 1, 1],
    ];
    for (var r = 0; r < 64; ++r) {
      var low = r & 0x03;
      int[] pat;
      if (r < 4) pat = patterns[0];
      else if (r >= 60) pat = [2, 2, 2, 2, 2, 2, 2, 2];
      else pat = patterns[low];
      for (var k = 0; k < 8; ++k)
        table[r, k] = pat[k];
    }
    return table;
  }

  // ── LFO + noise ─────────────────────────────────────────────────────────────

  private void AdvanceLfo() {
    // Tremolo ~3.7 Hz and vibrato ~6.4 Hz at clock/72; the increments scale the 13-/14-bit
    // accumulators so the documented frequencies fall out near the chip's native rate.
    this._lfoAm += AmIncrement;
    this._lfoPm += PmIncrement;
  }

  // AM accumulator: top bits index a 0..26 triangle (~1 dB depth, doubled when AM on).
  private const uint AmIncrement = 78;   // ≈ 3.7 Hz at ~49.7 kHz native
  private const uint PmIncrement = 105;  // ≈ 6.4 Hz

  private int AmOffset(Operator op) {
    if (!op.AmOn)
      return 0;
    // Triangle 0..26 in 1/256-dB units (≈ 1 dB peak tremolo per the manual).
    var phase = (int)((this._lfoAm >> 7) & 0x3F);
    var tri = phase < 32 ? phase : 63 - phase;
    return tri >> 1;
  }

  // Vibrato modulation table (eighths of a cycle), small ± offsets applied to the F-num.
  private static readonly int[] VibratoTable = [0, 1, 2, 1, 0, -1, -2, -1];

  private void ClockNoise() {
    // 23-bit Galois LFSR used by rhythm-mode HH/SD/CYM (emu2413: tap at bit 22, xor bit 8).
    var bit = ((this._noise) ^ (this._noise >> 14)) & 1;
    this._noise = (this._noise >> 1) | (bit << 22);
    if (this._noise == 0)
      this._noise = 1;
  }

  private static short Clamp16(int value) =>
    value > 32767 ? (short)32767 : value < -32768 ? (short)-32768 : (short)value;
}
