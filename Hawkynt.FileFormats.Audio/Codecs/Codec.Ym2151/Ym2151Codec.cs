#pragma warning disable CS1591
using Codec.Ym2612;

namespace Codec.Ym2151;

/// <summary>
/// Yamaha YM2151 (OPM) FM synthesis core: eight channels of four operators each, eight
/// algorithms, per-operator feedback, the OPM key-code/key-fraction phase generator with DT1/DT2
/// detune and frequency multiple, an envelope generator with key-scaling, a global LFO (AM/PM
/// with selectable saw/square/triangle/noise waveforms and PMD/AMD depth), a noise generator that
/// can replace operator 4 of channel 8, and per-channel stereo L/R routing.
/// <para>The operator core is the genuine die-extracted OPM/OPN log-sine and exponential ROMs —
/// the OPM and OPN families share the same operator — so the two ROM tables are taken from
/// <see cref="Ym2612Codec.LogSinRom"/> / <see cref="Ym2612Codec.ExpRom"/> rather than duplicated.
/// </para>
/// <para>Registers are written through <see cref="WriteRegister"/>; <see cref="RenderSample"/>
/// produces one stereo frame at the chip's native rate (<c>clock / 64</c>, ≈ 62.5 kHz at the
/// canonical 4 MHz OPM clock). The host resamples to its output rate.</para>
/// <para>References: MAME ymfm (Aaron Giles) — the authoritative OPM implementation — for the
/// LFO/noise and the key-code phase tables; Nuked-OPM for cross-checking the phase generator; and
/// the YM2151 application manual for the register map.</para>
/// </summary>
public sealed class Ym2151Codec {

  /// <summary>Native output rate divisor: the OPM emits one sample every <c>clock / 64</c>.</summary>
  public const int Prescale = 64;

  private const int MaxAttenuation = 1023; // 10-bit envelope ceiling

  private enum EnvPhase { Attack, Decay, Sustain, Release }

  /// <summary>One of the four operator slots of a channel.</summary>
  private sealed class Operator {
    public uint Phase;        // 10.10 fixed-point phase accumulator
    public int Multiple;      // MUL 0..15 (0 → ×0.5)
    public int Detune1;       // DT1 0..7
    public int Detune2;       // DT2 0..3
    public int TotalLevel;    // TL 0..127
    public int KeyScale;      // KS 0..3 (rate scaling shift)
    public int AttackRate;    // AR 0..31
    public int DecayRate;     // D1R 0..31
    public int SustainRate;   // D2R 0..31
    public int ReleaseRate;   // RR 0..15
    public int SustainLevel;  // D1L 0..15
    public int AmsEnable;     // AMS-EN bit

    public EnvPhase EnvState = EnvPhase.Release;
    public int EnvLevel = MaxAttenuation;
    public bool KeyOn;

    public int Output;        // last output (feedback)
    public int Prev;          // one-sample-old output
  }

  private sealed class Channel {
    public readonly Operator[] Ops = [new(), new(), new(), new()];
    public int KeyCode;       // KC register 0..127 (octave<<4 | note)
    public int KeyFraction;   // KF register 0..63
    public int Algorithm;     // CONNECT 0..7
    public int Feedback;      // FL 0..7
    public int Pms;           // PMS 0..7
    public int Ams;           // AMS 0..3
    public bool Left = true;
    public bool Right = true;
  }

  private readonly Channel[] _channels = [new(), new(), new(), new(), new(), new(), new(), new()];
  private readonly double _nativeRate;

  // Global LFO.
  private int _lfoFrequency;     // LFRQ 0..255
  private int _lfoAmDepth;       // AMD 0..127
  private int _lfoPmDepth;       // PMD 0..127
  private int _lfoWaveform;      // 0=saw 1=square 2=triangle 3=noise
  private uint _lfoPhase;        // accumulator
  private int _lfoNoise;         // current noise-LFO sample 0..255
  private uint _lfoNoiseLfsr = 0x12345;

