#pragma warning disable CS1591
namespace Codec.Tracker;

/// <summary>
/// Scream Tracker 3 (S3M) player. Note-to-frequency is C2SPD based: a note plays
/// at <c>C2SPD × 2^((note − C5)/12)</c> Hz, equivalently the ST3 period
/// <c>period = 8363 × 16 × periodTable[note] / C2SPD</c> with
/// <c>frequency = 14317056 / period</c>. Implements the ST3 effect set: A set
/// speed, B jump, C break, D volume slide (+ fine), E/F porta (+ fine / extra-fine),
/// G tone porta, H vibrato, I tremor, J arpeggio, K/L slide combos, O offset,
/// Q retrigger + volume slide, R tremolo, S sub-commands (incl. S8 pan, SB loop,
/// SC cut, SD delay), T tempo, U fine vibrato, V global volume.
/// </summary>
/// <remarks>
/// Effects follow the official Scream Tracker 3 TECH.DOC, with ambiguous cases
/// (default-pan 0x3/0xC scheme, tremor on/off counting, arpeggio cycling) checked
/// against OpenMPT.
/// </remarks>
internal sealed class S3mPlayer : TrackerPlayer {

  // Periods for one octave (C..B), used in the ST3 frequency formula. These are
  // the ST3 note periods for octave 0; higher octaves halve the period.
  private static readonly int[] NotePeriods = [1712, 1616, 1524, 1440, 1356, 1280, 1208, 1140, 1076, 1016, 960, 907];

  private static readonly int[] SineTable = [
    0, 24, 49, 74, 97, 120, 141, 161, 180, 197, 212, 224, 235, 244, 250, 253,
    255, 253, 250, 244, 235, 224, 212, 197, 180, 161, 141, 120, 97, 74, 49, 24,
  ];

  public S3mPlayer(TrackerSong song, int outputRate) : base(song, outputRate) { }

  /// <summary>
  /// ST3 period for a semitone note (1-based, where 1 → C-0) at the given C2SPD,
  /// per TECH.DOC: <c>period = 8363 × 16 × notePeriod / C2SPD</c>.
  /// </summary>
  public static double PeriodForNote(int note, double c2spd) {
    if (note <= 0 || c2spd <= 0)
      return 0;
    var n = note - 1;
    var octave = n / 12;
    var step = n % 12;
    // Octave division kept in floating point so octave ratios stay exact (cleaner than
    // ST3's integer >>octave, which loses precision in high octaves).
    var basePeriod = NotePeriods[step] / Math.Pow(2.0, octave);
    return 8363.0 * 16.0 * basePeriod / c2spd;
  }

  /// <summary>Replay frequency in Hz for an ST3 period.</summary>
  public static double FrequencyForPeriod(double period)
    => period <= 0 ? 0 : 14317056.0 / period;

  /// <summary>Replay frequency in Hz for a semitone note at the given C2SPD.</summary>
  public static double FrequencyForNote(int note, double c2spd)
    => FrequencyForPeriod(PeriodForNote(note, c2spd));

  private double PeriodFrequency(ChannelState st) => FrequencyForPeriod(st.PeriodHz);

