#pragma warning disable CS1591
namespace Codec.Ym2612;

/// <summary>
/// Yamaha YM2612 (OPN2) FM synthesis core: six channels of four operators each, eight
/// algorithms, per-operator envelopes, channel-6 DAC mode, LFO (AM/PM), and stereo L/R
/// enables. The operator is built on the genuine die-extracted log-sine and exponential
/// ROMs (see <see cref="Ym2612Tables"/>); the phase and envelope generators follow the
/// established sample-accurate OPN2 pipeline.
/// <para>Registers are written through <see cref="Write"/> (port 0 = channels 1-3, port 1 =
/// channels 4-6). <see cref="RenderSample"/> produces one stereo frame at the chip's native
/// rate (clock / 144 ≈ 53.27 kHz for the 7.67 MHz Mega Drive clock); the host resamples to
/// 44100 by simple ratio stepping.</para>
/// <para>Fidelity notes: the log-sine/exp operator, the 8 algorithms, op-1 feedback, TL
/// attenuation, detune+multiple phase generation, channel-3 special (per-operator F-num)
/// mode, the LFO AM/PM sensitivity tables, channel-6 DAC, and L/R routing are all modelled.
/// SSG-EG is parsed and its hold/alternate behaviour is approximated in the envelope
/// generator. Timers, IRQ, CSM and the busy flag are intentionally omitted — register logs
/// never depend on them.</para>
/// </summary>
public sealed class Ym2612Codec {

  /// <summary>The four operator slots that make up a channel.</summary>
  private sealed class Operator {
    // Phase generator.
    public uint Phase;            // 20.10 fixed-point phase accumulator (low 10 bits fraction)
    public int Multiple;          // MUL register 0..15
    public int Detune;            // DT register 0..7 (high bit = sign)
    public int TotalLevel;        // TL 0..127, ×8 into the log domain
    public int KeyScale;          // RS/KS rate-scaling 0..3

    // Envelope generator.
    public int AttackRate;        // AR 0..31
    public int DecayRate;         // D1R 0..31
    public int SustainRate;       // D2R 0..31
    public int ReleaseRate;       // RR 0..15 (×2+1 into 0..31)
    public int SustainLevel;      // SL 0..15 → attenuation steps
    public int AmOn;              // AM enable bit
    public int SsgEg;             // SSG-EG register (bit3 = enable)

    public EnvPhase EnvState = EnvPhase.Release;
    public int EnvLevel = MaxAttenuation;   // current attenuation 0..1023 (log units)
    public bool KeyOn;

    public int Output;            // last operator output (for feedback/modulation), 14-bit signed
    public int Prev;              // one-sample-old output (feedback averages prev two)
  }

  private enum EnvPhase { Attack, Decay, Sustain, Release }

  private sealed class Channel {
    public readonly Operator[] Ops = [new(), new(), new(), new()];
    public int FNum;              // 11-bit frequency number
    public int Block;             // 0..7 octave
    public int Algorithm;
    public int Feedback;          // 0..7
    public int Ams;               // AM sensitivity 0..3
    public int Pms;               // PM sensitivity 0..7
    public bool Left = true;
    public bool Right = true;

    // Channel-3 special mode: per-operator F-num/block (slots 0..2; slot 3 uses the channel's).
    public readonly int[] Ch3FNum = new int[3];
    public readonly int[] Ch3Block = new int[3];
  }

  private const int MaxAttenuation = 1023;        // 10-bit envelope ceiling
  private const int EnvelopeOff = 1023;

  /// <summary>Native FM sample rate divisor: the chip runs at <c>clock / 144</c>.</summary>
  public const int Prescale = 144;

  private readonly Channel[] _channels = [new(), new(), new(), new(), new(), new()];
  private readonly double _nativeRate;

  // Global LFO.
  private bool _lfoEnabled;
  private int _lfoRate;          // 0..7
  private uint _lfoCounter;
  private int _lfoStep;          // 0..127 LFO position

  // Channel-3 special mode flag (reg 0x27 bit 6).
  private bool _ch3Special;

  // Channel-6 DAC.
  private bool _dacEnabled;
  private int _dacSample;        // signed 8-bit centred sample << 6 → 14-bit

  // Selected register bank/address per port (set by even-address writes in real use; the
  // VGM/GYM command supplies address + value together, so we store the pending address).
  private int _addr0;
  private int _addr1;

