#pragma warning disable CS1591
using Codec.Mos6502;
using Codec.Nes2a03.Expansion;

namespace Codec.Nes2a03;

/// <summary>
/// A register-level emulation of the NES 2A03 audio processing unit (APU). Driven by
/// <see cref="Write"/> calls into the $4000-$4017 register window, it synthesises the five
/// channels — two pulses, triangle, noise and DMC — applies the frame counter that clocks
/// envelopes, linear and length counters, and emits 16-bit mono samples via
/// <see cref="RenderSamples"/>.
/// <para>The chip steps internally at the CPU clock rate (NTSC 1.789773 MHz, PAL 1.662607
/// MHz) and decimates to the requested output rate (default 44100 Hz) by averaging the
/// per-step nonlinear mix across each output-sample window — a cheap anti-aliasing measure
/// rather than a designed decimation filter. The DMC reads its samples through the supplied
/// <see cref="IBus6502"/>.</para>
/// <para>Timing approximations: the APU normally runs every other CPU cycle, but here each
/// channel timer is clocked once per CPU step and the frame counter fires its quarter/half
/// events at the documented per-step rates (≈3729 steps between events at NTSC). The frame
/// IRQ is not generated — NSF playback is play-call driven, not interrupt driven.</para>
/// </summary>
public sealed class Apu2a03 {

  /// <summary>Default render rate.</summary>
  public const int OutputSampleRate = 44100;

  /// <summary>NTSC CPU/APU master clock.</summary>
  public const double NtscClockHz = 1789773.0;

  /// <summary>PAL CPU/APU master clock.</summary>
  public const double PalClockHz = 1662607.0;

  private readonly ApuPulseChannel _pulse1 = new(isPulse1: true);
  private readonly ApuPulseChannel _pulse2 = new(isPulse1: false);
  private readonly ApuTriangleChannel _triangle = new();
  private readonly ApuNoiseChannel _noise = new();
  private readonly ApuDmcChannel _dmc;

  private readonly double _clockHz;
  private readonly int _outputRate;

  // Cartridge expansion sound chips (VRC6/VRC7/FDS/MMC5/N163/S5B), clocked every CPU step and
  // summed linearly on top of the 2A03's nonlinear mix.
  private readonly List<IExpansionAudio> _expansions = [];

  /// <summary>The master clock rate (Hz) the APU and any expansion chips step at.</summary>
  public double ClockHz => this._clockHz;

  /// <summary>Attaches an expansion sound chip to be clocked and mixed alongside the 2A03.</summary>
  internal void AttachExpansion(IExpansionAudio chip) => this._expansions.Add(chip);

  /// <summary>Routes a CPU write to any expansion chip whose register window covers it.</summary>
  internal bool WriteExpansion(ushort addr, byte value) {
    var handled = false;
    foreach (var chip in this._expansions)
      if (chip.HandlesWrite(addr)) {
        chip.Write(addr, value);
        handled = true;
      }
    return handled;
  }

  /// <summary>Routes a CPU read to any expansion chip exposing readable RAM at <paramref name="addr"/>.</summary>
  internal bool ReadExpansion(ushort addr, out byte value) {
    foreach (var chip in this._expansions)
      if (chip.TryRead(addr, out value))
        return true;
    value = 0;
    return false;
  }

  // Frame counter: 4-step (default) or 5-step sequence, clocked once per CPU step. The
  // 2A03's frame sequencer fires four events per ~14915-CPU-cycle period; we approximate
  // the quarter-frame spacing as clock/240 steps and the half-frame as clock/120 steps.
  private bool _fiveStepMode;
  private double _frameStepCounter;
  private readonly double _quarterFramePeriod;
  private int _frameStep;

  // Clock→output decimation accumulator.
  private readonly double _clocksPerSample;
  private double _sampleError;

