#pragma warning disable CS1591
namespace Codec.Opl;

/// <summary>
/// Yamaha OPL FM synthesis family: the YM3526 (OPL), YM3812 (OPL2), YMF262 (OPL3) and the
/// Y8950 MSX-Audio (an OPL core with an extra DELTA-T ADPCM channel). The OPL is a two-operator
/// FM chip with nine channels (eighteen on OPL3); OPL3 additionally provides four-operator
/// channel pairing, stereo L/R panning, and eight operator waveforms (against OPL2's four and
/// the plain sine of the original OPL).
/// <para><b>References.</b> The operator/envelope/phase/waveform and rhythm logic is ported from
/// <c>Nuked-OPL3</c> (Alexey Khokholov, "Nuke.YKT") — the cycle-accurate reverse-engineered
/// YMF262 — and cross-checked against MAME's <c>ymfm</c> and the Yamaha YMF262 / YM3812
/// application manuals for the register map. The log-sine and exponential operator ROMs are the
/// genuine die constants shared with the OPN/OPL family (see <see cref="OplTables"/>).</para>
/// <para>Registers are written through <see cref="WriteRegister(int,int,int)"/> (bank, address,
/// value); bank 1 is the OPL3 high register set (addresses 0x100..0x1FF).
/// <see cref="RenderSample(out short,out short)"/>
/// produces one stereo frame at the chip's native rate (<see cref="NativeSampleRate"/>); OPL and
/// OPL2 are mono and emit the same value on both sides.</para>
/// <para><b>Y8950 ADPCM.</b> The DELTA-T ADPCM channel (registers 0x07..0x12) is decoded only
/// when its ROM/RAM sample memory is supplied via <see cref="LoadAdpcmMemory"/>; VGM does not
/// carry that data in the common case, so without it the ADPCM channel is gated off and only the
/// FM part is voiced (see <see cref="AdpcmActive"/>).</para>
/// </summary>
public sealed class OplCodec {

  /// <summary>The OPL variant being emulated.</summary>
  public enum Chip {   /// <summary>
  /// Specifies the opl option.
  /// </summary>
Opl,   /// <summary>
  /// Specifies the opl 2 option.
  /// </summary>
Opl2,   /// <summary>
  /// Specifies the opl 3 option.
  /// </summary>
Opl3,   /// <summary>
  /// Specifies the y 8950 option.
  /// </summary>
Y8950 }

  /// <summary>FM sample-rate divisor: the OPL family emits one frame per clock/72 tick.</summary>
  public const int Prescale = 72;

  private readonly Chip _chip;
  private readonly double _nativeRate;

  // 18 channels (OPL/OPL2/Y8950 use only the first 9), each with two operators.
  private readonly OplChannel[] _channels = BuildChannels();

  // OPL3 global state.
  private bool _opl3Enabled;       // reg 0x105 bit0
  private bool _newWaveforms;      // OPL2 reg 0x01 bit5 (waveform select enable) / OPL3 implicit
  private bool _nts;               // note-select (reg 0x08 bit6)

  // Rhythm mode (reg 0xBD).
  private bool _rhythmMode;
  private int _tremoloDepth;       // reg 0xBD bit7 (AM depth: 1.0 vs 4.8 dB)
  private int _vibratoDepth;       // reg 0xBD bit6 (PM depth)

  // Global LFOs and EG timer.
  private uint _egTimer;
  private uint _lfoAm;
  private uint _lfoPm;
  private uint _noise = 1;         // 23-bit LFSR for rhythm HH/SD/CYM

  // Y8950 ADPCM (gated unless sample memory is loaded).
  private byte[]? _adpcmMemory;

  /// <param name="chip">Which OPL variant to emulate.</param>
  /// <param name="clock">Chip clock in Hz (3.58 MHz typical for OPL/OPL2/Y8950, 14.32 MHz OPL3).</param>
    /// <summary>
  /// Initializes a new instance of <see cref="OplCodec"/>.
  /// </summary>
public OplCodec(Chip chip = Chip.Opl2, double clock = 3579545.0) {
    this._chip = chip;
    this._nativeRate = clock / Prescale;
    // OPL3 implicitly has the extended waveforms available once OPL3 mode is enabled; OPL2 gates
    // them behind reg 0x01 bit5. The original OPL has sine only.
    foreach (var ch in this._channels) {
      ch.Modulator.Channel = ch;
      ch.Carrier.Channel = ch;
    }
    this.WirePartners();
  }

