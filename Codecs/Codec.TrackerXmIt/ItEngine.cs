#pragma warning disable CS1591
namespace Codec.TrackerXmIt;

/// <summary>
/// The IT playback engine: order/row/tick traversal, per-channel effect state, New Note Action
/// voice allocation and the additive mixer. See <see cref="ItPlayer"/> for the model overview and
/// references.
/// </summary>
internal sealed class ItEngine {

  /// <summary>Cap on simultaneously-sounding background (virtual) voices spawned by NNAs.</summary>
  public const int MaxVirtualVoices = 64;

  private readonly ItModule _mod;
  private readonly int _sampleRate;
  private readonly int _channelCount = ItPattern.MaxChannels;

  private readonly ItChannelState[] _channels;
  private readonly ItVoice[] _voices;

  private int _speed;
  private int _tempo;
  private double _samplesPerTick;
  private int _globalVolume;

  private int _orderIndex;
  private int _row;
  private int _tick;
  private int _patternDelay;

  private bool _positionJump; private int _positionJumpOrder;
  private bool _patternBreak; private int _patternBreakRow;

  public ItEngine(ItModule mod, int sampleRate) {
    this._mod = mod;
    this._sampleRate = sampleRate;
    this._speed = mod.InitialSpeed;
    this._tempo = mod.InitialTempo;
    this._globalVolume = mod.GlobalVolume;
    UpdateTiming();

    this._channels = new ItChannelState[this._channelCount];
    for (var i = 0; i < this._channelCount; ++i) {
      this._channels[i] = new ItChannelState {
        Panning = PanFromHeader(mod.ChannelPan[i]),
        ChannelVolume = mod.ChannelVolume[i],
        Muted = (mod.ChannelPan[i] & 0x80) != 0,
      };
    }

    this._voices = new ItVoice[this._channelCount + MaxVirtualVoices];
    for (var i = 0; i < this._voices.Length; ++i)
      this._voices[i] = new ItVoice();
  }

  private static int PanFromHeader(byte p) {
    var pan = p & 0x7F;
    if (pan > 64) pan = 32;          // 100 = surround → centre
    return pan * 255 / 64;
  }

  private void UpdateTiming() => this._samplesPerTick = this._sampleRate * 2.5 / Math.Max(1, this._tempo);

  // ── public ──────────────────────────────────────────────────────────────────

  public byte[] Render(double maxSeconds) {
    var maxSamples = (long)(maxSeconds * this._sampleRate);
    var output = new List<short>(1 << 16);
    var visited = new HashSet<int>();
    var done = false;

    while (!done && output.Count / 2 < maxSamples) {
      if (this._tick == 0) {
        var key = (this._orderIndex << 16) | this._row;
        if (!visited.Add(key)) break;
      }

      ProcessTick();

      var n = (int)this._samplesPerTick;
      var buf = new float[n * 2];
      foreach (var v in this._voices)
        if (v.Active) v.Mix(buf, n, this._sampleRate);
      for (var i = 0; i < buf.Length; ++i)
        output.Add(ClampToShort(buf[i]));

      Advance(ref done);
    }

    var pcm = new byte[output.Count * 2];
    for (var i = 0; i < output.Count; ++i) {
      pcm[i * 2] = (byte)(output[i] & 0xFF);
      pcm[i * 2 + 1] = (byte)((output[i] >> 8) & 0xFF);
    }
    return pcm;
  }

  public double EstimateSeconds(double maxSeconds) {
    var visited = new HashSet<int>();
    double seconds = 0;
    var done = false;
    this._orderIndex = 0; this._row = 0; this._tick = 0;
    while (!done && seconds < maxSeconds) {
      if (this._tick == 0) {
        var key = (this._orderIndex << 16) | this._row;
        if (!visited.Add(key)) break;
        ScanRowTiming();
      }
      seconds += this._samplesPerTick / this._sampleRate;
      AdvanceTimingOnly(ref done);
    }
    return Math.Min(seconds, maxSeconds);
  }