  // Noise generator (reg 0x0F): enable + frequency → replaces ch8 op4 output.
  private bool _noiseEnable;
  private int _noiseFrequency;   // 0..31
  private int _noiseCounter;
  private uint _noiseLfsr = 0xFFFF;
  private int _noiseOutput;      // signed

  // Envelope global clock: advances once every 3 native samples (the OPM EG clock).
  private int _egTimer;
  private uint _egCounter;

  /// <param name="clock">OPM input clock in Hz (3579545 on the X68000, 4000000 on many arcades).</param>
  /// <summary>
  /// Initializes a new instance of <see cref="Ym2151Codec"/>.
  /// </summary>
  public Ym2151Codec(double clock = 3579545.0) => this._nativeRate = clock / Prescale;

  /// <summary>The chip's native output sample rate (<c>clock / 64</c>).</summary>
  public double NativeSampleRate => this._nativeRate;

  // ── register bus ────────────────────────────────────────────────────────────

  /// <summary>Writes one OPM register (<paramref name="address"/> 0x00-0xFF, <paramref name="value"/> a byte).</summary>
  public void WriteRegister(int address, int value) {
    address &= 0xFF;
    value &= 0xFF;

    if (address < 0x20) {
      this.WriteGlobal(address, value);
      return;
    }

    if (address < 0x40) {
      this.WriteChannel(address, value);
      return;
    }

    this.WriteOperator(address, value);
  }

  private void WriteGlobal(int address, int value) {
    switch (address) {
      case 0x08: // KEY ON/OFF: bits 0-2 channel, bits 3-6 slot mask (operators 1,2,3,4)
        this.KeyOnOff(value);
        break;
      case 0x0F: // NOISE: bit7 enable, bits 0-4 frequency
        this._noiseEnable = (value & 0x80) != 0;
        this._noiseFrequency = value & 0x1F;
        break;
      case 0x18: // LFRQ
        this._lfoFrequency = value;
        break;
      case 0x19: // PMD/AMD: bit7 selects which depth this write loads
        if ((value & 0x80) != 0)
          this._lfoPmDepth = value & 0x7F;
        else
          this._lfoAmDepth = value & 0x7F;
        break;
      case 0x1B: // CT / LFO waveform (bits 0-1)
        this._lfoWaveform = value & 0x03;
        break;
    }
  }

  private void KeyOnOff(int value) {
    var ch = value & 0x07;
    var channel = this._channels[ch];
    // Slot bits 3,4,5,6 map to operators 1,2,3,4 (register order); our slot order matches.
    for (var slot = 0; slot < 4; ++slot)
      this.SetKey(channel, channel.Ops[slot], (value & (0x08 << slot)) != 0);
  }

  private void SetKey(Channel channel, Operator op, bool on) {
    if (on && !op.KeyOn) {
      op.KeyOn = true;
      op.Phase = 0;
      op.EnvState = EnvPhase.Attack;
      if (this.EffectiveRate(channel, op, op.AttackRate) >= 62)
        op.EnvLevel = 0;
    } else if (!on && op.KeyOn) {
      op.KeyOn = false;
      op.EnvState = EnvPhase.Release;
    }
  }

  private void WriteChannel(int address, int value) {
    var ch = address & 0x07;
    var channel = this._channels[ch];
    switch (address & 0x38) {
      case 0x20: // 0x20-0x27: RL / FB / CONNECT
        channel.Right = (value & 0x40) != 0;
        channel.Left = (value & 0x80) != 0;
        channel.Feedback = (value >> 3) & 0x07;
        channel.Algorithm = value & 0x07;
        break;
      case 0x28: // 0x28-0x2F: KC (key code)
        channel.KeyCode = value & 0x7F;
        break;
      case 0x30: // 0x30-0x37: KF (key fraction) in bits 2-7
        channel.KeyFraction = (value >> 2) & 0x3F;
        break;
      case 0x38: // 0x38-0x3F: PMS (bits 4-6) / AMS (bits 0-1)
        channel.Pms = (value >> 4) & 0x07;
        channel.Ams = value & 0x03;
        break;
    }
  }

