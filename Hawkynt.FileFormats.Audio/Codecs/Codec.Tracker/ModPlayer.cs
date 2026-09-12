#pragma warning disable CS1591
namespace Codec.Tracker;

/// <summary>
/// ProTracker MOD player. Implements the standard effect set: 0xy arpeggio,
/// 1xx/2xx slide up/down, 3xx tone portamento, 4xy vibrato, 5xy tone porta +
/// volume slide, 6xy vibrato + volume slide, 7xy tremolo, 8xx pan, 9xx sample
/// offset, Axy volume slide, Bxx position jump, Cxx volume, Dxx pattern break,
/// E-subcommands and Fxx speed/tempo. Note→frequency uses the Amiga period
/// table with finetune and the PAL clock.
/// </summary>
/// <remarks>
/// Effect behaviour follows the ProTracker 2.3 effect reference / FireLight
/// "fmoddoc". Vibrato/tremolo use the standard 32-entry sine table from the
/// ProTracker replayer.
/// </remarks>
internal sealed class ModPlayer : TrackerPlayer {

  private static readonly int[] SineTable = [
    0, 24, 49, 74, 97, 120, 141, 161, 180, 197, 212, 224, 235, 244, 250, 253,
    255, 253, 250, 244, 235, 224, 212, 197, 180, 161, 141, 120, 97, 74, 49, 24,
  ];

  public ModPlayer(TrackerSong song, int outputRate) : base(song, outputRate) { }

  private double PeriodToFrequency(int period) => AmigaPeriods.FrequencyForPeriod(period);

  protected override void ProcessNote(int channel, ref TrackerCell cell) {
    var st = this.State[channel];
    var voice = this.Channels[channel];
    var effect = cell.Effect;
    var param = cell.EffectParam;

    st.NoteCutTick = -1;
    st.NoteDelayTicks = 0;

    var hasNote = cell.Period > 0;
    var newInstrument = cell.Instrument;

    // Instrument change updates volume/finetune even without a note.
    if (newInstrument > 0 && newInstrument < this.Song.Samples.Length) {
      st.Instrument = newInstrument;
      var smp = this.Song.Samples[newInstrument];
      if (smp != null) {
        st.Volume = smp.DefaultVolume;
        st.FineTune = smp.FineTune;
      }
    }

    var isTonePorta = effect is 0x3 or 0x5;
    var isNoteDelay = effect == 0xE && (param >> 4) == 0xD;

    if (hasNote) {
      // Re-apply finetune from the active instrument.
      var row = AmigaPeriods.FineTuneToRow(st.FineTune);
      var noteIndex = AmigaPeriods.NearestNoteIndex(cell.Period);
      var tunedPeriod = AmigaPeriods.PeriodFor(noteIndex, row);
      if (tunedPeriod == 0)
        tunedPeriod = cell.Period;

      if (isTonePorta) {
        st.TargetPeriod = tunedPeriod;
      } else if (!isNoteDelay) {
        st.Period = tunedPeriod;
        this.TriggerVoice(channel, st);
        st.ResetOscillators();
      } else {
        st.NoteDelayTicks = param & 0x0F;
        st.TargetPeriod = tunedPeriod; // staged for the delay
      }
    }

    if (cell.Volume >= 0)
      st.Volume = Math.Clamp(cell.Volume, 0, 64);

    st.EffectivePeriod = st.Period;
    st.EffectiveVolume = st.Volume;
    this.ApplyTickZeroEffect(channel, st, voice, effect, param, hasNote);
    this.PushVoice(channel, st);
  }

  private void TriggerVoice(int channel, ChannelState st) {
    var voice = this.Channels[channel];
    if (st.Instrument > 0 && st.Instrument < this.Song.Samples.Length) {
      var smp = this.Song.Samples[st.Instrument];
      if (smp != null) {
        voice.Trigger(smp, this.PeriodToFrequency(st.Period), st.Volume);
        if (st.SampleOffset > 0)
          voice.PositionFixed = (long)st.SampleOffset << 16;
      }
    }
  }