  // expose for tests
  internal IReadOnlyList<ItVoice> Voices => this._voices;
  internal int ActiveVoiceCount() => this._voices.Count(v => v.Active);

  /// <summary>Steps the engine <paramref name="ticks"/> ticks (processing + advancing, no audio mixing) and reports the active voice count.</summary>
  internal int StepAndCountActiveVoices(int ticks) {
    var done = false;
    for (var t = 0; t < ticks && !done; ++t) {
      ProcessTick();
      Advance(ref done);
    }
    return ActiveVoiceCount();
  }

  // ── tick ──────────────────────────────────────────────────────────────────────

  private ItPattern CurrentPattern() {
    if (this._mod.Order.Length == 0) return ItPattern.Empty();
    var idx = Math.Clamp(this._orderIndex, 0, this._mod.Order.Length - 1);
    var pat = this._mod.Order[idx];
    if (pat == 254) return ItPattern.Empty(); // +++ marker
    if (pat >= this._mod.Patterns.Length) return ItPattern.Empty();
    return this._mod.Patterns[pat] ?? ItPattern.Empty();
  }

  private void ProcessTick() {
    var pattern = CurrentPattern();
    if (this._row >= pattern.Rows) this._row = 0;

    if (this._tick == 0) {
      this._positionJump = false;
      this._patternBreak = false;
      for (var c = 0; c < this._channelCount; ++c)
        ProcessRowCell(c, pattern.Cell(this._row, c));
    } else {
      for (var c = 0; c < this._channelCount; ++c)
        ProcessTickCell(c, pattern.Cell(this._row, c));
    }

    foreach (var v in this._voices) {
      if (!v.Active) continue;
      v.UpdateEnvelopesAndFade(this._sampleRate, 0);
      v.ComputeGains(this._globalVolume / 128.0f * (this._mod.MixVolume / 128.0f));
    }
  }

  // ── row processing ──────────────────────────────────────────────────────────

  private void ProcessRowCell(int c, in ItCell cell) {
    var ch = this._channels[c];

    if (cell.HasCommand) { ch.Command = cell.Command; ch.Param = cell.Param != 0 ? cell.Param : ch.Param; if (cell.Param != 0) ch.LastParam = cell.Param; }
    else { ch.Command = 0; }
    var param = cell.Param != 0 ? cell.Param : ch.LastParam;

    if (cell.HasInstrument && cell.Instrument > 0)
      ch.InstrumentNumber = cell.Instrument;

    var noteDelayTicks = cell.HasCommand && cell.Command == 19 /*S*/ && (param >> 4) == 0xD ? (param & 0x0F) : 0;

    if (cell.HasNote) {
      var note = cell.Note;
      if (note == 255) { NoteOff(c); }
      else if (note == 254) { NoteCut(c); }
      else if (note <= 119) {
        var isTonePorta = (cell.HasCommand && (cell.Command == 7 /*G*/)) ||
                          (cell.HasVolume && cell.Volume is >= 193 and <= 202); // volcol Gx
        if (noteDelayTicks > 0) { ch.PendingNote = note; ch.NoteDelay = noteDelayTicks; }
        else StartNote(c, note, isTonePorta);
      }
    }

    if (cell.HasVolume) ApplyVolumeColumnRow(c, cell.Volume);

    if (cell.HasCommand) ApplyCommandRow(c, cell.Command, param);
  }

  private void ProcessTickCell(int c, in ItCell cell) {
    var ch = this._channels[c];
    if (ch.NoteDelay > 0 && this._tick == ch.NoteDelay && ch.PendingNote > 0) {
      StartNote(c, ch.PendingNote, false);
      ch.PendingNote = 0;
      ch.NoteDelay = 0;
    }
    if (cell.HasVolume) ApplyVolumeColumnTick(c, cell.Volume);
    if (ch.Command != 0) ApplyCommandTick(c, ch.Command, ch.Param != 0 ? ch.Param : ch.LastParam);
  }

  // ── note start / NNA ────────────────────────────────────────────────────────