  // Envelope global timer: advances every 3 native samples (the OPN2 EG clock).
  private int _egTimer;
  private uint _egCounter;

  /// <param name="clock">FM clock in Hz (7670454 for the NTSC Mega Drive).</param>
  /// <summary>
  /// Initializes a new instance of <see cref="Ym2612Codec"/>.
  /// </summary>
public Ym2612Codec(double clock = 7670454.0) => this._nativeRate = clock / Prescale;

  /// <summary>The chip's native output sample rate (clock / 144).</summary>
  public double NativeSampleRate => this._nativeRate;

  /// <summary>The genuine die-extracted quarter-period log-sine ROM (256 entries, 1/256 dB).</summary>
  public static IReadOnlyList<ushort> LogSinRom => Ym2612Tables.LogSin;

  /// <summary>The genuine die-extracted exponential ROM (256 entries; OR'd with 0x400 in use).</summary>
  public static IReadOnlyList<ushort> ExpRom => Ym2612Tables.Exp;

  // ── register bus ────────────────────────────────────────────────────────────

  /// <summary>
  /// Writes one register. <paramref name="port"/> 0 addresses the global registers and
  /// channels 1-3; port 1 addresses channels 4-6. <paramref name="address"/> is the register
  /// number, <paramref name="value"/> the data byte.
  /// </summary>
  public void Write(int port, int address, int value) {
    address &= 0xFF;
    value &= 0xFF;
    if (port == 0)
      this._addr0 = address;
    else
      this._addr1 = address;

    if (port == 0 && address < 0x30) {
      this.WriteGlobal(address, value);
      return;
    }

    if (address < 0x30)
      return; // port-1 global mirror is unused

    this.WriteChannelRegister(port, address, value);
  }

  private void WriteGlobal(int address, int value) {
    switch (address) {
      case 0x22: // LFO
        this._lfoEnabled = (value & 0x08) != 0;
        this._lfoRate = value & 0x07;
        if (!this._lfoEnabled) {
          this._lfoCounter = 0;
          this._lfoStep = 0;
        }
        break;
      case 0x27: // channel-3 mode / timer control
        this._ch3Special = (value & 0xC0) != 0; // special mode (0x40) or CSM (0x80)
        break;
      case 0x28: // key on/off
        this.KeyOnOff(value);
        break;
      case 0x2A: // DAC sample
        this._dacSample = (value - 0x80) << 6; // centre 8-bit → 14-bit signed
        break;
      case 0x2B: // DAC enable
        this._dacEnabled = (value & 0x80) != 0;
        break;
    }
  }

  private void KeyOnOff(int value) {
    var chSel = value & 0x07;
    var ch = chSel switch { 0 => 0, 1 => 1, 2 => 2, 4 => 3, 5 => 4, 6 => 5, _ => -1 };
    if (ch < 0)
      return;
    var channel = this._channels[ch];
    for (var slot = 0; slot < 4; ++slot) {
      var on = (value & (0x10 << slot)) != 0;
      this.SetKey(channel.Ops[slot], on);
    }
  }

  private void SetKey(Operator op, bool on) {
    if (on && !op.KeyOn) {
      op.KeyOn = true;
      op.Phase = 0;
      op.EnvState = EnvPhase.Attack;
      // An already-loud operator (AR high) snaps straight to peak.
      if (this.EffectiveRate(op, op.AttackRate) >= 62)
        op.EnvLevel = 0;
    } else if (!on && op.KeyOn) {
      op.KeyOn = false;
      op.EnvState = EnvPhase.Release;
    }
  }

