#pragma warning disable CS1591
namespace Codec.TrackerXmIt;

/// <summary>
/// One XM voice: holds the active sample/instrument, frequency state, envelope positions,
/// auto-vibrato and fadeout, and the per-effect memory. Per XM.TXT plus OpenMPT/libxmp for
/// volume-column and effect-memory edge cases.
/// </summary>
internal sealed class XmChannel {

  // Active note state.
  private XmInstrument? _instrument;
  private XmSample? _sample;
  private int _note;            // 1..96
  private double _samplePos;
  private double _stepPerOutputSample;
  private bool _pingPongForward = true;
  private bool _playing;

  // Frequency.
  private int _period;
  private int _targetPeriod;    // for tone porta
  private int _finetune;

  // Volume / panning (0..64 vol, 0..255 pan).
  private int _volume = 64;
  private int _panning = 128;
  private float _finalLeftGain;
  private float _finalRightGain;

  // Envelopes.
  private int _volEnvPos;
  private int _panEnvPos;
  private int _fadeoutVol = 65536;
  private bool _keyOff;

  // Auto-vibrato.
  private int _autoVibratoPos;
  private int _autoVibratoSweepPos;

  // Effect memory.
  private int _portaUpMem, _portaDownMem, _tonePortaSpeed;
  private int _vibratoSpeed, _vibratoDepth, _vibratoPos, _vibratoWaveform;
  private int _tremoloSpeed, _tremoloDepth, _tremoloPos, _tremoloWaveform;
  private int _volSlideMem, _globalVolSlideMem;
  private int _panSlideMem, _offsetMem, _tremorMem, _multiRetrigMem;
  private int _patternLoopRow, _patternLoopCount;

  // ── row trigger ───────────────────────────────────────────────────────────────

  public void TriggerRow(XmEngine engine, in XmCell cell, XmModule mod) {
    var effect = cell.Effect;
    var param = cell.Param;

    var hasNote = cell.Note is > 0 and < 97;
    var keyOffNote = cell.Note == 97;

    // Instrument change.
    if (cell.Instrument > 0 && cell.Instrument <= mod.Instruments.Length) {
      this._instrument = mod.Instruments[cell.Instrument - 1];
      if (this._sample != null) {
        this._volume = this._sample.Volume;
        this._panning = this._sample.Panning;
      }
      ResetEnvelopes();
      this._fadeoutVol = 65536;
      this._keyOff = false;
    }

    // EDx note delay: defer the trigger to the right tick.
    var noteDelay = effect == 0x0E && (param >> 4) == 0x0D ? (param & 0x0F) : 0;

    if (keyOffNote) {
      this._keyOff = true;
    } else if (hasNote && noteDelay == 0) {
      var isTonePorta = effect == 0x03 || effect == 0x05 || (cell.Volume >> 4) == 0x0F;
      StartNote(cell.Note, mod, isTonePorta);
    } else if (hasNote && noteDelay > 0) {
      this._pendingNote = cell.Note; // triggered on tick == noteDelay
    }

    // Volume column.
    ApplyVolumeColumn(cell.Volume, tick0: true);

    // Effects (tick-0 part).
    ApplyEffectRow(engine, effect, param, mod);
  }

  private int _pendingNote;