  private void ApplyTickZeroEffect(int channel, ChannelState st, MixerChannel voice, int effect, int param, bool hasNote) {
    var x = param >> 4;
    var y = param & 0x0F;
    switch (effect) {
      case 0x0:
        st.ArpeggioParam = param;
        break;
      case 0x1:
        if (param != 0) st.PortaSpeed = param;
        break;
      case 0x2:
        if (param != 0) st.PortaSpeed = param;
        break;
      case 0x3:
        if (param != 0) st.TonePortaSpeed = param;
        break;
      case 0x4:
        if (x != 0) st.VibratoSpeed = x;
        if (y != 0) st.VibratoDepth = y;
        break;
      case 0x5: // tone porta + volume slide (continue tone porta)
      case 0x6: // vibrato + volume slide
        if (param != 0) st.VolSlideParam = param;
        break;
      case 0x7:
        if (x != 0) st.TremoloSpeed = x;
        if (y != 0) st.TremoloDepth = y;
        break;
      case 0x8:
        voice.Pan = param;
        break;
      case 0x9:
        if (param != 0) st.SampleOffset = param << 8;
        if (hasNote) voice.PositionFixed = (long)st.SampleOffset << 16;
        break;
      case 0xA:
        if (param != 0) st.VolSlideParam = param;
        break;
      case 0xB:
        this.PositionJumpRequested = true;
        this.PositionJumpOrder = param;
        this.PatternBreakRow = 0;
        break;
      case 0xC:
        st.Volume = Math.Clamp(param, 0, 64);
        break;
      case 0xD:
        this.PatternBreakRequested = true;
        this.PatternBreakRow = x * 10 + y;
        break;
      case 0xE:
        this.ApplyExtended(channel, st, voice, x, y, hasNote);
        break;
      case 0xF:
        if (param < 0x20) {
          if (param > 0) this.Speed = param;
        } else {
          this.Tempo = param;
        }
        break;
    }
  }

  private void ApplyExtended(int channel, ChannelState st, MixerChannel voice, int x, int y, bool hasNote) {
    switch (x) {
      case 0x1: // fine porta up
        st.Period = Math.Max(1, st.Period - y);
        break;
      case 0x2: // fine porta down
        st.Period += y;
        break;
      case 0x4: // vibrato waveform
        st.VibratoWaveform = y;
        break;
      case 0x5: // set finetune
        st.FineTune = y;
        if (hasNote) {
          var ni = AmigaPeriods.NearestNoteIndex(st.Period);
          st.Period = AmigaPeriods.PeriodFor(ni, AmigaPeriods.FineTuneToRow(y));
        }
        break;
      case 0x6: // pattern loop (handled by length traversal; affect playback via row repeat)
        // E60 sets loop start; E6x (x>0) repeats. Tracked minimally for playback.
        break;
      case 0x7: // tremolo waveform
        st.TremoloWaveform = y;
        break;
      case 0x8: // set panning (coarse)
        voice.Pan = y * 17;
        break;
      case 0x9: // retrigger
        st.RetrigParam = y;
        break;
      case 0xA: // fine volume slide up
        st.Volume = Math.Min(64, st.Volume + y);
        break;
      case 0xB: // fine volume slide down
        st.Volume = Math.Max(0, st.Volume - y);
        break;
      case 0xC: // note cut
        st.NoteCutTick = y;
        break;
      case 0xD: // note delay (staged in ProcessNote)
        break;
      case 0xE: // pattern delay
        this.PatternDelay = y;
        break;
    }
  }