  protected override void ProcessNote(int channel, ref TrackerCell cell) {
    var st = this.State[channel];
    var voice = this.Channels[channel];
    var effect = cell.Effect;
    var param = cell.EffectParam;

    st.NoteCutTick = -1;
    st.NoteDelayTicks = 0;

    if (cell.Instrument > 0 && cell.Instrument < this.Song.Samples.Length) {
      st.Instrument = cell.Instrument;
      var smp = this.Song.Samples[cell.Instrument];
      if (smp != null) {
        st.Volume = smp.DefaultVolume;
        st.C2Spd = smp.BaseRate;
      }
    }

    if (cell.Note == 254) {
      // Note off: silence the voice.
      voice.Volume = 0;
      st.Volume = 0;
    }

    var hasNote = cell.Note is > 0 and < 254;
    var isTonePorta = effect == 0x7 /* G */;
    var isNoteDelay = effect == 0x13 /* S */ && (param >> 4) == 0xD;

    if (hasNote) {
      if (isTonePorta) {
        st.TargetNote = cell.Note;
        st.TargetPeriodHz = PeriodForNote(cell.Note, st.C2Spd);
      } else if (!isNoteDelay) {
        st.Note = cell.Note;
        st.PeriodHz = PeriodForNote(cell.Note, st.C2Spd);
        this.TriggerVoice(channel, st);
        st.ResetOscillators();
      } else {
        st.NoteDelayTicks = param & 0x0F;
        st.TargetNote = cell.Note;
      }
    }

    if (cell.Volume >= 0)
      st.Volume = Math.Clamp(cell.Volume, 0, 64);

    st.EffectiveVolume = st.Volume;
    this.ApplyTickZeroEffect(channel, st, voice, effect, param, hasNote);
    this.PushVoice(channel, st);
  }

  private void TriggerVoice(int channel, ChannelState st) {
    var voice = this.Channels[channel];
    if (st.Instrument > 0 && st.Instrument < this.Song.Samples.Length) {
      var smp = this.Song.Samples[st.Instrument];
      if (smp != null) {
        st.C2Spd = smp.BaseRate;
        if (st.PeriodHz <= 0 && st.Note > 0)
          st.PeriodHz = PeriodForNote(st.Note, st.C2Spd);
        voice.Trigger(smp, this.PeriodFrequency(st), st.Volume);
        if (st.SampleOffset > 0)
          voice.PositionFixed = (long)st.SampleOffset << 16;
      }
    }
  }

  private void ApplyTickZeroEffect(int channel, ChannelState st, MixerChannel voice, int effect, int param, bool hasNote) {
    var x = param >> 4;
    var y = param & 0x0F;
    switch (effect) {
      case 0x1: // A — set speed
        if (param > 0) this.Speed = param;
        break;
      case 0x2: // B — position jump
        this.PositionJumpRequested = true;
        this.PositionJumpOrder = param;
        this.PatternBreakRow = 0;
        break;
      case 0x3: // C — pattern break
        this.PatternBreakRequested = true;
        this.PatternBreakRow = x * 10 + y;
        break;
      case 0x4: // D — volume slide (+ fine on tick 0)
        if (param != 0) st.VolSlideParam = param;
        this.ApplyFineVolumeSlide(st, st.VolSlideParam);
        break;
      case 0x5: // E — porta down (+ fine/extra-fine)
        if (param != 0) st.PortaSpeed = param;
        this.ApplyFinePorta(st, st.PortaSpeed, down: true);
        break;
      case 0x6: // F — porta up
        if (param != 0) st.PortaSpeed = param;
        this.ApplyFinePorta(st, st.PortaSpeed, down: false);
        break;
      case 0x7: // G — tone porta
        if (param != 0) st.TonePortaSpeed = param;
        break;
      case 0x8: // H — vibrato
        if (x != 0) st.VibratoSpeed = x;
        if (y != 0) st.VibratoDepth = y;
        break;
      case 0x9: // I — tremor
        if (param != 0) st.TremorParam = param;
        break;
      case 0xA: // J — arpeggio
        if (param != 0) st.ArpeggioParam = param;
        break;
      case 0xB: // K — vibrato + volume slide
      case 0xC: // L — tone porta + volume slide
        if (param != 0) st.VolSlideParam = param;
        this.ApplyFineVolumeSlide(st, st.VolSlideParam);
        break;
      case 0xF: // O — sample offset
        if (param != 0) st.SampleOffset = param << 8;
        if (hasNote) voice.PositionFixed = (long)st.SampleOffset << 16;
        break;
      case 0x11: // Q — retrigger + volume slide
        if (param != 0) st.RetrigParam = param;
        break;
      case 0x12: // R — tremolo
        if (x != 0) st.TremoloSpeed = x;
        if (y != 0) st.TremoloDepth = y;
        break;
      case 0x13: // S — sub-commands
        this.ApplySubCommand(channel, st, voice, x, y);
        break;
      case 0x14: // T — tempo
        if (param >= 0x20) this.Tempo = param;
        break;
      case 0x15: // U — fine vibrato
        if (x != 0) st.VibratoSpeed = x;
        if (y != 0) st.VibratoDepth = y;
        break;
      case 0x16: // V — global volume
        this.GlobalVolume = Math.Clamp(param, 0, 64);
        break;
    }
  }