  private static OplChannel[] BuildChannels() {
    var channels = new OplChannel[18];
    for (var i = 0; i < channels.Length; ++i)
      channels[i] = new OplChannel();
    return channels;
  }

  // OPL3 4-op pairs: channels 0-2 pair with 3-5, 9-11 pair with 12-14 (the second register bank).
  private void WirePartners() {
    void Pair(int a, int b) {
      this._channels[a].Partner = this._channels[b];
      this._channels[b].Partner = this._channels[a];
    }
    Pair(0, 3); Pair(1, 4); Pair(2, 5);
    Pair(9, 12); Pair(10, 13); Pair(11, 14);
  }

  /// <summary>The chip's native output sample rate (clock / 72).</summary>
  public double NativeSampleRate => this._nativeRate;

  /// <summary>The OPL variant this instance emulates.</summary>
  public Chip Variant => this._chip;

  /// <summary>True when rhythm mode (reg 0xBD bit5) is engaged.</summary>
  public bool RhythmMode => this._rhythmMode;

  /// <summary>True when OPL3 extended (18-channel/4-op/stereo) mode is enabled (reg 0x105 bit0).</summary>
  public bool Opl3Enabled => this._opl3Enabled;

  /// <summary>True when the Y8950 ADPCM channel has sample memory loaded and is therefore voiced.</summary>
  public bool AdpcmActive => this._chip == Chip.Y8950 && this._adpcmMemory is { Length: > 0 };

  /// <summary>The genuine die-extracted log-sine ROM (256 entries, 1/256 dB).</summary>
  public static IReadOnlyList<ushort> LogSinRom => OplTables.LogSin;

  /// <summary>The genuine die-extracted exponential ROM (256 entries; OR'd with 0x400 in use).</summary>
  public static IReadOnlyList<ushort> ExpRom => OplTables.Exp;

  /// <summary>
  /// Loads Y8950 ADPCM sample memory (the VGM 0x82/0x88 data blocks). Without it the ADPCM
  /// channel is gated off; the FM part always renders regardless.
  /// </summary>
  public void LoadAdpcmMemory(byte[] memory) => this._adpcmMemory = memory;

  // ── register bus ──────────────────────────────────────────────────────────────

  /// <summary>
  /// Writes one OPL register. <paramref name="bank"/> 0 is the base register set; bank 1 (OPL3)
  /// is the high set (the VGM 0x5F command / addresses 0x100..0x1FF). The Y8950/YM3812/YM3526
  /// single-bank commands use bank 0.
  /// </summary>
  public void WriteRegister(int bank, int address, int value) {
    address &= 0xFF;
    value &= 0xFF;
    if (bank != 0 && this._chip != Chip.Opl3)
      return; // only OPL3 has the second bank

    // Global / chip-wide registers.
    if (address == 0x01) {
      // OPL2 waveform-select enable (bit5); on OPL3 ignored (waveforms gated by 0x105 instead).
      if (this._chip == Chip.Opl2)
        this._newWaveforms = (value & 0x20) != 0;
      return;
    }
    if (address == 0x08) {
      this._nts = (value & 0x40) != 0;
      return;
    }
    if (address == 0xBD && bank == 0) {
      this.WriteRhythm(value);
      return;
    }
    if (bank == 1 && address == 0x04) {
      this.WriteFourOp(value);
      return;
    }
    if (bank == 1 && address == 0x05) {
      this._opl3Enabled = (value & 0x01) != 0;
      if (this._opl3Enabled)
        this._newWaveforms = true;
      return;
    }

    var region = address & 0xF0;

    switch (region) {
      case 0x20:
      case 0x30:
        this.WriteOperatorFlags(bank, address, value);
        break;
      case 0x40:
      case 0x50:
        this.WriteKslTotalLevel(bank, address, value);
        break;
      case 0x60:
      case 0x70:
        this.WriteAttackDecay(bank, address, value);
        break;
      case 0x80:
      case 0x90:
        this.WriteSustainRelease(bank, address, value);
        break;
      case 0xA0:
        if ((address & 0x0F) <= 0x08)
          this.WriteFNumLow(bank, address & 0x0F, value);
        break;
      case 0xB0:
        if ((address & 0x0F) <= 0x08)
          this.WriteFNumHighKey(bank, address & 0x0F, value);
        break;
      case 0xC0:
        if ((address & 0x0F) <= 0x08)
          this.WriteFeedbackConnection(bank, address & 0x0F, value);
        break;
      case 0xE0:
      case 0xF0:
        this.WriteWaveform(bank, address, value);
        break;
    }
  }