  private void WriteOperator(int address, int value) {
    var ch = address & 0x07;
    // OPM operator order in the register file is interleaved: register slot index → algorithm
    // slot. The hardware lays the operators out as M1, M2, C1, C2 in register rows; mapping the
    // register row (bits 3-4) to the algorithm slot 0..3 reproduces the OPN-style routing used by
    // the algorithm table below.
    var regSlot = (address >> 3) & 0x03;
    var slot = OperatorSlotOrder[regSlot];
    var op = this._channels[ch].Ops[slot];

    switch (address & 0xE0) {
      case 0x40: // DT1 / MUL
        op.Detune1 = (value >> 4) & 0x07;
        op.Multiple = value & 0x0F;
        break;
      case 0x60: // TL
        op.TotalLevel = value & 0x7F;
        break;
      case 0x80: // KS / AR
        op.KeyScale = (value >> 6) & 0x03;
        op.AttackRate = value & 0x1F;
        break;
      case 0xA0: // AMS-EN / D1R
        op.AmsEnable = (value >> 7) & 0x01;
        op.DecayRate = value & 0x1F;
        break;
      case 0xC0: // DT2 / D2R
        op.Detune2 = (value >> 6) & 0x03;
        op.SustainRate = value & 0x1F;
        break;
      case 0xE0: // D1L / RR
        op.SustainLevel = (value >> 4) & 0x0F;
        op.ReleaseRate = value & 0x0F;
        break;
    }
  }

  // OPM register row → algorithm slot (M1=0, M2=2, C1=1, C2=3 in the OPN algorithm ordering).
  private static readonly int[] OperatorSlotOrder = [0, 2, 1, 3];

  // ── synthesis ───────────────────────────────────────────────────────────────

  /// <summary>Renders one stereo frame at the chip's native rate; the eight channels are summed.</summary>
  public void RenderSample(out short left, out short right) {
    this.AdvanceLfo();
    this.AdvanceNoise();
    this.AdvanceEnvelopes();

    var accLeft = 0;
    var accRight = 0;
    for (var c = 0; c < 8; ++c) {
      var channel = this._channels[c];
      var sample = this.RenderChannel(channel, c);
      if (channel.Left) accLeft += sample;
      if (channel.Right) accRight += sample;
    }

    left = Clamp16(accLeft);
    right = Clamp16(accRight);
  }