  private void StartNote(int note, XmModule mod, bool tonePorta) {
    if (this._instrument == null) return;
    var mapIndex = Math.Clamp(note - 1, 0, 95);
    var sampleIndex = this._instrument.SampleMap[mapIndex];
    if (sampleIndex >= this._instrument.Samples.Length) return;
    var sample = this._instrument.Samples[sampleIndex];

    this._note = note;
    this._finetune = sample.Finetune;

    var newPeriod = NoteToPeriod(note + sample.RelativeNote, this._finetune, mod.LinearFrequency);
    if (tonePorta && this._sample != null && this._playing) {
      this._targetPeriod = newPeriod;
    } else {
      this._sample = sample;
      this._period = newPeriod;
      this._targetPeriod = newPeriod;
      this._samplePos = 0;
      this._pingPongForward = true;
      this._playing = sample.Pcm.Length > 0;
      this._volume = sample.Volume;
      this._panning = sample.Panning;
      ResetEnvelopes();
      this._fadeoutVol = 65536;
      this._keyOff = false;
      this._autoVibratoPos = 0;
      this._autoVibratoSweepPos = 0;
      if ((this._vibratoWaveform & 0x04) == 0) this._vibratoPos = 0;
      if ((this._tremoloWaveform & 0x04) == 0) this._tremoloPos = 0;
    }
  }

  private void ResetEnvelopes() {
    this._volEnvPos = 0;
    this._panEnvPos = 0;
  }

  // ── per-tick effect continuation ───────────────────────────────────────────────

  public void TickEffects(XmEngine engine, in XmCell cell, XmModule mod, int tick) {
    // Note delay trigger.
    if (cell.Effect == 0x0E && (cell.Param >> 4) == 0x0D && (cell.Param & 0x0F) == tick && this._pendingNote > 0) {
      var isTonePorta = false;
      StartNote(this._pendingNote, mod, isTonePorta);
      this._pendingNote = 0;
      ApplyVolumeColumn(cell.Volume, tick0: true);
    }

    ApplyVolumeColumn(cell.Volume, tick0: false);
    ApplyEffectTick(engine, cell.Effect, cell.Param, mod, tick);
  }

  // ── volume column (XM.TXT $10..$FF) ─────────────────────────────────────────────

  private void ApplyVolumeColumn(int v, bool tick0) {
    if (v == 0) return;
    switch (v >> 4) {
      case 0x1: case 0x2: case 0x3: case 0x4: // set volume 0x10..0x50 → 0..64
        if (tick0) this._volume = Math.Clamp(v - 0x10, 0, 64);
        break;
      case 0x5: // 0x50..0x5F set volume up to 64
        if (tick0) this._volume = Math.Clamp(v - 0x10, 0, 64);
        break;
      case 0x6: // volume slide down
        if (!tick0) this._volume = Math.Clamp(this._volume - (v & 0x0F), 0, 64);
        break;
      case 0x7: // volume slide up
        if (!tick0) this._volume = Math.Clamp(this._volume + (v & 0x0F), 0, 64);
        break;
      case 0x8: // fine volume down
        if (tick0) this._volume = Math.Clamp(this._volume - (v & 0x0F), 0, 64);
        break;
      case 0x9: // fine volume up
        if (tick0) this._volume = Math.Clamp(this._volume + (v & 0x0F), 0, 64);
        break;
      case 0xA: // set vibrato speed
        if (tick0 && (v & 0x0F) != 0) this._vibratoSpeed = v & 0x0F;
        break;
      case 0xB: // vibrato
        if (tick0 && (v & 0x0F) != 0) this._vibratoDepth = v & 0x0F;
        if (!tick0) DoVibrato();
        break;
      case 0xC: // set panning (0xC0..0xCF → 0..255)
        if (tick0) this._panning = (v & 0x0F) * 17;
        break;
      case 0xD: // pan slide left
        if (!tick0) this._panning = Math.Clamp(this._panning - (v & 0x0F), 0, 255);
        break;
      case 0xE: // pan slide right
        if (!tick0) this._panning = Math.Clamp(this._panning + (v & 0x0F), 0, 255);
        break;
      case 0xF: // tone portamento (0xF0..0xFF), value*16 as speed
        if (tick0 && (v & 0x0F) != 0) this._tonePortaSpeed = (v & 0x0F) << 4;
        if (!tick0) DoTonePorta();
        break;
    }
  }

  // ── effects: tick-0 ──────────────────────────────────────────────────────────