  // Precomputed nonlinear mixer lookup tables (pulse sum 0..30, tnd index 0..255*...).
  private readonly float[] _pulseTable = new float[31];
  private readonly float[] _tndTable = new float[203];

  /// <summary>
  /// Initializes a new instance of <see cref="Apu2a03"/>.
  /// </summary>
public Apu2a03(IBus6502 bus, double clockHz = NtscClockHz, int outputRate = OutputSampleRate) {
    this._dmc = new ApuDmcChannel(bus);
    this._clockHz = clockHz;
    this._outputRate = outputRate;
    this._clocksPerSample = clockHz / outputRate;
    // Quarter-frame events fire at ~240 Hz on a real 2A03; derive the per-step period.
    this._quarterFramePeriod = clockHz / 240.0;
    this.BuildMixerTables();
  }

  // ── nonlinear mixer ──────────────────────────────────────────────────────────
  //
  // The documented 2A03 mixer is nonlinear:
  //   pulse_out = 95.88 / (8128 / (pulse1 + pulse2) + 100)
  //   tnd_out   = 159.79 / (1 / (triangle/8227 + noise/12241 + dmc/22638) + 100)
  // Both terms are precomputed into lookup tables indexed by the raw channel sums.
  private void BuildMixerTables() {
    this._pulseTable[0] = 0f;
    for (var i = 1; i < this._pulseTable.Length; ++i)
      this._pulseTable[i] = (float)(95.88 / (8128.0 / i + 100.0));

    this._tndTable[0] = 0f;
    for (var i = 1; i < this._tndTable.Length; ++i)
      this._tndTable[i] = (float)(159.79 / (1.0 / (i / 22638.0) + 100.0));
  }

  // ── register interface ─────────────────────────────────────────────────────────

  /// <summary>Writes an APU register (<paramref name="addr"/> in the $4000-$4017 window).</summary>
  public void Write(ushort addr, byte value) {
    switch (addr) {
      case 0x4000: case 0x4001: case 0x4002: case 0x4003:
        this._pulse1.Write(addr - 0x4000, value);
        break;
      case 0x4004: case 0x4005: case 0x4006: case 0x4007:
        this._pulse2.Write(addr - 0x4004, value);
        break;
      case 0x4008: case 0x4009: case 0x400A: case 0x400B:
        this._triangle.Write(addr - 0x4008, value);
        break;
      case 0x400C: case 0x400D: case 0x400E: case 0x400F:
        this._noise.Write(addr - 0x400C, value);
        break;
      case 0x4010: case 0x4011: case 0x4012: case 0x4013:
        this._dmc.Write(addr - 0x4010, value);
        break;
      case 0x4015: // channel enables
        this._pulse1.Enabled = (value & 0x01) != 0;
        this._pulse2.Enabled = (value & 0x02) != 0;
        this._triangle.Enabled = (value & 0x04) != 0;
        this._noise.Enabled = (value & 0x08) != 0;
        this._dmc.SetEnabled((value & 0x10) != 0);
        break;
      case 0x4017: // frame counter mode
        this._fiveStepMode = (value & 0x80) != 0;
        this._frameStep = 0;
        this._frameStepCounter = 0;
        // Writing the 5-step mode immediately clocks a quarter and half frame.
        if (this._fiveStepMode) {
          this.ClockQuarterFrame();
          this.ClockHalfFrame();
        }
        break;
    }
  }

  /// <summary>Reads the $4015 status: bit per channel whose length (or DMC bytes) is active.</summary>
  public byte Read4015() {
    byte status = 0;
    if (this._pulse1.LengthActive) status |= 0x01;
    if (this._pulse2.LengthActive) status |= 0x02;
    if (this._triangle.LengthActive) status |= 0x04;
    if (this._noise.LengthActive) status |= 0x08;
    if (this._dmc.Active) status |= 0x10;
    return status;
  }

  // ── stepping & frame counter ─────────────────────────────────────────────────