  private void StartNote(int c, int note, bool tonePorta) {
    var ch = this._channels[c];
    ResolveInstrumentSample(ch, note, out var instrument, out var sample, out var playNote, out var insIndex);
    if (sample == null) return;

    if (tonePorta && ch.Voice is { Active: true, Playing: true }) {
      // Retarget the existing voice's pitch only.
      ch.PortaTargetFreq = ComputeFrequency(sample, playNote);
      ch.Note = playNote;
      return;
    }

    // Apply NNA / duplicate checks to the currently-playing voice on this channel.
    HandleNewNoteAction(c);

    var voice = AllocateVoice(c);
    voice.Clear();
    voice.Active = true;
    voice.Background = false;
    voice.OwnerChannel = c;
    voice.Instrument = instrument;
    voice.Sample = sample;
    voice.Note = playNote;
    voice.InstrumentIndex = insIndex;
    voice.C5Speed = sample.C5Speed;
    voice.Frequency = ComputeFrequency(sample, playNote);
    voice.SamplePos = 0;
    voice.Forward = true;
    voice.Playing = sample.Pcm.Length > 0;
    voice.SampleVolume = sample.DefaultVolume;
    voice.Volume = sample.DefaultVolume;
    voice.ChannelVolume = ch.ChannelVolume;
    voice.Fadeout = 65536;
    voice.KeyOff = false;
    voice.NoteFade = false;

    // Panning: instrument default pan overrides, else sample, else channel.
    if (instrument is { UsePan: true }) voice.Panning = instrument.DefaultPan * 255 / 64;
    else if (sample.UsePan) voice.Panning = sample.DefaultPan * 255 / 64;
    else voice.Panning = ch.Panning;

    // Initial filter from instrument.
    if (instrument is { } ins2) {
      if (ins2.InitialFilterCutoff >= 0) { voice.FilterCutoff = ins2.InitialFilterCutoff; voice.FilterSet = true; }
      if (ins2.InitialFilterResonance >= 0) { voice.FilterResonance = ins2.InitialFilterResonance; voice.FilterSet = true; }
    }

    ch.Voice = voice;
    ch.Note = playNote;
    ch.PortaTargetFreq = voice.Frequency;
  }

  private void ResolveInstrumentSample(ItChannelState ch, int note, out ItInstrument? instrument,
      out ItSample? sample, out int playNote, out int insIndex) {
    instrument = null; sample = null; playNote = note; insIndex = 0;

    if (this._mod.InstrumentMode && ch.InstrumentNumber > 0 && ch.InstrumentNumber <= this._mod.Instruments.Length) {
      instrument = this._mod.Instruments[ch.InstrumentNumber - 1];
      insIndex = ch.InstrumentNumber;
      var n = Math.Clamp(note, 0, 119);
      var smp = instrument.NoteSampleMap[n];
      // The keyboard table can remap the played note; a 0 entry means "play the note as-is".
      var mapped = instrument.NoteMap[n];
      playNote = mapped != 0 ? mapped : note;
      if (smp >= 1 && smp <= this._mod.Samples.Length) sample = this._mod.Samples[smp - 1];
    } else if (ch.InstrumentNumber >= 1 && ch.InstrumentNumber <= this._mod.Samples.Length) {
      // Sample mode: instrument number is a direct sample index.
      sample = this._mod.Samples[ch.InstrumentNumber - 1];
      insIndex = ch.InstrumentNumber;
    }
  }