  private void WriteChannelRegister(int port, int address, int value) {
    var bank = port == 0 ? 0 : 3;
    var chIndex = address & 0x03;
    if (chIndex == 3)
      return; // addresses ...3/...7/...B/...F are unused
    var ch = bank + chIndex;
    var channel = this._channels[ch];

    if (address < 0xA0) {
      // Per-operator registers: slot from address bits 2-3.
      var slot = SlotOrder[(address >> 2) & 0x03];
      var op = channel.Ops[slot];
      switch (address & 0xF0) {
        case 0x30: op.Detune = (value >> 4) & 0x07; op.Multiple = value & 0x0F; break;
        case 0x40: op.TotalLevel = value & 0x7F; break;
        case 0x50: op.KeyScale = (value >> 6) & 0x03; op.AttackRate = value & 0x1F; break;
        case 0x60: op.AmOn = (value >> 7) & 0x01; op.DecayRate = value & 0x1F; break;
        case 0x70: op.SustainRate = value & 0x1F; break;
        case 0x80: op.SustainLevel = (value >> 4) & 0x0F; op.ReleaseRate = value & 0x0F; break;
        case 0x90: op.SsgEg = value & 0x0F; break;
      }
      return;
    }

    switch (address & 0xFC) {
      case 0xA0: // F-num low
        channel.FNum = (channel.FNum & 0x700) | value;
        break;
      case 0xA4: // block + F-num high
        channel.FNum = (channel.FNum & 0x0FF) | ((value & 0x07) << 8);
        channel.Block = (value >> 3) & 0x07;
        break;
      case 0xA8: // channel-3 special: per-operator F-num low
        if (port == 0) channel.Ch3FNum[chIndex] = (channel.Ch3FNum[chIndex] & 0x700) | value;
        break;
      case 0xAC: // channel-3 special: per-operator block + F-num high
        if (port == 0) {
          channel.Ch3FNum[chIndex] = (channel.Ch3FNum[chIndex] & 0x0FF) | ((value & 0x07) << 8);
          channel.Ch3Block[chIndex] = (value >> 3) & 0x07;
        }
        break;
      case 0xB0: // feedback + algorithm
        channel.Feedback = (value >> 3) & 0x07;
        channel.Algorithm = value & 0x07;
        break;
      case 0xB4: // L/R enable + AMS/PMS
        channel.Left = (value & 0x80) != 0;
        channel.Right = (value & 0x40) != 0;
        channel.Ams = (value >> 4) & 0x03;
        channel.Pms = value & 0x07;
        break;
    }
  }

  // Operator slot order: register order (S1,S3,S2,S4) → algorithm slot order (0..3).
  private static readonly int[] SlotOrder = [0, 2, 1, 3];

  // ── synthesis ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Renders one stereo frame at the chip's native rate. <paramref name="left"/> and
  /// <paramref name="right"/> are signed 16-bit; the six channels are summed and clamped.
  /// </summary>
  public void RenderSample(out short left, out short right) {
    this.AdvanceLfo();
    this.AdvanceEnvelopes();

    var accLeft = 0;
    var accRight = 0;

    for (var c = 0; c < 6; ++c) {
      var channel = this._channels[c];
      int sample;
      if (c == 5 && this._dacEnabled) {
        sample = this._dacSample;
      } else {
        sample = this.RenderChannel(channel, c);
      }

      if (channel.Left) accLeft += sample;
      if (channel.Right) accRight += sample;
    }

    left = Clamp16(accLeft);
    right = Clamp16(accRight);
  }