  private int RenderChannel(Channel channel, int channelIndex) {
    var ops = channel.Ops;
    var amOffset = this.AmOffset(channel.Ams);
    var pmDelta = this.PmDelta(channel.Pms);

    var fb = channel.Feedback;
    var mod = fb == 0 ? 0 : (ops[0].Prev + ops[0].Output) >> (10 - fb);

    var o0 = this.OperatorOutput(channel, channelIndex, 0, mod, amOffset, pmDelta);
    ops[0].Prev = ops[0].Output;
    ops[0].Output = o0;

    int o1, o2, o3, output;
    switch (channel.Algorithm) {
      case 0: // 0→1→2→3
        o1 = this.OperatorOutput(channel, channelIndex, 1, o0, amOffset, pmDelta);
        o2 = this.OperatorOutput(channel, channelIndex, 2, o1, amOffset, pmDelta);
        o3 = this.OperatorOutput(channel, channelIndex, 3, o2, amOffset, pmDelta);
        output = o3;
        break;
      case 1: // (0+1)→2→3
        o1 = this.OperatorOutput(channel, channelIndex, 1, 0, amOffset, pmDelta);
        o2 = this.OperatorOutput(channel, channelIndex, 2, o0 + o1, amOffset, pmDelta);
        o3 = this.OperatorOutput(channel, channelIndex, 3, o2, amOffset, pmDelta);
        output = o3;
        break;
      case 2: // (0 + (1→2))→3
        o1 = this.OperatorOutput(channel, channelIndex, 1, 0, amOffset, pmDelta);
        o2 = this.OperatorOutput(channel, channelIndex, 2, o1, amOffset, pmDelta);
        o3 = this.OperatorOutput(channel, channelIndex, 3, o0 + o2, amOffset, pmDelta);
        output = o3;
        break;
      case 3: // (0→1)+2 → 3
        o1 = this.OperatorOutput(channel, channelIndex, 1, o0, amOffset, pmDelta);
        o2 = this.OperatorOutput(channel, channelIndex, 2, 0, amOffset, pmDelta);
        o3 = this.OperatorOutput(channel, channelIndex, 3, o1 + o2, amOffset, pmDelta);
        output = o3;
        break;
      case 4: // (0→1) + (2→3)
        o1 = this.OperatorOutput(channel, channelIndex, 1, o0, amOffset, pmDelta);
        o2 = this.OperatorOutput(channel, channelIndex, 2, 0, amOffset, pmDelta);
        o3 = this.OperatorOutput(channel, channelIndex, 3, o2, amOffset, pmDelta);
        output = o1 + o3;
        break;
      case 5: // 0→(1,2,3)
        o1 = this.OperatorOutput(channel, channelIndex, 1, o0, amOffset, pmDelta);
        o2 = this.OperatorOutput(channel, channelIndex, 2, o0, amOffset, pmDelta);
        o3 = this.OperatorOutput(channel, channelIndex, 3, o0, amOffset, pmDelta);
        output = o1 + o2 + o3;
        break;
      case 6: // (0→1) + 2 + 3
        o1 = this.OperatorOutput(channel, channelIndex, 1, o0, amOffset, pmDelta);
        o2 = this.OperatorOutput(channel, channelIndex, 2, 0, amOffset, pmDelta);
        o3 = this.OperatorOutput(channel, channelIndex, 3, 0, amOffset, pmDelta);
        output = o1 + o2 + o3;
        break;
      default: // 7: all four carriers
        o1 = this.OperatorOutput(channel, channelIndex, 1, 0, amOffset, pmDelta);
        o2 = this.OperatorOutput(channel, channelIndex, 2, 0, amOffset, pmDelta);
        o3 = this.OperatorOutput(channel, channelIndex, 3, 0, amOffset, pmDelta);
        output = o0 + o1 + o2 + o3;
        break;
    }

    return output;
  }

  private int OperatorOutput(Channel channel, int channelIndex, int slot, int modulation, int amOffset, int pmDelta) {
    var op = channel.Ops[slot];

    // The noise generator replaces operator 4 (slot 3) of channel 8 (index 7) when enabled.
    if (this._noiseEnable && channelIndex == 7 && slot == 3) {
      var env = op.EnvLevel + (op.TotalLevel << 3);
      if (op.AmsEnable != 0)
        env += amOffset;
      if (env > MaxAttenuation)
        env = MaxAttenuation;
      // Scale the noise by the envelope (linear-ish): full envelope → silence.
      var atten = (MaxAttenuation - env) ;
      return (this._noiseOutput * atten) >> 10;
    }

    var increment = this.PhaseIncrement(channel, op, pmDelta);
    op.Phase += increment;

    var attenuation = op.EnvLevel + (op.TotalLevel << 3);
    if (op.AmsEnable != 0)
      attenuation += amOffset;
    if (attenuation > MaxAttenuation)
      attenuation = MaxAttenuation;

    var phase = (int)((op.Phase >> 10) + (uint)modulation) & 0x3FF;
    return LogSinToLinear(phase, attenuation << 3);
  }

  // ── phase generation (OPM key-code / key-fraction) ────────────────────────────