  private void ApplyEffectRow(XmEngine engine, int effect, int param, XmModule mod) {
    switch (effect) {
      case 0x00: break; // arpeggio handled per-tick (param 0 = none)
      case 0x01: if (param != 0) this._portaUpMem = param; break;
      case 0x02: if (param != 0) this._portaDownMem = param; break;
      case 0x03: if (param != 0) this._tonePortaSpeed = param; break;
      case 0x04:
        if ((param >> 4) != 0) this._vibratoSpeed = param >> 4;
        if ((param & 0x0F) != 0) this._vibratoDepth = param & 0x0F;
        break;
      case 0x05: break; // tone porta + vol slide (slide on tick)
      case 0x06: break; // vibrato + vol slide
      case 0x07:
        if ((param >> 4) != 0) this._tremoloSpeed = param >> 4;
        if ((param & 0x0F) != 0) this._tremoloDepth = param & 0x0F;
        break;
      case 0x08: this._panning = param; break;
      case 0x09: if (param != 0) this._offsetMem = param; this._samplePos = this._offsetMem * 256; break;
      case 0x0A: if (param != 0) this._volSlideMem = param; break;
      case 0x0B: engine.RequestPositionJump(param); break;
      case 0x0C: this._volume = Math.Clamp(param, 0, 64); break;
      case 0x0D: engine.RequestPatternBreak((param >> 4) * 10 + (param & 0x0F)); break;
      case 0x0E: ApplyExtendedRow(engine, param); break;
      case 0x0F: engine.SetSpeed(param); break;
      case 0x10: engine.SetGlobalVolume(Math.Clamp(param, 0, 64)); break; // Gxx
      case 0x11: if (param != 0) this._globalVolSlideMem = param; break;  // Hxy
      case 0x15: // Lxx set envelope position
        this._volEnvPos = param;
        this._panEnvPos = param;
        break;
      case 0x14: this._keyOff = true; break;                              // Kxx key off
      case 0x19: if (param != 0) this._panSlideMem = param; break;        // Pxy pan slide
      case 0x1B: if (param != 0) this._multiRetrigMem = param; break;     // Rxy multi retrig
      case 0x1D: this._tremorMem = param != 0 ? param : this._tremorMem; break; // Txy tremor
      case 0x21: ApplyExtraFinePorta(param); break;                       // Xnn extra fine porta
    }
  }

  private void ApplyExtendedRow(XmEngine engine, int param) {
    var sub = param >> 4;
    var val = param & 0x0F;
    switch (sub) {
      case 0x1: this._period -= val; break;            // E1x fine porta up (tick 0)
      case 0x2: this._period += val; break;            // E2x fine porta down
      case 0x3: this._vibratoWaveform = val & 0x03; break; // glissando control approximated as waveform store
      case 0x4: this._vibratoWaveform = val; break;    // E4x vibrato waveform
      case 0x5: this._finetune = (val - 8) * 16; break;// E5x set finetune
      case 0x6: // E6x pattern loop
        if (val == 0) { this._patternLoopRow = engine.CurrentRowForLoop(); }
        else {
          if (this._patternLoopCount == 0) this._patternLoopCount = val;
          else --this._patternLoopCount;
          if (this._patternLoopCount > 0) engine.RequestPatternLoopJump(this._patternLoopRow);
        }
        break;
      case 0x7: this._tremoloWaveform = val; break;    // E7x tremolo waveform
      case 0x8: this._panning = val * 17; break;       // E8x coarse pan
      case 0xA: this._volume = Math.Clamp(this._volume + val, 0, 64); break; // EAx fine vol up
      case 0xB: this._volume = Math.Clamp(this._volume - val, 0, 64); break; // EBx fine vol down
      case 0xC: break; // ECx note cut (handled per-tick)
      case 0xD: break; // EDx note delay (handled in trigger)
      case 0xE: engine.RequestPatternDelay(val); break;// EEx pattern delay
    }
  }

