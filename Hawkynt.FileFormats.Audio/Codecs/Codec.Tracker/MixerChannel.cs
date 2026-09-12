#pragma warning disable CS1591
namespace Codec.Tracker;

/// <summary>
/// One mixing voice. Tracks the currently playing sample, its position as a
/// 16.16 fixed-point sample index, the playback frequency (in Hz), the linear
/// volume (0..64 in tracker units) and the stereo panning (0 = full left,
/// 255 = full right). The mixer advances <see cref="PositionFixed"/> by a
/// per-sample step derived from <see cref="FrequencyHz"/> and resamples the
/// source linearly.
/// </summary>
/// <remarks>
/// The 16.16 fixed-point convention and the linear-interpolation resampler
/// follow the mixing model used by micromod / libmodplug; volume scaling and
/// panning are applied in the accumulate step rather than baked into the voice.
/// </remarks>
internal sealed class MixerChannel {

  /// <summary>The sample currently assigned to this voice, or null when silent.</summary>
  public TrackerSample? Sample;

  /// <summary>Playback cursor into <see cref="Sample"/>, as a 16.16 fixed-point sample index.</summary>
  public long PositionFixed;

  /// <summary>Playback frequency in Hz. Drives the resampling step.</summary>
  public double FrequencyHz;

  /// <summary>Linear volume in tracker units (0..64).</summary>
  public int Volume;

  /// <summary>Stereo pan, 0 = hard left, 128 = centre, 255 = hard right.</summary>
  public int Pan = 128;

  /// <summary>True once playback has run past a non-looping sample's end.</summary>
  public bool Ended;

  public const int VolumeMax = 64;
  private const int FixedShift = 16;
  private const long FixedOne = 1L << FixedShift;

  public void Trigger(TrackerSample sample, double frequencyHz, int volume) {
    this.Sample = sample;
    this.PositionFixed = 0;
    this.FrequencyHz = frequencyHz;
    this.Volume = volume;
    this.Ended = false;
  }

  /// <summary>
  /// Renders <paramref name="count"/> stereo frames into the accumulator buffers,
  /// advancing the voice. Volume is scaled by <paramref name="masterScale"/>
  /// (global * instrument volume, 0..1) before accumulation.
  /// </summary>
  public void Mix(int[] left, int[] right, int count, int outputRate, double masterScale) {
    if (this.Sample is not { } smp || this.Ended || smp.Data.Length == 0 || this.Volume <= 0)
      return;

    var step = (long)Math.Round(this.FrequencyHz / outputRate * FixedOne);
    if (step <= 0)
      return;

    // Pre-compute pan gains (0..1) once per block.
    var panRight = this.Pan / 255.0;
    var panLeft = 1.0 - panRight;
    var volScale = this.Volume / (double)VolumeMax * masterScale;
    var gainLeft = volScale * panLeft;
    var gainRight = volScale * panRight;

    for (var i = 0; i < count; ++i) {
      var index = (int)(this.PositionFixed >> FixedShift);
      if (index >= smp.Data.Length) {
        if (smp.IsLooping) {
          this.PositionFixed = WrapLoop(this.PositionFixed, smp);
          index = (int)(this.PositionFixed >> FixedShift);
          if (index >= smp.Data.Length)
            break;
        } else {
          this.Ended = true;
          break;
        }
      }

      // Linear interpolation between index and index+1.
      var frac = (this.PositionFixed & (FixedOne - 1)) / (double)FixedOne;
      var s0 = smp.Data[index];
      short s1;
      var next = index + 1;
      if (next >= smp.Data.Length)
        s1 = smp.IsLooping && smp.LoopLength > 1 ? smp.Data[smp.LoopStart] : s0;
      else
        s1 = smp.Data[next];
      var sample = s0 + (s1 - s0) * frac;

      left[i] += (int)(sample * gainLeft);
      right[i] += (int)(sample * gainRight);

      this.PositionFixed += step;
      if (smp.IsLooping && (this.PositionFixed >> FixedShift) >= smp.LoopEnd)
        this.PositionFixed = WrapLoop(this.PositionFixed, smp);
    }
  }

  private static long WrapLoop(long pos, TrackerSample smp) {
    var loopLenFixed = (long)smp.LoopLength << FixedShift;
    if (loopLenFixed <= 0)
      return pos;
    var loopStartFixed = (long)smp.LoopStart << FixedShift;
    var loopEndFixed = (long)smp.LoopEnd << FixedShift;
    while (pos >= loopEndFixed)
      pos -= loopLenFixed;
    if (pos < loopStartFixed)
      pos = loopStartFixed;
    return pos;
  }
}