  private void ClockQuarterFrame() {
    this._pulse1.ClockEnvelope();
    this._pulse2.ClockEnvelope();
    this._noise.ClockEnvelope();
    this._triangle.ClockLinear();
  }

  private void ClockHalfFrame() {
    this._pulse1.ClockLength();
    this._pulse2.ClockLength();
    this._triangle.ClockLength();
    this._noise.ClockLength();
    this._pulse1.ClockSweep();
    this._pulse2.ClockSweep();
  }

  private void StepFrameCounter() {
    this._frameStepCounter += 1.0;
    if (this._frameStepCounter < this._quarterFramePeriod)
      return;
    this._frameStepCounter -= this._quarterFramePeriod;

    // 4-step: quarter on every step, half on steps 1 and 3 (period 0..3).
    // 5-step: quarter on steps 0,1,2,3 and half on steps 1 and 3; step 4 is idle.
    if (this._fiveStepMode) {
      if (this._frameStep != 4)
        this.ClockQuarterFrame();
      if (this._frameStep is 1 or 3)
        this.ClockHalfFrame();
      this._frameStep = (this._frameStep + 1) % 5;
    } else {
      this.ClockQuarterFrame();
      if (this._frameStep is 1 or 3)
        this.ClockHalfFrame();
      this._frameStep = (this._frameStep + 1) % 4;
    }
  }

  private void StepOneClock() {
    this.StepFrameCounter();
    // The triangle timer is clocked at the full CPU rate; the pulse, noise and DMC timers
    // are clocked every other CPU cycle (the APU divider). The triangle therefore runs at
    // f = clock/(32*(t+1)) and the pulses at f = clock/(16*(t+1)).
    this._triangle.ClockTimer();
    // Expansion chips step at the full CPU clock rate (their own internal dividers handle any
    // further prescaling, e.g. the AY's clock/16 or the OPLL's clock/72).
    for (var i = 0; i < this._expansions.Count; ++i)
      this._expansions[i].ClockOneCpuCycle();
    this._apuCycleToggle = !this._apuCycleToggle;
    if (!this._apuCycleToggle)
      return;
    this._pulse1.ClockTimer();
    this._pulse2.ClockTimer();
    this._noise.ClockTimer();
    this._dmc.ClockTimer();
  }

  private bool _apuCycleToggle;

  private float MixOneClock() {
    var pulseSum = this._pulse1.Output() + this._pulse2.Output();
    var tndIndex = 3 * this._triangle.Output() + 2 * this._noise.Output() + this._dmc.Output();
    var mix = this._pulseTable[pulseSum] + this._tndTable[tndIndex];
    // Expansion chips sum linearly on top of the base 2A03 nonlinear mix.
    for (var i = 0; i < this._expansions.Count; ++i)
      mix += this._expansions[i].Output();
    return mix;
  }

  // ── rendering ─────────────────────────────────────────────────────────────────

  /// <summary>Renders <paramref name="count"/> mono 16-bit samples into <paramref name="output"/>.</summary>
  public void RenderSamples(Span<short> output, int count) {
    for (var i = 0; i < count; ++i)
      output[i] = this.RenderOneSample();
  }

  private short RenderOneSample() {
    this._sampleError += this._clocksPerSample;
    var steps = (int)this._sampleError;
    this._sampleError -= steps;
    if (steps < 1)
      steps = 1;

    var sum = 0.0;
    for (var s = 0; s < steps; ++s) {
      this.StepOneClock();
      sum += this.MixOneClock();
    }

    // The mixer output spans roughly 0..1; center it and scale into 16-bit headroom.
    var avg = sum / steps;
    var scaled = (avg - 0.5) * 2.0 * short.MaxValue;
    return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
  }

  // Internal hooks for unit testing the channel maths without a full player.
  internal void StepForTest() => this.StepOneClock();
  internal float MixForTest() => this.MixOneClock();
}