  /// <summary>Convenience single-bank write (YM3526/YM3812/Y8950).</summary>
  public void WriteRegister(int address, int value) => this.WriteRegister(0, address, value);

  // Standard OPL operator address layout: per channel n (0..8) the modulator sits at
  // ModulatorAddress[n] and the carrier three addresses higher. Addresses 0x06/0x07, 0x0E/0x0F,
  // 0x16/0x17 are holes (no operator). This is the canonical YMF262/YM3812 slot map.
  private static readonly int[] ModulatorAddress = [0x00, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x10, 0x11, 0x12];

  // Resolve (bank,address) to the operator object, or null for an unmapped (hole) address.
  private OplOperator? ResolveOperator(int bank, int address) {
    var local = address & 0x1F;
    var channelBase = bank == 1 ? 9 : 0;
    for (var c = 0; c < 9; ++c) {
      if (local == ModulatorAddress[c])
        return this._channels[channelBase + c].Modulator;
      if (local == ModulatorAddress[c] + 3)
        return this._channels[channelBase + c].Carrier;
    }
    return null;
  }

  private void WriteOperatorFlags(int bank, int address, int value) {
    var op = this.ResolveOperator(bank, address);
    if (op == null) return;
    op.Tremolo = (value & 0x80) != 0;
    op.Vibrato = (value & 0x40) != 0;
    op.EgSustain = (value & 0x20) != 0;
    op.Ksr = (value & 0x10) != 0 ? 1 : 0;
    op.Multiple = value & 0x0F;
  }

  private void WriteKslTotalLevel(int bank, int address, int value) {
    var op = this.ResolveOperator(bank, address);
    if (op == null) return;
    op.Ksl = (value >> 6) & 0x03;
    op.TotalLevel = value & 0x3F;
  }

  private void WriteAttackDecay(int bank, int address, int value) {
    var op = this.ResolveOperator(bank, address);
    if (op == null) return;
    op.AttackRate = (value >> 4) & 0x0F;
    op.DecayRate = value & 0x0F;
  }

  private void WriteSustainRelease(int bank, int address, int value) {
    var op = this.ResolveOperator(bank, address);
    if (op == null) return;
    op.SustainLevel = (value >> 4) & 0x0F;
    op.ReleaseRate = value & 0x0F;
  }

  private void WriteWaveform(int bank, int address, int value) {
    var op = this.ResolveOperator(bank, address);
    if (op == null) return;
    // The original OPL (YM3526) has no waveform select at all — sine only.
    if (this._chip == Chip.Opl) {
      op.Waveform = 0;
      return;
    }
    // Waveforms above 3 require the new-waveform enable (OPL2 reg 0x01 bit5 / OPL3 0x105).
    var ws = value & 0x07;
    if (!this._newWaveforms)
      ws &= 0x03;
    op.Waveform = ws;
  }

  private void WriteFNumLow(int bank, int index, int value) {
    var ch = this._channels[(bank == 1 ? 9 : 0) + index];
    ch.FNum = (ch.FNum & 0x300) | value;
  }

  private void WriteFNumHighKey(int bank, int index, int value) {
    var ch = this._channels[(bank == 1 ? 9 : 0) + index];
    ch.FNum = (ch.FNum & 0x0FF) | ((value & 0x03) << 8);
    ch.Block = (value >> 2) & 0x07;
    this.SetKey(ch, (value & 0x20) != 0);
  }

  private void WriteFeedbackConnection(int bank, int index, int value) {
    var ch = this._channels[(bank == 1 ? 9 : 0) + index];
    ch.Feedback = (value >> 1) & 0x07;
    ch.Additive = (value & 0x01) != 0;
    if (this._chip == Chip.Opl3) {
      ch.Left = (value & 0x10) != 0;
      ch.Right = (value & 0x20) != 0;
    }
  }