  private void ApplyExtraFinePorta(int param) {
    var sub = param >> 4;
    var val = param & 0x0F;
    if (sub == 0x1) this._period -= val;   // X1x extra fine porta up
    else if (sub == 0x2) this._period += val; // X2x extra fine porta down
  }

  // ── effects: per-tick ─────────────────────────────────────────────────────────

  private void ApplyEffectTick(XmEngine engine, int effect, int param, XmModule mod, int tick) {
    switch (effect) {
      case 0x00: DoArpeggio(param, tick, mod); break;
      case 0x01: this._period = Math.Max(1, this._period - this._portaUpMem * 4); break;
      case 0x02: this._period += this._portaDownMem * 4; break;
      case 0x03: DoTonePorta(); break;
      case 0x04: DoVibrato(); break;
      case 0x05: DoTonePorta(); DoVolumeSlide(this._volSlideMem); break;
      case 0x06: DoVibrato(); DoVolumeSlide(this._volSlideMem); break;
      case 0x07: DoTremolo(); break;
      case 0x0A: DoVolumeSlide(this._volSlideMem); break;
      case 0x0E: ApplyExtendedTick(param, tick); break;
      case 0x11: DoGlobalVolSlide(engine); break;
      case 0x19: DoPanSlide(); break;
      case 0x1B: DoMultiRetrig(tick); break;
      case 0x1D: DoTremor(tick); break;
    }
  }

  private void ApplyExtendedTick(int param, int tick) {
    var sub = param >> 4;
    var val = param & 0x0F;
    switch (sub) {
      case 0x9: // E9x retrigger
        if (val > 0 && tick % val == 0) { this._samplePos = 0; this._pingPongForward = true; this._playing = this._sample?.Pcm.Length > 0; }
        break;
      case 0xC: // ECx note cut
        if (tick == val) this._volume = 0;
        break;
    }
  }

  // ── effect primitives ──────────────────────────────────────────────────────────

  private void DoArpeggio(int param, int tick, XmModule mod) {
    if (param == 0) return;
    var which = tick % 3;
    var add = which switch { 1 => param >> 4, 2 => param & 0x0F, _ => 0 };
    this._arpAdd = add;
  }
  private int _arpAdd;

  private void DoVolumeSlide(int mem) {
    var up = mem >> 4;
    var down = mem & 0x0F;
    if (up != 0) this._volume = Math.Clamp(this._volume + up, 0, 64);
    else if (down != 0) this._volume = Math.Clamp(this._volume - down, 0, 64);
  }

  private void DoGlobalVolSlide(XmEngine engine) {
    var up = this._globalVolSlideMem >> 4;
    var down = this._globalVolSlideMem & 0x0F;
    if (up != 0) engine.SlideGlobalVolume(up);
    else if (down != 0) engine.SlideGlobalVolume(-down);
  }

  private void DoPanSlide() {
    var left = this._panSlideMem >> 4;
    var right = this._panSlideMem & 0x0F;
    if (left != 0) this._panning = Math.Clamp(this._panning - left, 0, 255);
    else if (right != 0) this._panning = Math.Clamp(this._panning + right, 0, 255);
  }

  private void DoTonePorta() {
    if (this._tonePortaSpeed == 0 || this._targetPeriod == 0) return;
    if (this._period < this._targetPeriod) this._period = Math.Min(this._targetPeriod, this._period + this._tonePortaSpeed * 4);
    else if (this._period > this._targetPeriod) this._period = Math.Max(this._targetPeriod, this._period - this._tonePortaSpeed * 4);
  }

  private void DoVibrato() {
    var delta = WaveformValue(this._vibratoWaveform, this._vibratoPos) * this._vibratoDepth / 32;
    this._vibratoDelta = delta * 4;
    this._vibratoPos = (this._vibratoPos + this._vibratoSpeed) & 0x3F;
  }
  private int _vibratoDelta;