  private void HandleNewNoteAction(int c) {
    var ch = this._channels[c];
    var old = ch.Voice;
    if (old is not { Active: true }) return;

    var nna = old.Instrument?.NewNoteAction ?? 0;

    // Duplicate check: if the new instrument matches per DCT, force the DCA on the old voice.
    var ins = old.Instrument;
    if (ins is { DuplicateCheckType: > 0 }) {
      var dup = ins.DuplicateCheckType switch {
        3 => old.InstrumentIndex == ch.InstrumentNumber,           // instrument
        2 => old.InstrumentIndex == ch.InstrumentNumber,           // sample (approx by instrument)
        1 => old.Note == ch.Note,                                  // note
        _ => false,
      };
      if (dup) {
        switch (ins.DuplicateCheckAction) {
          case 0: old.Active = false; old.Playing = false; return;     // cut
          case 1: old.KeyOff = true; break;                            // off
          case 2: old.NoteFade = true; break;                          // fade
        }
      }
    }

    switch (nna) {
      case 0: // cut
        old.Active = false; old.Playing = false; break;
      case 1: // continue
        old.Background = true; old.OwnerChannel = -1; break;
      case 2: // note off
        old.Background = true; old.OwnerChannel = -1; old.KeyOff = true; break;
      case 3: // note fade
        old.Background = true; old.OwnerChannel = -1; old.NoteFade = true; break;
    }
    if (nna != 0) ch.Voice = null;
  }

  private ItVoice AllocateVoice(int c) {
    // Prefer the channel's reserved primary slot when free.
    var primary = this._voices[c];
    if (!primary.Active) return primary;

    // Find a free background slot.
    for (var i = this._channelCount; i < this._voices.Length; ++i)
      if (!this._voices[i].Active) return this._voices[i];

    // All in use: steal the quietest background voice (lowest fadeout).
    ItVoice? victim = null;
    for (var i = this._channelCount; i < this._voices.Length; ++i) {
      var v = this._voices[i];
      if (victim == null || v.Fadeout < victim.Fadeout) victim = v;
    }
    return victim ?? primary;
  }

  // ── note off / cut ──────────────────────────────────────────────────────────

  private void NoteOff(int c) { if (this._channels[c].Voice is { } v) v.KeyOff = true; }
  private void NoteCut(int c) { if (this._channels[c].Voice is { } v) { v.Playing = false; v.Active = false; } this._channels[c].Voice = null; }

  // ── frequency ─────────────────────────────────────────────────────────────────

  private static double ComputeFrequency(ItSample sample, int note) {
    // IT note 60 = C-5 = sample.C5Speed. Each semitone is 2^(1/12).
    var semis = note - 60;
    return sample.C5Speed * Math.Pow(2.0, semis / 12.0);
  }

  // ── volume column (ITTECH) ────────────────────────────────────────────────────

  private void ApplyVolumeColumnRow(int c, int v) {
    var ch = this._channels[c];
    if (v <= 64) { if (ch.Voice is { } vo) vo.Volume = v; }
    else if (v is >= 128 and <= 192) { if (ch.Voice is { } vo) vo.Panning = (v - 128) * 255 / 64; }
    else if (v is >= 65 and <= 74) ch.VolColMem = v - 65;       // A: fine vol up (applied tick0 below)
    else if (v is >= 75 and <= 84) { if (ch.Voice is { } vo) vo.Volume = Math.Clamp(vo.Volume + (v - 75), 0, 64); }
    else if (v is >= 85 and <= 94) { if (ch.Voice is { } vo) vo.Volume = Math.Clamp(vo.Volume - (v - 85), 0, 64); }
    // ranges 95..114 vol slide, 193..202 tone porta, 203..212 vibrato handled per-tick
    if (v is >= 95 and <= 104) ch.VolColSlide = v - 95;     // slide up per tick
    else if (v is >= 105 and <= 114) ch.VolColSlide = -(v - 105);
    else ch.VolColSlide = 0;
  }

  private void ApplyVolumeColumnTick(int c, int v) {
    var ch = this._channels[c];
    if (ch.VolColSlide != 0 && ch.Voice is { } vo)
      vo.Volume = Math.Clamp(vo.Volume + ch.VolColSlide, 0, 64);
  }

  // ── commands A..Z (row part) ──────────────────────────────────────────────────

