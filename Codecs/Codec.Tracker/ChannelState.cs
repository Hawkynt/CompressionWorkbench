#pragma warning disable CS1591
namespace Codec.Tracker;

/// <summary>
/// Per-channel playback state retained across rows: the current period (MOD) or
/// note (S3M), the active instrument, volume, the effect memories (last
/// parameters for porta / vibrato / offset / volslide, etc.) and the vibrato /
/// tremolo oscillator phases. The effect handlers read and update this; the
/// mixer reads only the resulting frequency/volume on the associated voice.
/// </summary>
internal sealed class ChannelState {

  public int Period;          // MOD: current Amiga period
  public int TargetPeriod;    // tone-portamento destination
  public int Note;            // S3M: current semitone note (1..120)
  public int TargetNote;      // S3M tone-portamento destination note
  public int Instrument;      // active 1-based instrument
  public int Volume = 64;     // 0..64
  public int FineTune;        // MOD finetune row source for retriggered notes

  public double C2Spd = 8363; // S3M sample replay rate at C-2
  public double PeriodHz;     // S3M: continuous ST3 period for accurate porta/vibrato
  public double TargetPeriodHz; // S3M tone-porta destination period

  // Effect memories.
  public int PortaSpeed;
  public int TonePortaSpeed;
  public int VibratoSpeed, VibratoDepth;
  public int TremoloSpeed, TremoloDepth;
  public int VolSlideParam;
  public int SampleOffset;
  public int ArpeggioParam;
  public int RetrigParam;
  public int TremorParam, TremorCount;
  public int LastEffectParam; // generic memory for S3M "continue" semantics

  // Oscillator phases.
  public int VibratoPos;
  public int TremoloPos;
  public int VibratoWaveform;
  public int TremoloWaveform;

  // Note-delay / cut bookkeeping within a row.
  public int NoteDelayTicks;
  public int NoteCutTick = -1;

  // Per-tick effective overrides applied on top of the base period/volume by
  // oscillator and arpeggio effects; reset to the base each tick before effects run.
  public int EffectivePeriod;
  public int EffectiveVolume;

  public void ResetOscillators() {
    if ((this.VibratoWaveform & 0x04) == 0) this.VibratoPos = 0;
    if ((this.TremoloWaveform & 0x04) == 0) this.TremoloPos = 0;
  }
}