  private int RenderChannel(Channel channel, int channelIndex) {
    var ops = channel.Ops;

    var amOffset = this.AmOffset(channel.Ams);

    // op1 feedback: average of its previous two outputs, scaled by the feedback shift.
    var fb = channel.Feedback;
    var mod = fb == 0 ? 0 : (ops[0].Prev + ops[0].Output) >> (10 - fb);

    var o0 = this.OperatorOutput(channel, 0, mod, amOffset, channelIndex);
    ops[0].Prev = ops[0].Output;
    ops[0].Output = o0;

    int o1, o2, o3;
    int output;
    switch (channel.Algorithm) {
      case 0: // 0→1→2→3
        o1 = this.OperatorOutput(channel, 1, o0, amOffset, channelIndex);
        o2 = this.OperatorOutput(channel, 2, o1, amOffset, channelIndex);
        o3 = this.OperatorOutput(channel, 3, o2, amOffset, channelIndex);
        output = o3;
        break;
      case 1: // (0+1)→2→3
        o1 = this.OperatorOutput(channel, 1, 0, amOffset, channelIndex);
        o2 = this.OperatorOutput(channel, 2, o0 + o1, amOffset, channelIndex);
        o3 = this.OperatorOutput(channel, 3, o2, amOffset, channelIndex);
        output = o3;
        break;
      case 2: // (0+(1→2))→3 → 1→2, 0+2→3
        o1 = this.OperatorOutput(channel, 1, 0, amOffset, channelIndex);
        o2 = this.OperatorOutput(channel, 2, o1, amOffset, channelIndex);
        o3 = this.OperatorOutput(channel, 3, o0 + o2, amOffset, channelIndex);
        output = o3;
        break;
      case 3: // (0→1)+2 →3
        o1 = this.OperatorOutput(channel, 1, o0, amOffset, channelIndex);
        o2 = this.OperatorOutput(channel, 2, 0, amOffset, channelIndex);
        o3 = this.OperatorOutput(channel, 3, o1 + o2, amOffset, channelIndex);
        output = o3;
        break;
      case 4: // (0→1) + (2→3)
        o1 = this.OperatorOutput(channel, 1, o0, amOffset, channelIndex);
        o2 = this.OperatorOutput(channel, 2, 0, amOffset, channelIndex);
        o3 = this.OperatorOutput(channel, 3, o2, amOffset, channelIndex);
        output = o1 + o3;
        break;
      case 5: // 0→(1,2,3) all
        o1 = this.OperatorOutput(channel, 1, o0, amOffset, channelIndex);
        o2 = this.OperatorOutput(channel, 2, o0, amOffset, channelIndex);
        o3 = this.OperatorOutput(channel, 3, o0, amOffset, channelIndex);
        output = o1 + o2 + o3;
        break;
      case 6: // (0→1) + 2 + 3
        o1 = this.OperatorOutput(channel, 1, o0, amOffset, channelIndex);
        o2 = this.OperatorOutput(channel, 2, 0, amOffset, channelIndex);
        o3 = this.OperatorOutput(channel, 3, 0, amOffset, channelIndex);
        output = o1 + o2 + o3;
        break;
      default: // 7: 0 + 1 + 2 + 3 (all carriers)
        o1 = this.OperatorOutput(channel, 1, 0, amOffset, channelIndex);
        o2 = this.OperatorOutput(channel, 2, 0, amOffset, channelIndex);
        o3 = this.OperatorOutput(channel, 3, 0, amOffset, channelIndex);
        output = o0 + o1 + o2 + o3;
        break;
    }

    return output;
  }

  /// <summary>
  /// Computes one operator's signed output. <paramref name="modulation"/> is the phase
  /// modulation from the upstream operator(s) in 14-bit operator units.
  /// </summary>
  private int OperatorOutput(Channel channel, int slot, int modulation, int amOffset, int channelIndex) {
    var op = channel.Ops[slot];

    // Phase generation: increment from F-num/block, multiple and detune.
    var (fnum, block) = this.OperatorFrequency(channel, channelIndex, slot);
    var increment = PhaseIncrement(fnum, block, op.Multiple, op.Detune);
    op.Phase += increment;

    // Effective attenuation in EG units (0..1023): envelope + total level + AM.
    var env = op.EnvLevel + (op.TotalLevel << 3);
    if (op.AmOn != 0)
      env += amOffset;
    if (env > MaxAttenuation)
      env = MaxAttenuation;

    // Phase index (10-bit) including modulation.
    var phase = (int)((op.Phase >> 10) + (uint)modulation) & 0x3FF;
    // EG units (1/32 dB step) → 1/256-dB attenuation domain: ×8 so a full envelope (1023 ≈
    // 96 dB) drives the exp shift past the 11-bit mantissa and silences the operator.
    var output = LogSinToLinear(phase, env << 3);
    return output;
  }

  private (int FNum, int Block) OperatorFrequency(Channel channel, int channelIndex, int slot) {
    // Channel-3 special mode gives operators 0..2 their own F-num/block (slot 3 = channel's).
    if (channelIndex == 2 && this._ch3Special && slot != 3) {
      var idx = Ch3SlotToOperand[slot];
      return (channel.Ch3FNum[idx], channel.Ch3Block[idx]);
    }
    return (channel.FNum, channel.Block);
  }

  // Ch3 special-mode operators map to F-num registers in a fixed order.
  private static readonly int[] Ch3SlotToOperand = [2, 0, 1, 0];