  private void ApplyCommandRow(int c, int cmd, int param) {
    var ch = this._channels[c];
    switch (cmd) {
      case 1: this._speed = param > 0 ? param : this._speed; break;                  // Axx speed
      case 2: this._positionJump = true; this._positionJumpOrder = param; break;     // Bxx jump
      case 3: this._patternBreak = true; this._patternBreakRow = (param >> 4) * 10 + (param & 0x0F); break; // Cxx break
      case 6: ch.PortaMem = param != 0 ? param : ch.PortaMem; break;                 // Hxy vibrato sets below
      case 7: if (param != 0) ch.PortaSpeed = param; break;                          // Gxx tone porta
      case 9: if (ch.Voice is { } vo && param != 0) { ch.OffsetMem = param; vo.SamplePos = param * 256; } else if (ch.Voice is { } vo2) vo2.SamplePos = ch.OffsetMem * 256; break; // Oxx offset
      case 12: ch.ChannelVolume = Math.Clamp(param, 0, 64); if (ch.Voice is { } cv) cv.ChannelVolume = ch.ChannelVolume; break; // Mxx channel vol
      case 16: this._globalVolume = Math.Clamp(param, 0, 128); break;                // Vxx global vol
      case 20: this._tempo = param >= 0x20 ? param : this._tempo; if (param >= 0x20) UpdateTiming(); ch.TempoSlide = param < 0x20 ? param : 0; break; // Txx tempo
      case 24: if (ch.Voice is { } pv) pv.Panning = param; break;                    // Xxx set pan
      case 26: ApplyFilterCommand(c, param); break;                                  // Zxx filter
      case 19: ApplyExtendedRow(c, param); break;                                    // Sxy
      case 4: ch.VolSlideMem = param != 0 ? param : ch.VolSlideMem; ApplyVolSlideFineRow(c, ch.VolSlideMem); break; // Dxy vol slide (fine on row)
      case 5: ch.PortaMem = param != 0 ? param : ch.PortaMem; ApplyPortaFineRow(c, ch.PortaMem, down: true); break; // Exy porta down
      case 11: ch.PortaMem = param != 0 ? param : ch.PortaMem; ApplyPortaFineRow(c, ch.PortaMem, down: false); break; // Fxy porta up
    }
  }

  private void ApplyExtendedRow(int c, int param) {
    var ch = this._channels[c];
    var sub = param >> 4;
    var val = param & 0x0F;
    switch (sub) {
      case 0x7: // S7x NNA / envelope overrides (subset)
        if (ch.Voice is { } v7) {
          switch (val) {
            case 0x3: if (v7.Instrument != null) v7.Instrument.NewNoteAction = 0; break; // NNA cut (note: shared instrument object; pragmatic)
            case 0x4: if (v7.Instrument != null) v7.Instrument.NewNoteAction = 1; break;
            case 0x5: if (v7.Instrument != null) v7.Instrument.NewNoteAction = 2; break;
            case 0x6: if (v7.Instrument != null) v7.Instrument.NewNoteAction = 3; break;
          }
        }
        break;
      case 0x8: if (ch.Voice is { } v8) v8.Panning = val * 17; break;   // S8x pan
      case 0x9: /* S9x sound control — ignored subset (surround/reverb) */ break;
      case 0xB: // SBx pattern loop
        if (val == 0) ch.PatternLoopRow = this._row;
        else {
          if (ch.PatternLoopCount == 0) ch.PatternLoopCount = val + 1;
          if (--ch.PatternLoopCount > 0) { this._patternBreak = false; this._positionJump = false; this._row = ch.PatternLoopRow - 1; }
        }
        break;
      case 0xC: ch.NoteCutTick = val; break;        // SCx note cut
      case 0xD: /* SDx note delay handled in row trigger */ break;
      case 0xE: this._patternDelay = val; break;    // SEx pattern delay
    }
  }

  private void ApplyFilterCommand(int c, int param) {
    var ch = this._channels[c];
    if (ch.Voice is not { } v) return;
    if (param < 0x80) { v.FilterCutoff = param * 127 / 127; v.FilterSet = true; } // Z00..Z7F cutoff
    else { v.FilterResonance = (param - 0x80) * 127 / 127; v.FilterSet = true; }  // Z80..ZFF resonance
  }

