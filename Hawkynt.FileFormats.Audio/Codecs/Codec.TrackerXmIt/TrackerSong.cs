#pragma warning disable CS1591
namespace Codec.TrackerXmIt;

/// <summary>
/// Shared rendering conventions for the XM and IT software players: output format, virtual
/// channel cap, and the deterministic song-length cap used by order traversal.
/// </summary>
public static class TrackerRender {

  /// <summary>Output sample rate of the rendered SONG.wav (Hz).</summary>
  public const int OutputSampleRate = 44100;

  /// <summary>Output channel count (stereo).</summary>
  public const int OutputChannels = 2;

  /// <summary>Output bits per sample.</summary>
  public const int OutputBits = 16;

  /// <summary>
  /// Hard cap on rendered duration (seconds). Order traversal also stops on a detected loop;
  /// this is the absolute deterministic ceiling so a pathological module still terminates.
  /// </summary>
  public const double MaxSeconds = 600.0; // 10 minutes
}
