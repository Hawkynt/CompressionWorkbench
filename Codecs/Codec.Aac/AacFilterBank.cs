#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// AAC filter bank: inverse MDCT (2048-point for long windows, 8×256-point for
/// short windows) plus sine/KBD (Kaiser-Bessel Derived) window application and
/// overlap-add with the previous frame's tail (ISO/IEC 14496-3 §4.6.11).
/// </summary>
internal static class AacFilterBank {

  public const int LongFrameSize = 1024;
  public const int ShortFrameSize = 128;
  public const int LongMdctSize = 2048;
  public const int ShortMdctSize = 256;

  /// <summary>
  /// Performs IMDCT + windowing + overlap-add for one channel.
  /// Returns <paramref name="channelOverlap"/>-sized carry-over buffer.
  /// </summary>
  public static void Synthesize(
    float[] spectralInput,
    int windowSequence,
    int windowShape,
    int prevWindowShape,
    float[] channelOverlap,
    float[] pcmOut) {
    _ = spectralInput; _ = windowSequence; _ = windowShape;
    _ = prevWindowShape; _ = channelOverlap; _ = pcmOut;
    throw new NotSupportedException(
      "AAC filter bank not yet implemented. A full IMDCT (naive O(N^2) or " +
      "split-radix), sine + KBD window tables (α=4 for long, α=6 for short), " +
      "and overlap-add buffer management are required per ISO/IEC 14496-3 §4.6.11.");
  }
}