  private void ApplyVolSlideFineRow(int c, int mem) {
    var ch = this._channels[c];
    if (ch.Voice is not { } v) return;
    var up = mem >> 4; var down = mem & 0x0F;
    if (up == 0x0F && down != 0) v.Volume = Math.Clamp(v.Volume - down, 0, 64);       // Dx F → fine down
    else if (down == 0x0F && up != 0) v.Volume = Math.Clamp(v.Volume + up, 0, 64);    // D F y → fine up
  }

  private void ApplyPortaFineRow(int c, int mem, bool down) {
    var ch = this._channels[c];
    if (ch.Voice is not { } v) return;
    if ((mem & 0xF0) == 0xF0) { var amt = (mem & 0x0F); SlidePitch(v, down ? -amt : amt, fine: true); }
    else if ((mem & 0xF0) == 0xE0) { var amt = (mem & 0x0F); SlidePitch(v, down ? -amt : amt, extraFine: true); }
  }

  // ── commands A..Z (per-tick part) ─────────────────────────────────────────────

  private void ApplyCommandTick(int c, int cmd, int param) {
    var ch = this._channels[c];
    switch (cmd) {
      case 4: DoVolumeSlide(c, ch.VolSlideMem); break;                       // Dxy
      case 5: DoPitchSlide(c, ch.PortaMem, down: true); break;               // Exy
      case 11: DoPitchSlide(c, ch.PortaMem, down: false); break;             // Fxy
      case 7: DoTonePorta(c); break;                                          // Gxx
      case 6: DoVibrato(c, param); break;                                     // Hxy
      case 8: DoTremor(c, param); break;                                      // Ixy
      case 10: DoArpeggio(c, param); break;                                   // Jxy
      case 9: break;                                                          // Oxx (row only)
      case 14: DoVibrato(c, param, fine: true); break;                        // Uxy fine vibrato
      case 19: ApplyExtendedTick(c, param); break;                            // Sxy
      case 20: if (ch.TempoSlide != 0) DoTempoSlide(ch.TempoSlide); break;    // Txx slide
      case 18: DoChannelVolSlide(c, param); break;                            // Nxy chan vol slide
      case 16: break;                                                         // Vxx
      case 25: DoPanSlide(c, param); break;                                   // Pxy pan slide
      case 17: DoRetrig(c, param); break;                                     // Qxy retrig
      case 21: DoTremolo(c, param); break;                                    // Rxy tremolo
    }
  }

  private void ApplyExtendedTick(int c, int param) {
    var ch = this._channels[c];
    var sub = param >> 4;
    var val = param & 0x0F;
    if (sub == 0xC && this._tick == ch.NoteCutTick) NoteCut(c);  // SCx
  }

  // ── effect primitives ──────────────────────────────────────────────────────────

  private void DoVolumeSlide(int c, int mem) {
    if (this._channels[c].Voice is not { } v) return;
    var up = mem >> 4; var down = mem & 0x0F;
    if (up == 0x0F || down == 0x0F) return; // fine handled on row
    if (up != 0) v.Volume = Math.Clamp(v.Volume + up, 0, 64);
    else if (down != 0) v.Volume = Math.Clamp(v.Volume - down, 0, 64);
  }

  private void DoChannelVolSlide(int c, int mem) {
    var ch = this._channels[c];
    if (ch.Voice is not { } v) return;
    var up = mem >> 4; var down = mem & 0x0F;
    if (up != 0) ch.ChannelVolume = Math.Clamp(ch.ChannelVolume + up, 0, 64);
    else if (down != 0) ch.ChannelVolume = Math.Clamp(ch.ChannelVolume - down, 0, 64);
    v.ChannelVolume = ch.ChannelVolume;
  }

  private void DoPanSlide(int c, int mem) {
    if (this._channels[c].Voice is not { } v) return;
    var left = mem >> 4; var right = mem & 0x0F;
    if (left != 0) v.Panning = Math.Clamp(v.Panning - left * 4, 0, 255);
    else if (right != 0) v.Panning = Math.Clamp(v.Panning + right * 4, 0, 255);
  }