  // OPL3 reg 0x104: each bit pairs two channels into a 4-op voice.
  private void WriteFourOp(int value) {
    int[] firsts = [0, 1, 2, 9, 10, 11];
    for (var b = 0; b < 6; ++b) {
      var first = this._channels[firsts[b]];
      var second = first.Partner!;
      var on = this._opl3Enabled && (value & (1 << b)) != 0;
      first.FourOp = on;
      second.FourOpSecondary = on;
    }
  }

  // ── key on/off ────────────────────────────────────────────────────────────────

  private void SetKey(OplChannel channel, bool on) {
    if (on == channel.KeyOn)
      return;
    channel.KeyOn = on;
    KeyChannelOperators(channel, on);
    // In 4-operator mode the key-on of the primary channel also gates the partner's two
    // operators (all four sound as one voice); the partner's own B0 key-on is ignored.
    if (channel.FourOp && channel.Partner != null)
      KeyChannelOperators(channel.Partner, on);
  }

  private static void KeyChannelOperators(OplChannel channel, bool on) {
    if (on) {
      channel.Modulator.KeyOn();
      channel.Carrier.KeyOn();
    } else {
      channel.Modulator.KeyOff();
      channel.Carrier.KeyOff();
    }
  }

  // ── synthesis ───────────────────────────────────────────────────────────────

  /// <summary>Renders one stereo frame at the chip's native rate; values are signed 16-bit.</summary>
  public void RenderSample(out short left, out short right) {
    this.AdvanceLfo();
    this.ClockNoise();
    this.AdvanceEnvelopes();

    var l = 0;
    var r = 0;

    var melodic = this._rhythmMode ? 6 : 9;
    // OPL/OPL2/Y8950: 9 channels in bank 0. OPL3: 18 channels across both banks; the rhythm
    // channels (6..8) only exist in bank 0.
    var totalChannels = this._chip == Chip.Opl3 && this._opl3Enabled ? 18 : 9;

    for (var c = 0; c < totalChannels; ++c) {
      // Skip the rhythm-occupied channels 6..8 when rhythm mode is active (bank 0 only).
      if (this._rhythmMode && c is >= 6 and <= 8)
        continue;
      var ch = this._channels[c];
      if (ch.FourOpSecondary)
        continue; // voiced as part of its 4-op partner
      var sample = ch.FourOp ? this.RenderFourOp(ch) : this.RenderTwoOp(ch);
      if (ch.Left) l += sample;
      if (ch.Right) r += sample;
    }

    if (this._rhythmMode) {
      var rhythm = this.RenderRhythm();
      // Rhythm is summed to both sides (panning of ch6..8 still applies on OPL3).
      l += rhythm;
      r += rhythm;
    }

    left = Clamp16(l);
    right = Clamp16(r);
  }

  /// <summary>Renders one mono frame (left+right averaged) for callers wanting a single value.</summary>
  public short RenderSample() {
    this.RenderSample(out var l, out var r);
    return Clamp16((l + r) / 2);
  }

  private int RenderTwoOp(OplChannel ch) {
    var mod = ch.Modulator;
    var car = ch.Carrier;

    var fbIn = ch.Feedback == 0
      ? 0
      : (mod.Prev + mod.Output) >> (9 - ch.Feedback);
    var modOut = this.OperatorOutput(mod, fbIn);
    mod.Prev = mod.Output;
    mod.Output = modOut;

    int carOut;
    if (ch.Additive) {
      // AM/additive: both operators sound, summed.
      carOut = this.OperatorOutput(car, 0);
      car.Output = carOut;
      return (modOut + carOut);
    }
    // FM: carrier phase-modulated by the modulator.
    carOut = this.OperatorOutput(car, modOut);
    car.Output = carOut;
    return carOut;
  }

