#pragma warning disable CS1591
namespace Codec.TrackerXmIt;

/// <summary>
/// One IT playback voice (a sounding sample instance). The engine owns a pool of voices: each of
/// the song's track channels maps to a "primary" voice, and New Note Actions (NNA) move the old
/// voice into a background (virtual) slot so it keeps ringing while a new note plays. Per
/// ITTECH.TXT for note actions, duplicate checks, envelopes and the resonant filter.
/// </summary>
internal sealed class ItVoice {

  public bool Active;
  public bool Background;        // moved here by an NNA (continue/off/fade)
  public int OwnerChannel = -1;  // which track channel spawned this voice

  public ItInstrument? Instrument;
  public ItSample? Sample;
  public int Note;               // 0..119
  public int InstrumentIndex;    // 1-based, for DCT

  public double SamplePos;
  public bool Forward = true;
  public bool Playing;

  // Pitch state.
  public int C5Speed;
  public double Frequency;

  // Volume / panning.
  public int Volume = 64;        // channel volume note value 0..64
  public int SampleVolume = 64;  // sample default volume
  public int ChannelVolume = 64; // Mxx
  public int Panning = 128;      // 0..255
  public int Fadeout = 65536;
  public bool KeyOff;
  public bool NoteFade;

  // Envelopes.
  public int VolEnvTick, PanEnvTick, PitchEnvTick;
  public int VolEnvNode, PanEnvNode, PitchEnvNode;

  // Filter.
  public readonly ItFilter Filter = new();
  public int FilterCutoff = 127;
  public int FilterResonance;
  public bool FilterSet;

  // Auto-vibrato.
  public int AutoVibratoPos, AutoVibratoSweep;

  // Computed gains for the current tick.
  private float _leftGain, _rightGain;

  public void Clear() {
    this.Active = false;
    this.Background = false;
    this.OwnerChannel = -1;
    this.Instrument = null;
    this.Sample = null;
    this.Playing = false;
    this.SamplePos = 0;
    this.Forward = true;
    this.KeyOff = false;
    this.NoteFade = false;
    this.Fadeout = 65536;
    this.VolEnvTick = this.PanEnvTick = this.PitchEnvTick = 0;
    this.VolEnvNode = this.PanEnvNode = this.PitchEnvNode = 0;
    this.AutoVibratoPos = this.AutoVibratoSweep = 0;
    this.Filter.Reset();
    this.FilterSet = false;
    this.FilterCutoff = 127;
    this.FilterResonance = 0;
  }

  // ── per-tick state update ──────────────────────────────────────────────────────

  public void UpdateEnvelopesAndFade(int sampleRate, double pitchBendSemis) {
    var envVol = 64;
    if (this.Instrument is { } ins) {
      if (ins.VolumeEnvelope.Enabled && ins.VolumeEnvelope.Nodes.Length > 0)
        envVol = EvalEnvelope(ins.VolumeEnvelope, ref this.VolEnvTick, this.KeyOff, 64);

      if (ins.PanningEnvelope.Enabled && ins.PanningEnvelope.Nodes.Length > 0) {
        var p = EvalEnvelope(ins.PanningEnvelope, ref this.PanEnvTick, this.KeyOff, 32); // -32..32
        this.Panning = Math.Clamp(this.Panning + p * 4, 0, 255);
      }

      if (ins.PitchEnvelope.Enabled && ins.PitchEnvelope.Nodes.Length > 0) {
        var pe = EvalEnvelopeSigned(ins.PitchEnvelope, ref this.PitchEnvTick, this.KeyOff);
        if (ins.PitchEnvelope.IsFilter) {
          // Pitch envelope acts on the filter cutoff (range scaled to 0..127).
          this.FilterCutoff = Math.Clamp(this.FilterCutoff + pe, 0, 127);
          this.FilterSet = true;
        } else {
          pitchBendSemis += pe / 8.0; // 32 units ≈ 4 semitones
        }
      }

      if (this.NoteFade && ins.Fadeout > 0) {
        this.Fadeout -= ins.Fadeout * 2;
        if (this.Fadeout <= 0) { this.Fadeout = 0; this.Playing = false; this.Active = false; }
      }
    }

    this._envVol = envVol;
    this._pitchBendSemis = pitchBendSemis;

    if (this.FilterSet)
      this.Filter.Set(this.FilterCutoff, this.FilterResonance, sampleRate);
  }

  private int _envVol = 64;
  private double _pitchBendSemis;