  private void ApplySubCommand(int channel, ChannelState st, MixerChannel voice, int x, int y) {
    switch (x) {
      case 0x3: st.VibratoWaveform = y; break;
      case 0x4: st.TremoloWaveform = y; break;
      case 0x8: voice.Pan = y * 17; break; // S8x set pan
      case 0xB: break;                      // SBx pattern loop (length traversal handles flow)
      case 0xC: st.NoteCutTick = y; break;  // SCx note cut
      case 0xD: st.NoteDelayTicks = y; break; // SDx note delay
    }
  }

  private void ApplyFinePorta(ChannelState st, int param, bool down) {
    var x = param >> 4;
    var y = param & 0x0F;
    // Fine (Fx) applies once on tick 0 by 4×y; extra-fine (Ex) by y; period up = pitch up.
    if (x == 0xF)
      st.PeriodHz += (down ? +1 : -1) * 4.0 * y;
    else if (x == 0xE)
      st.PeriodHz += (down ? +1 : -1) * 1.0 * y;
    st.PeriodHz = Math.Max(1, st.PeriodHz);
  }

  private void ApplyFineVolumeSlide(ChannelState st, int param) {
    var up = param >> 4;
    var down = param & 0x0F;
    // Fxy fine variants: Dx F → fine up by x; D F y → fine down by y.
    if (down == 0xF && up != 0)
      st.Volume = Math.Min(64, st.Volume + up);
    else if (up == 0xF && down != 0)
      st.Volume = Math.Max(0, st.Volume - down);
  }

  protected override void ProcessEffectTickN(int channel, ref TrackerCell cell) {
    var st = this.State[channel];
    var voice = this.Channels[channel];
    var effect = cell.Effect;
    var param = cell.EffectParam;

    st.EffectiveVolume = st.Volume;
    var freqOverridden = false;

    switch (effect) {
      case 0x4: // D — volume slide
        this.TickVolumeSlide(st, st.VolSlideParam);
        break;
      case 0x5: // E — porta down (period up → pitch down)
        if ((st.PortaSpeed >> 4) < 0xE) st.PeriodHz = Math.Max(1, st.PeriodHz + 4.0 * st.PortaSpeed);
        break;
      case 0x6: // F — porta up (period down → pitch up)
        if ((st.PortaSpeed >> 4) < 0xE) st.PeriodHz = Math.Max(1, st.PeriodHz - 4.0 * st.PortaSpeed);
        break;
      case 0x7: // G — tone porta
        this.TickTonePorta(st);
        break;
      case 0x8: // H — vibrato
        this.TickVibrato(channel, st);
        freqOverridden = true;
        break;
      case 0x9: // I — tremor
        this.TickTremor(st);
        break;
      case 0xA: // J — arpeggio
        this.TickArpeggio(channel, st);
        freqOverridden = true;
        break;
      case 0xB: // K — vibrato + volslide
        this.TickVibrato(channel, st);
        this.TickVolumeSlide(st, st.VolSlideParam);
        freqOverridden = true;
        break;
      case 0xC: // L — tone porta + volslide
        this.TickTonePorta(st);
        this.TickVolumeSlide(st, st.VolSlideParam);
        break;
      case 0x11: // Q — retrigger + volume slide
        this.TickRetrig(channel, st);
        break;
      case 0x12: // R — tremolo
        this.TickTremolo(channel, st);
        break;
      case 0x13: // S — sub-commands per tick
        var sx = param >> 4;
        var sy = param & 0x0F;
        if (sx == 0xC && this.Tick == sy) st.Volume = 0;
        if (sx == 0xD && this.Tick == st.NoteDelayTicks) {
          st.Note = st.TargetNote;
          st.PeriodHz = PeriodForNote(st.Note, st.C2Spd);
          this.TriggerVoice(channel, st);
          st.ResetOscillators();
        }
        break;
      case 0x15: // U — fine vibrato
        this.TickVibrato(channel, st, fine: true);
        freqOverridden = true;
        break;
    }

    if (!freqOverridden)
      voice.FrequencyHz = this.PeriodFrequency(st);
    voice.Volume = st.EffectiveVolume;
  }