  // OPL3 4-operator voice: operators op1(mod),op2(car of ch),op3(mod of partner),op4(car of
  // partner) chained per the channel's algorithm pair (reg 0xC0 bit0 of both halves).
  private int RenderFourOp(OplChannel ch) {
    var partner = ch.Partner!;
    var op1 = ch.Modulator;
    var op2 = ch.Carrier;
    var op3 = partner.Modulator;
    var op4 = partner.Carrier;

    var fbIn = ch.Feedback == 0 ? 0 : (op1.Prev + op1.Output) >> (9 - ch.Feedback);
    var o1 = this.OperatorOutput(op1, fbIn);
    op1.Prev = op1.Output;
    op1.Output = o1;

    // Algorithm selected by (ch.Additive, partner.Additive) → 4 connections (Nuked-OPL3).
    var alg = (ch.Additive ? 1 : 0) | (partner.Additive ? 2 : 0);
    switch (alg) {
      case 0: { // op1→op2→op3→op4 (all FM)
        var o2 = this.OperatorOutput(op2, o1); op2.Output = o2;
        var o3 = this.OperatorOutput(op3, o2); op3.Output = o3;
        var o4 = this.OperatorOutput(op4, o3); op4.Output = o4;
        return o4;
      }
      case 1: { // op1 + (op2→op3→op4)
        var o2 = this.OperatorOutput(op2, 0); op2.Output = o2;
        var o3 = this.OperatorOutput(op3, o2); op3.Output = o3;
        var o4 = this.OperatorOutput(op4, o3); op4.Output = o4;
        return o1 + o4;
      }
      case 2: { // (op1→op2) + (op3→op4)
        var o2 = this.OperatorOutput(op2, o1); op2.Output = o2;
        var o3 = this.OperatorOutput(op3, 0); op3.Output = o3;
        var o4 = this.OperatorOutput(op4, o3); op4.Output = o4;
        return o2 + o4;
      }
      default: { // case 3: op1 + (op2→op3) + op4
        var o2 = this.OperatorOutput(op2, o1); op2.Output = o2;
        var o3 = this.OperatorOutput(op3, o2); op3.Output = o3;
        var o4 = this.OperatorOutput(op4, 0); op4.Output = o4;
        return o1 + o3 + o4;
      }
    }
  }

  /// <summary>
  /// Computes one operator's signed output: advance the phase, sum attenuations (envelope + TL +
  /// KSL + tremolo), apply the waveform-shaped log-sine, and exponentiate. <paramref name="phaseMod"/>
  /// is the phase modulation from the upstream operator in operator-output units.
  /// </summary>
  private int OperatorOutput(OplOperator op, int phaseMod) {
    op.Phase += this.PhaseIncrement(op);

    var att = op.EnvLevel + (op.TotalLevel << 2) + op.KslAttenuation();
    if (op.Tremolo)
      att += this.TremoloOffset();
    if (att > OplOperator.MaxAttenuation)
      att = OplOperator.MaxAttenuation;

    var phase = (int)((op.Phase >> 9) + (uint)phaseMod) & 0x3FF;
    return Waveform(op.Waveform, phase, att << 4);
  }

  private uint PhaseIncrement(OplOperator op) {
    var ch = op.Channel;
    var fnum = ch.FNum;
    if (op.Vibrato)
      fnum += this.VibratoOffset(fnum);
    // base step: (fnum << block) >> 1, then ×MUL (table already doubled, so >>1 once).
    var baseInc = ((uint)fnum << ch.Block) >> 1;
    return (uint)(((long)baseInc * OplTables.Multiply[op.Multiple]) >> 1);
  }

  // ── waveform generator ──────────────────────────────────────────────────────

  // The eight OPL3 waveforms derived from the quarter-period log-sine ROM (Nuked-OPL3 logic).
  private static int Waveform(int wave, int phase, int attenuation) {
    phase &= 0x3FF;
    var quarter = phase & 0xFF;
    var negative = (phase & 0x200) != 0;
    var mirror = (phase & 0x100) != 0;

    int logsin;
    bool sign;
    switch (wave) {
      case 0: // full sine
        logsin = LogSinValue(quarter, mirror);
        sign = negative;
        break;
      case 1: // half sine (negative half muted)
        if (negative) return 0;
        logsin = LogSinValue(quarter, mirror);
        sign = false;
        break;
      case 2: // abs sine (both halves positive)
        logsin = LogSinValue(quarter, mirror);
        sign = false;
        break;
      case 3: // quarter / pulse sine (only the rising quarters)
        if (mirror) return 0;
        logsin = OplTables.LogSin[quarter];
        sign = false;
        break;
      case 4: // OPL3: alternating sine (frequency-doubled, sign alternates)
        if (negative) return 0;
        logsin = LogSinValue((phase << 1) & 0xFF, ((phase << 1) & 0x100) != 0);
        sign = (phase & 0x100) != 0;
        break;
      case 5: // OPL3: camel sine (frequency-doubled abs)
        if (negative) return 0;
        logsin = LogSinValue((phase << 1) & 0xFF, ((phase << 1) & 0x100) != 0);
        sign = false;
        break;
      case 6: // OPL3: square wave
        return Exponential(attenuation, negative);
      default: // case 7: OPL3: log-sawtooth (derived directly from the exp ramp)
        if (negative) {
          logsin = (0x100 - (phase & 0xFF)) << 3;
          sign = true;
        } else {
          logsin = (phase & 0xFF) << 3;
          sign = false;
        }
        break;
    }

    return Exponential(logsin + attenuation, sign);
  }