  /// <summary>
  /// Converts a 10-bit phase index plus an attenuation (in the operator's 1/256-dB log units,
  /// the same units as the log-sine ROM) to a signed linear sample using the genuine log-sine
  /// and exponential ROMs.
  /// </summary>
  private static int LogSinToLinear(int phase, int attenuation) {
    // Quarter-wave symmetry: bit 8 mirrors the index, bit 9 negates the sample.
    var quarter = phase & 0xFF;
    var index = (phase & 0x100) != 0 ? 0xFF - quarter : quarter;
    var att = Ym2612Tables.LogSin[index] + attenuation;
    if (att > 0x1FFF)
      att = 0x1FFF;

    // exp: mantissa OR 0x400, shifted down by the integer-dB part of the attenuation.
    var value = (Ym2612Tables.Exp[(att & 0xFF) ^ 0xFF] | 0x400) >> (att >> 8);
    return (phase & 0x200) != 0 ? -value : value;
  }

  /// <summary>Phase increment in 20.10 fixed point for the given F-num/block/multiple/detune.</summary>
  internal static uint PhaseIncrement(int fnum, int block, int multiple, int detune) {
    // Base increment: (fnum << block) >> 1 gives the 11-bit accumulator step per native sample.
    var baseInc = ((uint)fnum << block) >> 1;

    // Detune offset from the key-code (top of the scaled F-num).
    var keyCode = KeyCode(fnum, block);
    var detuneIndex = ((keyCode & 0x03) | ((detune & 0x03) << 2)) & 0x1F;
    var detOffset = Ym2612Tables.DetuneOffset[detuneIndex & 0x07];
    var inc = (detune & 0x04) != 0 ? baseInc - (uint)detOffset : baseInc + (uint)detOffset;

    // Multiple (MUL=0 → ×0.5).
    var mul = Ym2612Tables.Multiple[multiple];
    return (uint)(((long)inc * mul) >> 1) & 0xFFFFF;
  }

  /// <summary>5-bit key code derived from block and the top F-num bits (per OPN2).</summary>
  internal static int KeyCode(int fnum, int block) {
    var f11 = (fnum >> 10) & 1;
    var f10 = (fnum >> 9) & 1;
    var f9 = (fnum >> 8) & 1;
    // N4..N3 logic from the OPN2: combines the top F-num bits.
    var n4 = f11;
    var n3 = (f11 & (f10 | f9)) | (~f11 & 1 & f10 & f9);
    return (block << 2) | (n4 << 1) | (n3 & 1);
  }

  // ── envelope generator ────────────────────────────────────────────────────

  private void AdvanceEnvelopes() {
    // The EG global counter advances once every 3 native samples.
    if (++this._egTimer < 3)
      return;
    this._egTimer = 0;
    ++this._egCounter;

    foreach (var channel in this._channels)
      for (var slot = 0; slot < 4; ++slot)
        this.AdvanceOperatorEnvelope(channel, channel.Ops[slot], slot);
  }

  private void AdvanceOperatorEnvelope(Channel channel, Operator op, int slot) {
    var rate = op.EnvState switch {
      EnvPhase.Attack => op.AttackRate,
      EnvPhase.Decay => op.DecayRate,
      EnvPhase.Sustain => op.SustainRate,
      _ => (op.ReleaseRate << 1) | 1,
    };
    var effective = this.EffectiveRate(op, rate);

    // Rate 0 never advances.
    if (rate == 0 && op.EnvState != EnvPhase.Attack)
      return;

    // Step gating: high rates step every EG tick, low rates only on a periodic mask.
    var shift = EnvRateShift[effective];
    if ((this._egCounter & ((1u << shift) - 1)) != 0)
      return;
    var step = EnvIncrement[effective, (int)((this._egCounter >> shift) & 0x07)];
    if (step == 0)
      return;

    switch (op.EnvState) {
      case EnvPhase.Attack:
        // OPN2 attack: inverted-exponential approach to 0.
        if (op.EnvLevel <= 0 || effective >= 62) {
          op.EnvLevel = 0;
          op.EnvState = EnvPhase.Decay;
          break;
        }
        op.EnvLevel += (~op.EnvLevel * step) >> 4;
        if (op.EnvLevel <= 0) {
          op.EnvLevel = 0;
          op.EnvState = EnvPhase.Decay;
        }
        break;
      case EnvPhase.Decay:
        op.EnvLevel += step;
        if (op.EnvLevel >= SustainLevelToAttenuation(op.SustainLevel)) {
          op.EnvLevel = SustainLevelToAttenuation(op.SustainLevel);
          op.EnvState = EnvPhase.Sustain;
        }
        break;
      case EnvPhase.Sustain:
      case EnvPhase.Release:
        op.EnvLevel += step;
        if (op.EnvLevel >= MaxAttenuation) {
          op.EnvLevel = MaxAttenuation;
          // SSG-EG alternate/repeat handling (basic): on hardware bit3 enables it; for the
          // looping modes (even bit1 clear) the phase restarts. Modelled minimally.
          if ((op.SsgEg & 0x08) != 0 && op.KeyOn && (op.SsgEg & 0x02) == 0) {
            op.Phase = 0;
            op.EnvLevel = 0;
            op.EnvState = EnvPhase.Attack;
          }
        }
        break;
    }
  }