  private void DoPitchSlide(int c, int mem, bool down) {
    if ((mem & 0xF0) is 0xE0 or 0xF0) return; // fine handled on row
    if (this._channels[c].Voice is not { } v) return;
    SlidePitch(v, down ? -mem : mem, fine: false);
  }

  private static void SlidePitch(ItVoice v, int amount, bool fine = false, bool extraFine = false) {
    // amount in "linear period" units; convert to a frequency ratio. IT slides 1 unit = 1/16 semitone
    // in linear mode (approximation), scaled by fine/extra-fine.
    var scale = extraFine ? 1.0 / 64.0 : fine ? 1.0 / 16.0 : 1.0 / 4.0;
    var semis = amount * scale / 16.0;
    v.Frequency *= Math.Pow(2.0, semis / 12.0);
  }

  private void DoTonePorta(int c) {
    var ch = this._channels[c];
    if (ch.Voice is not { } v || ch.PortaSpeed == 0) return;
    var target = ch.PortaTargetFreq;
    var step = Math.Pow(2.0, ch.PortaSpeed / 4.0 / 16.0 / 12.0);
    if (v.Frequency < target) v.Frequency = Math.Min(target, v.Frequency * step);
    else if (v.Frequency > target) v.Frequency = Math.Max(target, v.Frequency / step);
  }

  private void DoVibrato(int c, int param, bool fine = false) {
    var ch = this._channels[c];
    if (param != 0) { if ((param >> 4) != 0) ch.VibratoSpeed = param >> 4; if ((param & 0x0F) != 0) ch.VibratoDepth = param & 0x0F; }
    if (ch.Voice is not { } v) return;
    var delta = Math.Sin(ch.VibratoPos * Math.PI / 32.0) * ch.VibratoDepth * (fine ? 0.25 : 1.0);
    var semis = delta / 64.0;
    v.Frequency = ch.PortaTargetFreq * Math.Pow(2.0, semis / 12.0);
    ch.VibratoPos = (ch.VibratoPos + ch.VibratoSpeed) & 0x3F;
  }

  private void DoTremolo(int c, int param) {
    var ch = this._channels[c];
    if (param != 0) { if ((param >> 4) != 0) ch.TremoloSpeed = param >> 4; if ((param & 0x0F) != 0) ch.TremoloDepth = param & 0x0F; }
    if (ch.Voice is not { } v) return;
    var delta = (int)(Math.Sin(ch.TremoloPos * Math.PI / 32.0) * ch.TremoloDepth);
    v.Volume = Math.Clamp(v.Volume + delta, 0, 64);
    ch.TremoloPos = (ch.TremoloPos + ch.TremoloSpeed) & 0x3F;
  }

  private void DoTremor(int c, int param) {
    var ch = this._channels[c];
    if (param != 0) ch.TremorMem = param;
    var on = (ch.TremorMem >> 4) + 1;
    var off = (ch.TremorMem & 0x0F) + 1;
    if (ch.Voice is { } v) v.Volume = (this._tick % (on + off)) < on ? v.Volume : 0;
  }

  private void DoArpeggio(int c, int param) {
    var ch = this._channels[c];
    if (param == 0 || ch.Voice is not { } v || v.Sample == null) return;
    var which = this._tick % 3;
    var add = which switch { 1 => param >> 4, 2 => param & 0x0F, _ => 0 };
    v.Frequency = ComputeFrequency(v.Sample, ch.Note + add);
  }

  private void DoRetrig(int c, int param) {
    var ch = this._channels[c];
    var interval = param & 0x0F;
    if (interval == 0 || this._tick % interval != 0) return;
    if (ch.Voice is { } v) { v.SamplePos = 0; v.Forward = true; v.Playing = v.Sample?.Pcm.Length > 0; }
  }