  private void DoTremolo() {
    var delta = WaveformValue(this._tremoloWaveform, this._tremoloPos) * this._tremoloDepth / 64;
    this._tremoloDelta = delta;
    this._tremoloPos = (this._tremoloPos + this._tremoloSpeed) & 0x3F;
  }
  private int _tremoloDelta;

  private void DoTremor(int tick) {
    var on = (this._tremorMem >> 4) + 1;
    var off = (this._tremorMem & 0x0F) + 1;
    var period = on + off;
    this._tremorMute = (tick % period) >= on;
  }
  private bool _tremorMute;

  private void DoMultiRetrig(int tick) {
    var interval = this._multiRetrigMem & 0x0F;
    var volOp = this._multiRetrigMem >> 4;
    if (interval == 0 || tick % interval != 0) return;
    this._samplePos = 0;
    this._pingPongForward = true;
    this._playing = this._sample?.Pcm.Length > 0;
    this._volume = volOp switch {
      0x1 => Math.Clamp(this._volume - 1, 0, 64),
      0x2 => Math.Clamp(this._volume - 2, 0, 64),
      0x3 => Math.Clamp(this._volume - 4, 0, 64),
      0x4 => Math.Clamp(this._volume - 8, 0, 64),
      0x5 => Math.Clamp(this._volume - 16, 0, 64),
      0x6 => Math.Clamp(this._volume * 2 / 3, 0, 64),
      0x7 => Math.Clamp(this._volume / 2, 0, 64),
      0x9 => Math.Clamp(this._volume + 1, 0, 64),
      0xA => Math.Clamp(this._volume + 2, 0, 64),
      0xB => Math.Clamp(this._volume + 4, 0, 64),
      0xC => Math.Clamp(this._volume + 8, 0, 64),
      0xD => Math.Clamp(this._volume + 16, 0, 64),
      0xE => Math.Clamp(this._volume * 3 / 2, 0, 64),
      0xF => Math.Clamp(this._volume * 2, 0, 64),
      _ => this._volume,
    };
  }

  private static int WaveformValue(int waveform, int pos) {
    pos &= 0x3F;
    return (waveform & 0x03) switch {
      0 => (int)Math.Round(Math.Sin(pos * Math.PI / 32.0) * 255) >> 3, // sine ≈ -31..31 scaled below
      1 => pos < 32 ? (pos * 2 - 32) : (96 - pos * 2),                 // ramp/sawtooth approximation
      _ => pos < 32 ? 31 : -31,                                        // square
    };
  }

  // ── per-tick state update (envelopes, fadeout, auto-vibrato) ─────────────────────

  public void UpdatePerTick(XmModule mod, int sampleRate, int tick) {
    if (this._instrument == null) return;

    UpdateVolumeEnvelope();
    UpdatePanEnvelope();
    UpdateAutoVibrato();
    UpdateFadeout();
    ComputeFinalGains(mod);
  }

  private int _envVolFactor = 64;   // 0..64
  private int _envPanOffset;        // -128..128

  private void UpdateVolumeEnvelope() {
    var env = this._instrument!.VolumeEnvelope;
    if (!env.Enabled || env.Points.Length == 0) { this._envVolFactor = 64; return; }
    this._envVolFactor = EvaluateEnvelope(env, ref this._volEnvPos, this._keyOff, max: 64);
  }

  private void UpdatePanEnvelope() {
    var env = this._instrument!.PanningEnvelope;
    if (!env.Enabled || env.Points.Length == 0) { this._envPanOffset = 0; return; }
    var v = EvaluateEnvelope(env, ref this._panEnvPos, this._keyOff, max: 64);
    this._envPanOffset = (v - 32) * 4; // centred
  }