  private static int LogSinValue(int quarter, bool mirror)
    => OplTables.LogSin[mirror ? 0xFF - quarter : quarter];

  private static int Exponential(int attenuation, bool negative) {
    if (attenuation > 0x1FFF)
      attenuation = 0x1FFF;
    if (attenuation < 0)
      attenuation = 0;
    var value = (OplTables.Exp[(attenuation & 0xFF) ^ 0xFF] | 0x400) >> (attenuation >> 8);
    return negative ? -value : value;
  }

  // ── rhythm mode ─────────────────────────────────────────────────────────────

  private int RenderRhythm() {
    var acc = 0;
    var ch6 = this._channels[6];
    var ch7 = this._channels[7];
    var ch8 = this._channels[8];

    // Bass drum: a normal 2-op FM voice on channel 6.
    acc += this.RenderTwoOp(ch6);

    // Advance HH/SD/TOM/CYM phases.
    ch7.Modulator.Phase += this.PhaseIncrement(ch7.Modulator);  // HH
    ch7.Carrier.Phase += this.PhaseIncrement(ch7.Carrier);      // SD
    ch8.Modulator.Phase += this.PhaseIncrement(ch8.Modulator);  // TOM
    ch8.Carrier.Phase += this.PhaseIncrement(ch8.Carrier);      // CYM

    var noise = (this._noise & 1) != 0 ? 1 : 0;
    var hh = (int)(ch7.Modulator.Phase >> 9);
    var tc = (int)(ch8.Carrier.Phase >> 9);

    // The shared HH/CYM phase decision (Nuked-OPL3 / andete rhythm logic): three HH phase bits
    // combine into one control bit, mixed with two TC phase bits.
    var bit2 = (hh >> 2) & 1;
    var bit3 = (hh >> 3) & 1;
    var bit7 = (hh >> 7) & 1;
    var bit5 = (tc >> 5) & 1;
    var bit8 = (tc >> 8) & 1;
    var hhBit = (bit2 ^ bit7) | (bit8 ^ bit5) | (bit5 ^ bit7);

    // High-Hat (ch7 modulator): two-state noisy phase.
    var hhPhase = hhBit != 0
      ? (noise != 0 ? 0x2D0 : 0x234)
      : (noise != 0 ? 0x34 : 0x0D0);
    acc += this.RhythmOperatorOutput(ch7.Modulator, hhPhase);

    // Snare Drum (ch7 carrier): bit8 of own phase XOR noise.
    var sd = (int)(ch7.Carrier.Phase >> 9);
    var sdBit = (sd >> 8) & 1;
    var sdPhase = (sdBit << 9) | ((sdBit ^ noise) << 8);
    acc += this.RhythmOperatorOutput(ch7.Carrier, sdPhase & 0x3FF);

    // Tom-Tom (ch8 modulator): plain tone from its own phase.
    acc += this.RhythmOperatorOutput(ch8.Modulator, (int)(ch8.Modulator.Phase >> 9) & 0x3FF);

    // Top-Cymbal (ch8 carrier): two-state phase from the HH/CYM decision.
    var tcPhase = hhBit != 0 ? 0x300 : 0x100;
    acc += this.RhythmOperatorOutput(ch8.Carrier, tcPhase);

    return acc;
  }

  private int RhythmOperatorOutput(OplOperator op, int phase) {
    var att = op.EnvLevel + (op.TotalLevel << 2) + op.KslAttenuation();
    if (op.Tremolo)
      att += this.TremoloOffset();
    if (att > OplOperator.MaxAttenuation)
      att = OplOperator.MaxAttenuation;
    return Waveform(op.Waveform, phase, att << 4);
  }

  // ── envelope generator ──────────────────────────────────────────────────────

  private void AdvanceEnvelopes() {
    ++this._egTimer;
    foreach (var ch in this._channels) {
      this.AdvanceOperatorEnvelope(ch.Modulator);
      this.AdvanceOperatorEnvelope(ch.Carrier);
    }
  }