  private void DoTempoSlide(int slide) {
    if (slide < 0x10) this._tempo = Math.Max(32, this._tempo - slide);
    else this._tempo = Math.Min(255, this._tempo + (slide - 0x10));
    UpdateTiming();
  }

  // ── advancing ──────────────────────────────────────────────────────────────────

  private void Advance(ref bool done) {
    ++this._tick;
    var totalTicks = this._speed + this._patternDelay * this._speed;
    if (this._tick < totalTicks) return;
    this._tick = 0;
    this._patternDelay = 0;
    AdvanceRow(ref done);
  }

  private void AdvanceTimingOnly(ref bool done) {
    ++this._tick;
    var totalTicks = this._speed + this._patternDelay * this._speed;
    if (this._tick < totalTicks) return;
    this._tick = 0;
    this._patternDelay = 0;
    AdvanceRow(ref done);
  }

  private void ScanRowTiming() {
    var pattern = CurrentPattern();
    if (this._row >= pattern.Rows) return;
    this._positionJump = false; this._patternBreak = false; this._patternDelay = 0;
    for (var c = 0; c < this._channelCount; ++c) {
      var cell = pattern.Cell(this._row, c);
      if (!cell.HasCommand) continue;
      var param = cell.Param;
      switch (cell.Command) {
        case 1: if (param > 0) this._speed = param; break;
        case 20: if (param >= 0x20) { this._tempo = param; UpdateTiming(); } break;
        case 2: this._positionJump = true; this._positionJumpOrder = param; break;
        case 3: this._patternBreak = true; this._patternBreakRow = (param >> 4) * 10 + (param & 0x0F); break;
        case 19 when (param >> 4) == 0xE: this._patternDelay = param & 0x0F; break;
      }
    }
  }

  private void AdvanceRow(ref bool done) {
    if (this._positionJump) {
      this._orderIndex = this._positionJumpOrder;
      this._row = this._patternBreak ? this._patternBreakRow : 0;
      this._positionJump = false; this._patternBreak = false;
      if (this._orderIndex >= this._mod.Order.Length) done = true;
      return;
    }
    if (this._patternBreak) {
      this._row = this._patternBreakRow;
      this._patternBreak = false;
      AdvanceOrder(ref done);
      return;
    }
    ++this._row;
    var pattern = CurrentPattern();
    if (this._row >= pattern.Rows) { this._row = 0; AdvanceOrder(ref done); }
  }

  private void AdvanceOrder(ref bool done) {
    ++this._orderIndex;
    // Skip separator (254) and end (255) markers.
    while (this._orderIndex < this._mod.Order.Length && this._mod.Order[this._orderIndex] == 254)
      ++this._orderIndex;
    if (this._orderIndex >= this._mod.Order.Length || (this._orderIndex < this._mod.Order.Length && this._mod.Order[this._orderIndex] == 255))
      done = true;
  }

  private static short ClampToShort(float v) {
    var i = (int)MathF.Round(v);
    if (i > short.MaxValue) i = short.MaxValue;
    if (i < short.MinValue) i = short.MinValue;
    return (short)i;
  }
}

/// <summary>Persistent per-track-channel effect/note state (distinct from the sounding voices).</summary>
internal sealed class ItChannelState {
  public ItVoice? Voice;
  public int InstrumentNumber;
  public int Note = 60;
  public int Panning = 128;
  public int ChannelVolume = 64;
  public bool Muted;

  public int Command;
  public int Param;
  public int LastParam;

  public int PendingNote;
  public int NoteDelay;
  public int NoteCutTick = -1;

  public int PortaSpeed;
  public int PortaMem;
  public double PortaTargetFreq;
  public int VolSlideMem;
  public int OffsetMem;
  public int TempoSlide;

  public int VibratoSpeed, VibratoDepth, VibratoPos;
  public int TremoloSpeed, TremoloDepth, TremoloPos;
  public int TremorMem;

  public int VolColMem;
  public int VolColSlide;

  public int PatternLoopRow;
  public int PatternLoopCount;
}