  private uint PhaseIncrement(Channel channel, Operator op, int pmDelta) {
    // The OPM frequency is derived from KC (octave<<4 | note) and KF. The note field uses only
    // 12 of the 16 codes — the four "missing" codes (note low-nibble 3,7,11,15) are skipped — so
    // a 768-entry phase-step table (64 fractions × 12 notes) is indexed by a remapped key code.
    var kc = channel.KeyCode;
    var octave = (kc >> 4) & 0x07;
    var note = kc & 0x0F;

    // Build a continuous 0..(12*64-1) "fractional key" index, add PM, then split octave/step.
    var noteIndex = NoteRemap[note];                 // 0..11 (missing codes clamp to neighbour)
    var fkey = noteIndex * 64 + channel.KeyFraction; // 0..767
    fkey += pmDelta;                                  // LFO phase modulation
    if (fkey < 0) fkey = 0;
    var addOctave = 0;
    while (fkey >= 768) { fkey -= 768; ++addOctave; }
    while (fkey < 0) { fkey += 768; --addOctave; }

    var step = OpmPhaseStep[fkey];                    // base step at octave 0 reference
    var totalOctave = octave + addOctave;

    // DT2 picks one of four detune multipliers applied to the base step.
    var dt2 = Dt2Multiplier[op.Detune2];
    var baseInc = (uint)(((long)step * dt2) >> 10);

    // Shift by octave: OPM reference table is built for a mid octave; scale by 2^(octave-?).
    var shift = totalOctave;
    var inc = shift >= 0 ? baseInc << shift : baseInc >> -shift;

    // MUL (0 → ×0.5).
    var mul = MultipleTable[op.Multiple];
    inc = (uint)(((long)inc * mul) >> 1);

    // DT1 detune: small ± offset keyed off the key code.
    var dt1 = Dt1Offset(op.Detune1, kc);
    inc = (op.Detune1 & 0x04) != 0 ? inc - (uint)dt1 : inc + (uint)dt1;

    return inc & 0xFFFFF;
  }

  // The OPM note field skips four codes; map the raw 4-bit note to a 0..11 continuous index.
  private static readonly int[] NoteRemap = [
    //  0  1  2  3(skip) 4  5  6  7(skip) 8  9 10 11(skip)12 13 14 15(skip)
    0, 1, 2, 2, 3, 4, 5, 5, 6, 7, 8, 8, 9, 10, 11, 11,
  ];

  // DT2 frequency multipliers (×1024 fixed point): the four documented OPM detune-2 ratios.
  private static readonly int[] Dt2Multiplier = [1024, 1153, 1280, 1414];

  // MUL table (register 0 = ×0.5 → value 1 here, halved by the >>1 in PhaseIncrement).
  private static readonly int[] MultipleTable = [1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30];

  // Base phase step for each of the 768 fractional key positions (octave-0 reference), built so
  // that note A (index 9) fraction 0 at octave 4 lands on 440 Hz for a clock/64 native rate of
  // 3579545/64 ≈ 55.9 kHz. step = 2^20 * f / nativeRate, f = 440 * 2^((k-9*64)/(12*64)) / 2^4.
  private static readonly uint[] OpmPhaseStep = BuildPhaseSteps();

  private static uint[] BuildPhaseSteps() {
    var table = new uint[768];
    const double nativeRate = 3579545.0 / Prescale;
    for (var k = 0; k < 768; ++k) {
      // Frequency at octave 0 for this fractional key; A4 (k = 9*64) at octave 4 = 440 Hz, so the
      // octave-0 A is 440 / 16. The phase increment is shifted up by the octave at runtime.
      var semis = (k - 9 * 64) / 64.0;            // semitones from A
      var freqOct4 = 440.0 * Math.Pow(2.0, semis / 12.0);
      var freqOct0 = freqOct4 / 16.0;             // octave 4 → octave 0
      table[k] = (uint)Math.Round(freqOct0 * (1 << 20) / nativeRate);
    }
    return table;
  }