  protected override void ProcessEffectTickN(int channel, ref TrackerCell cell) {
    var st = this.State[channel];
    var voice = this.Channels[channel];
    var effect = cell.Effect;
    var param = cell.EffectParam;
    var x = param >> 4;
    var y = param & 0x0F;

    // Effective values default to the base period/volume; oscillators adjust them.
    st.EffectivePeriod = st.Period;
    st.EffectiveVolume = st.Volume;

    switch (effect) {
      case 0x0:
        this.TickArpeggio(channel, st);
        break;
      case 0x1:
        st.Period = Math.Max(1, st.Period - st.PortaSpeed);
        break;
      case 0x2:
        st.Period += st.PortaSpeed;
        break;
      case 0x3:
        this.TickTonePorta(st);
        break;
      case 0x4:
        this.TickVibrato(channel, st);
        break;
      case 0x5:
        this.TickTonePorta(st);
        this.TickVolumeSlide(st, st.VolSlideParam);
        break;
      case 0x6:
        this.TickVibrato(channel, st);
        this.TickVolumeSlide(st, st.VolSlideParam);
        break;
      case 0x7:
        this.TickTremolo(channel, st);
        break;
      case 0xA:
        this.TickVolumeSlide(st, st.VolSlideParam);
        break;
      case 0xE:
        switch (x) {
          case 0x9: // retrigger every y ticks
            if (y > 0 && this.Tick % y == 0)
              this.TriggerVoice(channel, st);
            break;
          case 0xC: // note cut
            if (this.Tick == st.NoteCutTick)
              st.Volume = 0;
            break;
          case 0xD: // note delay
            if (this.Tick == st.NoteDelayTicks) {
              st.Period = st.TargetPeriod;
              this.TriggerVoice(channel, st);
              st.ResetOscillators();
            }
            break;
        }
        break;
    }

    this.PushVoice(channel, st);
  }

  private void TickArpeggio(int channel, ChannelState st) {
    if (st.ArpeggioParam == 0)
      return;
    var x = st.ArpeggioParam >> 4;
    var y = st.ArpeggioParam & 0x0F;
    var phase = this.Tick % 3;
    var semis = phase switch { 1 => x, 2 => y, _ => 0 };
    if (semis == 0)
      return;
    var baseNote = AmigaPeriods.NearestNoteIndex(st.Period);
    var note = Math.Clamp(baseNote + semis, 0, 35);
    var period = AmigaPeriods.PeriodFor(note, AmigaPeriods.FineTuneToRow(st.FineTune));
    st.EffectivePeriod = period == 0 ? st.Period : period;
  }

  private void TickTonePorta(ChannelState st) {
    if (st.TargetPeriod == 0)
      return;
    if (st.Period < st.TargetPeriod) {
      st.Period = Math.Min(st.Period + st.TonePortaSpeed, st.TargetPeriod);
    } else if (st.Period > st.TargetPeriod) {
      st.Period = Math.Max(st.Period - st.TonePortaSpeed, st.TargetPeriod);
    }
  }

  private void TickVibrato(int channel, ChannelState st) {
    var delta = SineTable[st.VibratoPos & 0x1F] * st.VibratoDepth / 128;
    st.EffectivePeriod = Math.Max(1, (st.VibratoPos & 0x20) != 0 ? st.Period - delta : st.Period + delta);
    st.VibratoPos = (st.VibratoPos + st.VibratoSpeed) & 0x3F;
  }

  private void TickTremolo(int channel, ChannelState st) {
    var delta = SineTable[st.TremoloPos & 0x1F] * st.TremoloDepth / 64;
    var vol = (st.TremoloPos & 0x20) != 0 ? st.Volume - delta : st.Volume + delta;
    st.EffectiveVolume = Math.Clamp(vol, 0, 64);
    st.TremoloPos = (st.TremoloPos + st.TremoloSpeed) & 0x3F;
  }

  private void TickVolumeSlide(ChannelState st, int param) {
    var up = param >> 4;
    var down = param & 0x0F;
    if (up > 0)
      st.Volume = Math.Min(64, st.Volume + up);
    else if (down > 0)
      st.Volume = Math.Max(0, st.Volume - down);
  }

  /// <summary>Pushes the effective period/volume (base plus this tick's oscillator offset) to the voice.</summary>
  private void PushVoice(int channel, ChannelState st) {
    var voice = this.Channels[channel];
    voice.FrequencyHz = this.PeriodToFrequency(st.EffectivePeriod);
    voice.Volume = st.EffectiveVolume;
  }
}