  /// <summary>
  /// Pure linear interpolation of an XM envelope's y-value at a given tick position (no looping
  /// or sustain advance). Exposed for testing the envelope interpolation walk.
  /// </summary>
  internal static int InterpolateEnvelopeAt(XmEnvelope env, int pos) {
    var pts = env.Points;
    if (pts.Length == 0) return 0;
    if (pos <= pts[0].X) return pts[0].Y;
    for (var i = 0; i < pts.Length - 1; ++i) {
      var (x0, y0) = pts[i];
      var (x1, y1) = pts[i + 1];
      if (pos >= x0 && pos <= x1) {
        var span = Math.Max(1, x1 - x0);
        return y0 + (y1 - y0) * (pos - x0) / span;
      }
    }
    return pts[^1].Y;
  }

  private int EvaluateEnvelope(XmEnvelope env, ref int pos, bool keyOff, int max) {
    // Find the segment containing pos.
    var pts = env.Points;
    var value = pts[0].Y;
    for (var i = 0; i < pts.Length - 1; ++i) {
      var (x0, y0) = pts[i];
      var (x1, y1) = pts[i + 1];
      if (pos >= x0 && pos <= x1) {
        var span = Math.Max(1, x1 - x0);
        value = y0 + (y1 - y0) * (pos - x0) / span;
        break;
      }
      if (pos > pts[^1].X) value = pts[^1].Y;
    }
    if (pos >= pts[^1].X) value = pts[^1].Y;

    // Sustain: hold at sustain point unless key released.
    var sustainHold = env.Sustain && !keyOff && env.SustainPoint < pts.Length && pos >= pts[env.SustainPoint].X;
    if (sustainHold) {
      pos = pts[env.SustainPoint].X;
      return Math.Clamp(value, 0, max);
    }

    // Loop.
    if (env.Loop && env.LoopEnd < pts.Length && pos >= pts[env.LoopEnd].X) {
      pos = pts[Math.Clamp(env.LoopStart, 0, pts.Length - 1)].X;
    } else if (pos < pts[^1].X) {
      ++pos;
    }
    return Math.Clamp(value, 0, max);
  }

  private void UpdateAutoVibrato() {
    var ins = this._instrument!;
    if (ins.VibratoDepth == 0 || ins.VibratoRate == 0) { this._autoVibratoDelta = 0; return; }
    var depth = ins.VibratoDepth;
    if (ins.VibratoSweep > 0 && this._autoVibratoSweepPos < ins.VibratoSweep) {
      depth = depth * this._autoVibratoSweepPos / ins.VibratoSweep;
      ++this._autoVibratoSweepPos;
    }
    var sweep = WaveformValue(ins.VibratoType, this._autoVibratoPos);
    this._autoVibratoDelta = sweep * depth / 64;
    this._autoVibratoPos = (this._autoVibratoPos + ins.VibratoRate) & 0x3F;
  }
  private int _autoVibratoDelta;

  private void UpdateFadeout() {
    if (this._keyOff && this._instrument!.Fadeout > 0) {
      this._fadeoutVol -= this._instrument.Fadeout * 2;
      if (this._fadeoutVol < 0) { this._fadeoutVol = 0; this._playing = false; }
    }
  }

  private void ComputeFinalGains(XmModule mod) {
    var env = this._envVolFactor / 64.0f;
    var fade = this._fadeoutVol / 65536.0f;
    var vol = this._volume / 64.0f;
    var tremolo = this._tremoloDelta / 64.0f;
    var amp = Math.Clamp(vol + tremolo, 0f, 1f) * env * fade;
    if (this._tremorMute) amp = 0;

    var pan = Math.Clamp(this._panning + this._envPanOffset, 0, 255) / 255.0f;
    this._finalLeftGain = amp * (1f - pan);
    this._finalRightGain = amp * pan;
  }

  // ── mixing ──────────────────────────────────────────────────────────────────────

