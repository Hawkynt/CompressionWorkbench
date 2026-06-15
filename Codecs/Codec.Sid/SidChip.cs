#pragma warning disable CS1591
namespace Codec.Sid;

/// <summary>
/// A register-level MOS 6581/8580 SID emulator. Driven by <see cref="Write"/> calls into
/// the 25 control registers ($D400-$D418 relative), it synthesises three voices, applies
/// the per-voice ADSR envelopes, routes voices through the multimode filter per the FILT
/// bits, and emits 16-bit mono samples through <see cref="RenderSamples"/>.
/// <para>The chip steps internally at the SID clock rate and decimates to the requested
/// output rate (default 44100 Hz) by averaging every internal step within an output sample
/// window — a cheap anti-aliasing measure rather than a designed decimation filter.</para>
/// </summary>
public sealed class SidChip {

  /// <summary>Default render rate.</summary>
  public const int OutputSampleRate = 44100;

  private readonly SidModel _model;
  private readonly double _clockHz;
  private readonly int _outputRate;

  private readonly SidVoice[] _voices = [new(), new(), new()];
  private readonly SidFilter _filter;

  private readonly byte[] _registers = new byte[0x20];

  // Master volume nibble and routing bits from $D417/$D418.
  private int _volume;
  private bool _voice3Off;
  private readonly bool[] _filterVoice = new bool[3];

  // Fractional accumulator for clock→output decimation.
  private double _clocksPerSample;
  private double _sampleError;

  public SidChip(SidModel model, double clockHz, int outputRate = OutputSampleRate) {
    // Collapse aliases (e.g. the 6582 is electrically an 8580) onto the behaviour the filter implements.
    this._model = model.Resolve();
    this._clockHz = clockHz;
    this._outputRate = outputRate;
    this._filter = new SidFilter(this._model, clockHz);
    this._clocksPerSample = clockHz / outputRate;
  }

  /// <summary>The electrically distinct model this chip behaves as (aliases collapsed; 6582 → 8580).</summary>
  public SidModel Model => this._model;

  /// <summary>Writes a SID control register (<paramref name="reg"/> 0..0x1C relative to $D400).</summary>
  public void Write(int reg, byte value) {
    reg &= 0x1F;
    this._registers[reg] = value;

    switch (reg) {
      // Voice 1
      case 0x00: this._voices[0].WriteFreqLo(value); break;
      case 0x01: this._voices[0].WriteFreqHi(value); break;
      case 0x02: this._voices[0].WritePwLo(value); break;
      case 0x03: this._voices[0].WritePwHi(value); break;
      case 0x04: this._voices[0].WriteControl(value); break;
      case 0x05: this._voices[0].Envelope.WriteAttackDecay(value); break;
      case 0x06: this._voices[0].Envelope.WriteSustainRelease(value); break;
      // Voice 2
      case 0x07: this._voices[1].WriteFreqLo(value); break;
      case 0x08: this._voices[1].WriteFreqHi(value); break;
      case 0x09: this._voices[1].WritePwLo(value); break;
      case 0x0A: this._voices[1].WritePwHi(value); break;
      case 0x0B: this._voices[1].WriteControl(value); break;
      case 0x0C: this._voices[1].Envelope.WriteAttackDecay(value); break;
      case 0x0D: this._voices[1].Envelope.WriteSustainRelease(value); break;
      // Voice 3
      case 0x0E: this._voices[2].WriteFreqLo(value); break;
      case 0x0F: this._voices[2].WriteFreqHi(value); break;
      case 0x10: this._voices[2].WritePwLo(value); break;
      case 0x11: this._voices[2].WritePwHi(value); break;
      case 0x12: this._voices[2].WriteControl(value); break;
      case 0x13: this._voices[2].Envelope.WriteAttackDecay(value); break;
      case 0x14: this._voices[2].Envelope.WriteSustainRelease(value); break;
      // Filter
      case 0x15: this.UpdateCutoff(); break; // FC lo (3 bits)
      case 0x16: this.UpdateCutoff(); break; // FC hi (8 bits)
      case 0x17: // RES/FILT
        this._filter.SetResonance(value >> 4);
        this._filterVoice[0] = (value & 0x01) != 0;
        this._filterVoice[1] = (value & 0x02) != 0;
        this._filterVoice[2] = (value & 0x04) != 0;
        break;
      case 0x18: // MODE/VOL
        this._volume = value & 0x0F;
        this._voice3Off = (value & 0x80) != 0;
        this._filter.SetMode(
          lowPass: (value & 0x10) != 0,
          bandPass: (value & 0x20) != 0,
          highPass: (value & 0x40) != 0);
        break;
    }
  }

  private void UpdateCutoff() {
    // FC is 11 bits: $D415 holds the low 3 bits, $D416 the high 8 bits.
    var fc = (this._registers[0x15] & 0x07) | (this._registers[0x16] << 3);
    this._filter.SetCutoff(fc);
  }

  /// <summary>Renders <paramref name="count"/> mono 16-bit samples into <paramref name="output"/>.</summary>
  public void RenderSamples(Span<short> output, int count) {
    for (var i = 0; i < count; ++i)
      output[i] = this.RenderOneSample();
  }

  private short RenderOneSample() {
    // Step the chip for the (fractional) number of clocks that map to one output sample,
    // averaging the per-clock mix for a basic anti-aliasing decimation.
    this._sampleError += this._clocksPerSample;
    var steps = (int)this._sampleError;
    this._sampleError -= steps;
    if (steps < 1)
      steps = 1;

    var sum = 0.0;
    for (var s = 0; s < steps; ++s)
      sum += this.StepOneClock();

    var avg = sum / steps;
    var scaled = avg * this._volume / 15.0;
    return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
  }

  private double StepOneClock() {
    // Advance oscillators (voice N syncs/ring-mods from voice N-1, wrapping 0←2).
    this._voices[0].Clock(this._voices[2]);
    this._voices[1].Clock(this._voices[0]);
    this._voices[2].Clock(this._voices[1]);

    var unfiltered = 0.0;
    var filtered = 0.0;

    for (var v = 0; v < 3; ++v) {
      if (v == 2 && this._voice3Off && !this._filterVoice[2])
        continue; // 3OFF mutes voice 3 only when it isn't routed through the filter.

      var ringSource = this._voices[(v + 2) % 3];
      var wave = this._voices[v].Output(ringSource) - 0x800; // center to signed
      var env = this._voices[v].Envelope.Level;
      var sample = wave * env; // 12-bit wave * 8-bit env

      if (this._filterVoice[v])
        filtered += sample;
      else
        unfiltered += sample;
    }

    var filterOut = this._filter.Process(filtered);
    // Normalise: wave(±2048) * env(255) → scale into 16-bit headroom across 3 voices.
    var mix = (unfiltered + filterOut) / (2048.0 * 255.0 * 3.0) * short.MaxValue;
    return mix;
  }
}