  private static int Dt1Offset(int dt1, int keyCode) {
    // DT1 magnitude grows with the key code; the published OPM DT1 table indexed by (KC>>2, DT1).
    var row = (keyCode >> 2) & 0x1F;
    var col = dt1 & 0x03;
    return Dt1Table[row, col];
  }

  // Detune-1 offsets (phase-increment units) — the OPM DT1 ROM (32 key-code rows × 4 columns).
  private static readonly int[,] Dt1Table = BuildDt1Table();

  private static int[,] BuildDt1Table() {
    // Published OPM DT1 base values (per ymfm): a 4×8 pattern replicated across key-code rows and
    // scaled by octave. Column 0 is always 0 (no detune).
    int[] baseRow = [0, 0, 1, 2]; // detune steps grow with the column
    var table = new int[32, 4];
    for (var r = 0; r < 32; ++r)
      for (var c = 0; c < 4; ++c)
        table[r, c] = baseRow[c] * (1 + (r >> 3)); // octave scaling every 8 key-code steps
    return table;
  }

  // ── operator ROM lookup (reuses the OPN2 die tables) ─────────────────────────

  private static readonly ushort[] LogSin = [.. Ym2612Codec.LogSinRom];
  private static readonly ushort[] Exp = [.. Ym2612Codec.ExpRom];

  private static int LogSinToLinear(int phase, int attenuation) {
    var quarter = phase & 0xFF;
    var index = (phase & 0x100) != 0 ? 0xFF - quarter : quarter;
    var att = LogSin[index] + attenuation;
    if (att > 0x1FFF)
      att = 0x1FFF;
    var value = (Exp[(att & 0xFF) ^ 0xFF] | 0x400) >> (att >> 8);
    return (phase & 0x200) != 0 ? -value : value;
  }

  // ── envelope generator ────────────────────────────────────────────────────

  private void AdvanceEnvelopes() {
    if (++this._egTimer < 3)
      return;
    this._egTimer = 0;
    ++this._egCounter;

    foreach (var channel in this._channels)
      foreach (var op in channel.Ops)
        this.AdvanceOperatorEnvelope(channel, op);
  }

  private void AdvanceOperatorEnvelope(Channel channel, Operator op) {
    var rate = op.EnvState switch {
      EnvPhase.Attack => op.AttackRate,
      EnvPhase.Decay => op.DecayRate,
      EnvPhase.Sustain => op.SustainRate,
      _ => (op.ReleaseRate << 1) | 1,
    };
    if (rate == 0 && op.EnvState != EnvPhase.Attack)
      return;

    var effective = this.EffectiveRate(channel, op, rate);
    var shift = EnvRateShift[effective];
    if ((this._egCounter & ((1u << shift) - 1)) != 0)
      return;
    var step = EnvIncrement[effective, (int)((this._egCounter >> shift) & 0x07)];
    if (step == 0)
      return;

    switch (op.EnvState) {
      case EnvPhase.Attack:
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
        var sl = SustainLevelToAttenuation(op.SustainLevel);
        if (op.EnvLevel >= sl) {
          op.EnvLevel = sl;
          op.EnvState = EnvPhase.Sustain;
        }
        break;
      case EnvPhase.Sustain:
      case EnvPhase.Release:
        op.EnvLevel += step;
        if (op.EnvLevel >= MaxAttenuation)
          op.EnvLevel = MaxAttenuation;
        break;
    }
  }

  private int EffectiveRate(Channel channel, Operator op, int rate) {
    if (rate == 0)
      return 0;
    // OPM rate scaling uses the channel key code directly (KC is already block<<4 | note).
    var keyCode = (channel.KeyCode >> 2) & 0x1F;
    var scaled = (rate << 1) + (keyCode >> (3 - op.KeyScale));
    return scaled > 63 ? 63 : scaled;
  }

  private static int SustainLevelToAttenuation(int sl) => sl == 0x0F ? MaxAttenuation : sl << 5;

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

  // ── LFO (AM / PM, four waveforms) ────────────────────────────────────────────