  private void AdvanceOperatorEnvelope(OplOperator op) {
    var rate = op.State switch {
      OplOperator.EgState.Attack => op.AttackRate,
      OplOperator.EgState.Decay => op.DecayRate,
      OplOperator.EgState.Sustain => op.EgSustain ? 0 : op.ReleaseRate,
      _ => op.ReleaseRate,
    };

    var effective = op.EffectiveRate(rate, this._nts);
    if (effective == 0)
      return;

    var step = OplTables.EnvelopeIncrement(effective, (int)(this._egTimer & 0xFFFF));
    if (step == 0)
      return;

    var sustainEg = op.SustainLevel == 0x0F ? OplOperator.MaxAttenuation : op.SustainLevel << 4;

    switch (op.State) {
      case OplOperator.EgState.Attack:
        // Logarithmic attack toward 0.
        op.EnvLevel += (~op.EnvLevel * step) >> 3;
        if (op.EnvLevel <= 0 || effective >= 60) {
          op.EnvLevel = 0;
          op.State = OplOperator.EgState.Decay;
        }
        break;
      case OplOperator.EgState.Decay:
        op.EnvLevel += step;
        if (op.EnvLevel >= sustainEg) {
          op.EnvLevel = sustainEg;
          op.State = OplOperator.EgState.Sustain;
        }
        break;
      case OplOperator.EgState.Sustain:
      case OplOperator.EgState.Release:
        op.EnvLevel += step;
        if (op.EnvLevel >= OplOperator.MaxAttenuation)
          op.EnvLevel = OplOperator.MaxAttenuation;
        break;
    }
  }

  // ── LFO + noise ─────────────────────────────────────────────────────────────

  private void AdvanceLfo() {
    this._lfoAm += AmIncrement;
    this._lfoPm += PmIncrement;
  }

  private const uint AmIncrement = 78;   // ≈ 3.7 Hz tremolo at the native rate
  private const uint PmIncrement = 105;  // ≈ 6.4 Hz vibrato

  private int TremoloOffset() {
    // Triangle 0..26 in 1/256-dB units; depth bit selects ~1.0 dB vs ~4.8 dB.
    var phase = (int)((this._lfoAm >> 7) & 0x3F);
    var tri = phase < 32 ? phase : 63 - phase;
    return this._tremoloDepth != 0 ? tri : tri >> 2;
  }

  private static readonly int[] VibratoTable = [0, 1, 2, 1, 0, -1, -2, -1];

  private int VibratoOffset(int fnum) {
    var pm = VibratoTable[(this._lfoPm >> 10) & 0x07] * (fnum >> 7);
    return this._vibratoDepth != 0 ? pm : pm >> 1;
  }

  private void ClockNoise() {
    var bit = (this._noise ^ (this._noise >> 14)) & 1;
    this._noise = (this._noise >> 1) | (bit << 22);
    if (this._noise == 0)
      this._noise = 1;
  }

  // ── rhythm register ─────────────────────────────────────────────────────────

  private void WriteRhythm(int value) {
    this._tremoloDepth = (value >> 7) & 1;
    this._vibratoDepth = (value >> 6) & 1;
    var enable = (value & 0x20) != 0;
    this._rhythmMode = enable;
    if (!enable)
      return;

    // Bit 4 BD, 3 SD, 2 TT, 1 TC, 0 HH.
    this.KeyRhythm(this._channels[6].Modulator, (value & 0x10) != 0);
    this.KeyRhythm(this._channels[6].Carrier, (value & 0x10) != 0);
    this.KeyRhythm(this._channels[7].Carrier, (value & 0x08) != 0);  // SD
    this.KeyRhythm(this._channels[8].Modulator, (value & 0x04) != 0); // TT
    this.KeyRhythm(this._channels[8].Carrier, (value & 0x02) != 0);   // TC
    this.KeyRhythm(this._channels[7].Modulator, (value & 0x01) != 0); // HH
  }

  private void KeyRhythm(OplOperator op, bool on) {
    if (on) {
      if (op.State == OplOperator.EgState.Release)
        op.KeyOn();
    } else {
      op.KeyOff();
    }
  }

  private static short Clamp16(int value) =>
    value > 32767 ? (short)32767 : value < -32768 ? (short)-32768 : (short)value;
}