  private void TickTonePorta(ChannelState st) {
    if (st.TargetPeriodHz <= 0)
      return;
    var speed = 4.0 * st.TonePortaSpeed;
    if (st.PeriodHz > st.TargetPeriodHz)
      st.PeriodHz = Math.Max(st.PeriodHz - speed, st.TargetPeriodHz);
    else if (st.PeriodHz < st.TargetPeriodHz)
      st.PeriodHz = Math.Min(st.PeriodHz + speed, st.TargetPeriodHz);
  }

  private void TickVibrato(int channel, ChannelState st, bool fine = false) {
    var delta = SineTable[st.VibratoPos & 0x1F] * st.VibratoDepth * (fine ? 1 : 4) / 64.0;
    var period = (st.VibratoPos & 0x20) != 0 ? st.PeriodHz + delta : st.PeriodHz - delta;
    this.Channels[channel].FrequencyHz = FrequencyForPeriod(Math.Max(1, period));
    st.VibratoPos = (st.VibratoPos + st.VibratoSpeed) & 0x3F;
  }

  private void TickTremolo(int channel, ChannelState st) {
    var delta = SineTable[st.TremoloPos & 0x1F] * st.TremoloDepth / 64;
    st.EffectiveVolume = Math.Clamp((st.TremoloPos & 0x20) != 0 ? st.Volume - delta : st.Volume + delta, 0, 64);
    st.TremoloPos = (st.TremoloPos + st.TremoloSpeed) & 0x3F;
  }

  private void TickArpeggio(int channel, ChannelState st) {
    if (st.ArpeggioParam == 0)
      return;
    var x = st.ArpeggioParam >> 4;
    var y = st.ArpeggioParam & 0x0F;
    var semis = (this.Tick % 3) switch { 1 => x, 2 => y, _ => 0 };
    // Arpeggio offsets the current pitch by whole semitones.
    this.Channels[channel].FrequencyHz = this.PeriodFrequency(st) * Math.Pow(2.0, semis / 12.0);
  }

  private void TickTremor(ChannelState st) {
    var onTime = (st.TremorParam >> 4) + 1;
    var offTime = (st.TremorParam & 0x0F) + 1;
    var cycle = st.TremorCount % (onTime + offTime);
    st.EffectiveVolume = cycle < onTime ? st.Volume : 0;
    ++st.TremorCount;
  }

  private void TickRetrig(int channel, ChannelState st) {
    var interval = st.RetrigParam & 0x0F;
    if (interval > 0 && this.Tick % interval == 0)
      this.TriggerVoice(channel, st);
  }

  private void TickVolumeSlide(ChannelState st, int param) {
    var up = param >> 4;
    var down = param & 0x0F;
    if (up == 0xF || down == 0xF)
      return; // fine variants applied on tick 0 only
    if (up > 0)
      st.Volume = Math.Min(64, st.Volume + up);
    else if (down > 0)
      st.Volume = Math.Max(0, st.Volume - down);
    st.EffectiveVolume = st.Volume;
  }

  private void PushVoice(int channel, ChannelState st) {
    var voice = this.Channels[channel];
    voice.FrequencyHz = this.PeriodFrequency(st);
    voice.Volume = st.EffectiveVolume;
  }
}