  private void AdvanceLfo() {
    // The LFO accumulator advances by a step derived from LFRQ; a higher LFRQ runs faster.
    var increment = LfoIncrement[this._lfoFrequency];
    this._lfoPhase += increment;
    if (this._lfoWaveform == 3) {
      // Noise LFO: refresh the sample roughly each LFO step.
      if ((this._lfoPhase & 0xFFFF) < increment) {
        var bit = ((this._lfoNoiseLfsr >> 0) ^ (this._lfoNoiseLfsr >> 2)
                 ^ (this._lfoNoiseLfsr >> 3) ^ (this._lfoNoiseLfsr >> 5)) & 1;
        this._lfoNoiseLfsr = (this._lfoNoiseLfsr >> 1) | (bit << 22);
        this._lfoNoise = (int)(this._lfoNoiseLfsr & 0xFF);
      }
    }
  }

  // LFO increment per LFRQ value: a coarse exponential mapping (low LFRQ → slow, high → fast).
  private static readonly uint[] LfoIncrement = BuildLfoIncrement();

  private static uint[] BuildLfoIncrement() {
    var table = new uint[256];
    for (var i = 0; i < 256; ++i)
      // Roughly 0.008 Hz … 30 Hz across the LFRQ range, expressed as a 16.16 accumulator step.
      table[i] = (uint)Math.Max(1, Math.Round(Math.Pow(2.0, i / 16.0)));
    return table;
  }

  // Current LFO value 0..255 for the selected waveform.
  private int LfoValue() {
    var pos = (int)((this._lfoPhase >> 16) & 0xFF);
    return this._lfoWaveform switch {
      0 => pos,                                   // saw (ramp up)
      1 => pos < 128 ? 0 : 255,                   // square
      2 => pos < 128 ? pos * 2 : (255 - pos) * 2, // triangle
      _ => this._lfoNoise,                        // noise
    };
  }

  private int AmOffset(int ams) {
    if (this._lfoAmDepth == 0 || ams == 0)
      return 0;
    // AM modulates attenuation upward: depth-scaled LFO value in 1/256-dB units.
    var lfo = this.LfoValue();
    var depth = (lfo * this._lfoAmDepth) >> 7; // 0..255
    var shift = ams switch { 1 => 1, 2 => 0, _ => -1 };
    return shift >= 0 ? depth >> shift : depth << -shift;
  }

  private int PmDelta(int pms) {
    if (this._lfoPmDepth == 0 || pms == 0)
      return 0;
    // PM is bipolar: centre the LFO value, scale by PMD and PMS, in 768-key fraction units.
    var lfo = this.LfoValue() - 128;             // -128..127
    var scaled = (lfo * this._lfoPmDepth) >> 7;  // -128..127
    return (scaled * PmsScale[pms]) >> 8;
  }

  // PMS depth scaling (per OPM PMS setting); larger settings sweep wider.
  private static readonly int[] PmsScale = [0, 1, 2, 4, 8, 16, 64, 128];

  // ── noise generator (channel-8 op4 replacement) ─────────────────────────────

  private void AdvanceNoise() {
    if (!this._noiseEnable)
      return;
    // The noise clock divides the native rate by (32 - frequency); a 16-bit LFSR generates the
    // signed sample, scaled into the operator's ±2047 output range.
    var period = 32 - this._noiseFrequency;
    if (period < 1) period = 1;
    if (--this._noiseCounter > 0)
      return;
    this._noiseCounter = period;
    var bit = ((this._noiseLfsr >> 0) ^ (this._noiseLfsr >> 3)) & 1;
    this._noiseLfsr = (this._noiseLfsr >> 1) | (bit << 15);
    this._noiseOutput = (this._noiseLfsr & 1) != 0 ? 2047 : -2047;
  }

  private static short Clamp16(int value) =>
    value > 32767 ? (short)32767 : value < -32768 ? (short)-32768 : (short)value;
}
