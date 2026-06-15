#pragma warning disable CS1591
namespace Codec.Tracker;

/// <summary>
/// A decoded instrument sample: 16-bit signed PCM (converted up from the
/// module's native 8-bit signed data) plus its loop window and the playback
/// rate that corresponds to its reference note. <see cref="LoopStart"/> /
/// <see cref="LoopLength"/> are expressed in sample frames.
/// </summary>
internal sealed class TrackerSample {

  public required short[] Data;
  public int LoopStart;
  public int LoopLength;
  public int DefaultVolume = 64;

  /// <summary>Reference replay rate for the sample's centre note, in Hz.</summary>
  public int BaseRate;

  /// <summary>Finetune in the ProTracker -8..+7 range (MOD only); 0 for S3M.</summary>
  public int FineTune;

  public bool IsLooping => this.LoopLength > 1;
  public int LoopEnd => this.LoopStart + this.LoopLength;
}