  public void Mix(float[] stereoBuffer, int frames, int sampleRate, float globalVol) {
    if (!this._playing || this._sample == null || this._sample.Pcm.Length == 0) return;

    var effectivePeriod = this._period - this._vibratoDelta - this._autoVibratoDelta;
    if (this._arpAdd != 0)
      effectivePeriod = NoteToPeriod(this._note + (this._sample.RelativeNote) + this._arpAdd, this._finetune, this._linearForMix);
    if (effectivePeriod < 1) effectivePeriod = 1;

    var freq = PeriodToFrequency(effectivePeriod, this._linearForMix);
    this._stepPerOutputSample = freq / sampleRate;

    var pcm = this._sample.Pcm;
    var loopType = this._sample.LoopType;
    var loopStart = this._sample.LoopStart;
    var loopLen = this._sample.LoopLength;
    var loopEnd = loopStart + loopLen;

    var lg = this._finalLeftGain * globalVol;
    var rg = this._finalRightGain * globalVol;

    for (var f = 0; f < frames; ++f) {
      var idx = (int)this._samplePos;
      if (idx < 0 || idx >= pcm.Length) { this._playing = loopType != 0; if (!this._playing) break; idx = Math.Clamp(idx, 0, pcm.Length - 1); }
      var s = pcm[idx] / 32768.0f * 32768.0f; // keep in 16-bit domain
      stereoBuffer[f * 2] += s * lg;
      stereoBuffer[f * 2 + 1] += s * rg;

      if (this._pingPongForward) this._samplePos += this._stepPerOutputSample;
      else this._samplePos -= this._stepPerOutputSample;

      if (loopType == 1 && loopLen > 0) {
        if (this._samplePos >= loopEnd) this._samplePos = loopStart + (this._samplePos - loopEnd);
      } else if (loopType == 2 && loopLen > 0) {
        if (this._pingPongForward && this._samplePos >= loopEnd) { this._pingPongForward = false; this._samplePos = loopEnd - (this._samplePos - loopEnd); }
        else if (!this._pingPongForward && this._samplePos <= loopStart) { this._pingPongForward = true; this._samplePos = loopStart + (loopStart - this._samplePos); }
      } else if (this._samplePos >= pcm.Length) {
        this._playing = false;
        break;
      }
    }
  }

  private bool _linearForMix;
  public void SetLinear(bool linear) => this._linearForMix = linear;

  // ── frequency math (XM.TXT) ──────────────────────────────────────────────────────

  /// <summary>
  /// Computes the XM period for a note (1..) and finetune. Linear (XM.TXT):
  /// <c>period = 7680 - (note-1)*64 - finetune/2</c>. Amiga uses the FT2 relation
  /// <c>period = 7680*8363 / freq_linear</c> so that <see cref="PeriodToFrequency"/> recovers the
  /// same pitch; the audible difference between the two tables is the porta/vibrato granularity,
  /// which both formulas preserve. (Exact-cycle Amiga period-table interpolation is not modelled.)
  /// </summary>
  public static int NoteToPeriod(int note, int finetune, bool linear) {
    if (note < 1) note = 1;
    if (note > 119) note = 119;
    if (linear)
      return 7680 - (note - 1) * 64 - finetune / 2;
    var freq = LinearFrequencyOf(note, finetune);
    return (int)Math.Round(7680.0 * 8363.0 / Math.Max(1.0, freq));
  }

  /// <summary>Frequency in Hz for a given period and table mode.</summary>
  public static double PeriodToFrequency(int period, bool linear) {
    if (linear)
      return 8363.0 * Math.Pow(2.0, (4608 - period) / 768.0);
    return period <= 0 ? 0 : 7680.0 * 8363.0 / period;
  }

  private static double LinearFrequencyOf(int note, int finetune) {
    var period = 7680 - (note - 1) * 64 - finetune / 2;
    return 8363.0 * Math.Pow(2.0, (4608 - period) / 768.0);
  }

  private int _defaultPan = 128;
  public void SetDefaultPan(int pan) { this._defaultPan = pan; this._panning = pan; }
}