  public void ComputeGains(float masterGain) {
    var vol = this.Volume / 64.0f;
    var sv = this.SampleVolume / 64.0f;
    var cv = this.ChannelVolume / 64.0f;
    var env = this._envVol / 64.0f;
    var fade = this.Fadeout / 65536.0f;
    var amp = vol * sv * cv * env * fade * masterGain;
    var pan = Math.Clamp(this.Panning, 0, 255) / 255.0f;
    this._leftGain = amp * (1f - pan);
    this._rightGain = amp * pan;
  }

  // ── envelope evaluation ─────────────────────────────────────────────────────────

  private static int EvalEnvelope(ItEnvelope env, ref int tick, bool keyOff, int scaleMax) {
    var y = SampleEnvelope(env, tick);
    AdvanceEnvelope(env, ref tick, keyOff);
    return Math.Clamp(y, scaleMax == 32 ? -32 : 0, scaleMax == 32 ? 32 : scaleMax);
  }

  private static int EvalEnvelopeSigned(ItEnvelope env, ref int tick, bool keyOff) {
    var y = SampleEnvelope(env, tick);
    AdvanceEnvelope(env, ref tick, keyOff);
    return y;
  }

  private static int SampleEnvelope(ItEnvelope env, int tick) {
    var nodes = env.Nodes;
    if (nodes.Length == 0) return 0;
    if (tick <= nodes[0].Tick) return nodes[0].Y;
    for (var i = 0; i < nodes.Length - 1; ++i) {
      var (t0, y0) = nodes[i];
      var (t1, y1) = nodes[i + 1];
      if (tick >= t0 && tick <= t1) {
        var span = Math.Max(1, t1 - t0);
        return y0 + (y1 - y0) * (tick - t0) / span;
      }
    }
    return nodes[^1].Y;
  }

  private static void AdvanceEnvelope(ItEnvelope env, ref int tick, bool keyOff) {
    var nodes = env.Nodes;
    if (nodes.Length == 0) return;

    if (env.Sustain && !keyOff && env.SustainEnd < nodes.Length) {
      if (tick >= nodes[env.SustainEnd].Tick) { tick = nodes[Math.Clamp(env.SustainStart, 0, nodes.Length - 1)].Tick; return; }
    }
    if (env.Loop && env.LoopEnd < nodes.Length) {
      if (tick >= nodes[env.LoopEnd].Tick) { tick = nodes[Math.Clamp(env.LoopStart, 0, nodes.Length - 1)].Tick; return; }
    }
    if (tick < nodes[^1].Tick) ++tick;
  }

  // ── mixing ────────────────────────────────────────────────────────────────────

  public void Mix(float[] stereoBuffer, int frames, int sampleRate) {
    if (!this.Playing || this.Sample == null || this.Sample.Pcm.Length == 0) return;
    var pcm = this.Sample.Pcm;

    var freq = this.Frequency * Math.Pow(2.0, this._pitchBendSemis / 12.0);
    var step = freq / sampleRate;

    var useSustain = this.Sample.SustainLoop && !this.KeyOff;
    var loopStart = useSustain ? this.Sample.SustainStart : this.Sample.LoopStart;
    var loopEnd = useSustain ? this.Sample.SustainEnd : this.Sample.LoopEnd;
    var pingPong = useSustain ? this.Sample.SustainPingPong : this.Sample.PingPong;
    var looping = useSustain ? this.Sample.SustainLoop : this.Sample.Loop;
    if (loopEnd > pcm.Length) loopEnd = pcm.Length;

    var lg = this._leftGain;
    var rg = this._rightGain;
    var filterActive = this.Filter.Active;

    for (var f = 0; f < frames; ++f) {
      var idx = (int)this.SamplePos;
      if (idx < 0 || idx >= pcm.Length) { this.Playing = false; this.Active = false; break; }
      float s = pcm[idx];
      if (filterActive) s = this.Filter.Process(s);
      stereoBuffer[f * 2] += s * lg;
      stereoBuffer[f * 2 + 1] += s * rg;

      if (this.Forward) this.SamplePos += step; else this.SamplePos -= step;

      if (looping && loopEnd > loopStart) {
        if (pingPong) {
          if (this.Forward && this.SamplePos >= loopEnd) { this.Forward = false; this.SamplePos = loopEnd - (this.SamplePos - loopEnd); }
          else if (!this.Forward && this.SamplePos <= loopStart) { this.Forward = true; this.SamplePos = loopStart + (loopStart - this.SamplePos); }
        } else if (this.SamplePos >= loopEnd) {
          this.SamplePos = loopStart + (this.SamplePos - loopEnd);
        }
      } else if (this.SamplePos >= pcm.Length) {
        this.Playing = false;
        this.Active = false;
        break;
      }
    }
  }
}