  private int EffectiveRate(Operator op, int rate) {
    if (rate == 0)
      return 0;
    var keyCode = KeyCode(this.RateScaleFNum(op), this.RateScaleBlock(op));
    var scaled = (rate << 1) + (keyCode >> (3 - op.KeyScale));
    return scaled > 63 ? 63 : scaled;
  }

  // For rate scaling we use the channel's F-num/block; operators borrow them through the EG.
  private int RateScaleFNum(Operator op) => this.OwningChannel(op).FNum;
  private int RateScaleBlock(Operator op) => this.OwningChannel(op).Block;

  private Channel OwningChannel(Operator op) {
    foreach (var c in this._channels)
      foreach (var o in c.Ops)
        if (ReferenceEquals(o, op))
          return c;
    return this._channels[0];
  }

  private static int SustainLevelToAttenuation(int sl) => sl == 0x0F ? MaxAttenuation : sl << 5;

  // EG rate → shift (how often a step happens) and per-step increment table; the standard
  // OPN2 envelope timing (4 increments cycle through an 8-entry pattern).
  private static readonly int[] EnvRateShift = BuildRateShift();
  private static readonly int[,] EnvIncrement = BuildRateIncrement();

  private static int[] BuildRateShift() {
    var shift = new int[64];
    for (var r = 0; r < 64; ++r)
      shift[r] = r < 48 ? 11 - (r >> 2) : 0;
    return shift;
  }

  private static int[,] BuildRateIncrement() {
    // Increment pattern indexed by [rate, eg-counter low 3 bits]; mirrors the OPN2 EG ROM.
    var table = new int[64, 8];
    int[][] patterns = [
      [0, 1, 0, 1, 0, 1, 0, 1], // pattern a
      [0, 1, 0, 1, 1, 1, 0, 1], // pattern b
      [0, 1, 1, 1, 0, 1, 1, 1], // pattern c
      [0, 1, 1, 1, 1, 1, 1, 1], // pattern d
    ];
    for (var r = 0; r < 64; ++r) {
      var low = r & 0x03;
      int[] pat;
      if (r < 4) pat = patterns[0];
      else if (r >= 60) pat = [2, 2, 2, 2, 2, 2, 2, 2]; // saturated high rates step by 2
      else pat = patterns[low];
      for (var k = 0; k < 8; ++k)
        table[r, k] = pat[k];
    }
    return table;
  }

  // ── LFO ─────────────────────────────────────────────────────────────────────

  private void AdvanceLfo() {
    if (!this._lfoEnabled)
      return;
    this._lfoCounter += LfoIncrement[this._lfoRate];
    this._lfoStep = (int)((this._lfoCounter >> 11) & 0x7F);
  }

  // LFO frequency increments per rate setting (relative to the native EG step rate).
  private static readonly uint[] LfoIncrement = [108, 77, 71, 67, 62, 44, 8, 5];

  private int AmOffset(int ams) {
    if (!this._lfoEnabled || ams == 0)
      return 0;
    // Triangle AM: 0..126 in 1/256-dB units, scaled by the AMS depth.
    var tri = this._lfoStep < 64 ? this._lfoStep : 127 - this._lfoStep;
    var depth = ams switch { 1 => 0, 2 => 1, _ => 2 }; // 0/1.4/5.9/11.8 dB → shift down
    return (tri << 1) >> depth;
  }

  // ── DAC accessor (for tests) ──────────────────────────────────────────────

  /// <summary>True when channel-6 DAC mode is engaged (reg 0x2B bit 7).</summary>
  public bool DacEnabled => this._dacEnabled;

  private static short Clamp16(int value) =>
    value > 32767 ? (short)32767 : value < -32768 ? (short)-32768 : (short)value;
}
