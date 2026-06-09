#pragma warning disable CS1591
namespace Codec.AmrNb;

/// <summary>
/// Unpacked AMR-NB frame parameters. Mirrors ffmpeg <c>AMRNBFrame</c> / <c>AMRNBSubframe</c>
/// (amrnbdata.h): five LSF indices followed by four subframes of pitch lag, pitch gain, fixed gain
/// and up to ten pulse fields. The flat <see cref="Words"/> array preserves the original uint16
/// field layout so the <c>order_*</c> bit-reorder tables (which index <c>offsetof&gt;&gt;1</c>) map
/// straight onto it.
/// </summary>
internal sealed class AmrNbFrame {
  // Layout (uint16 slots): lsf[0..4]=0..4, then per subframe f (base 5+f*13):
  //   +0 p_lag, +1 p_gain, +2 fixed_gain, +3..+12 pulses[0..9].
  public const int WordCount = 5 + 4 * 13;
  public readonly int[] Words = new int[WordCount];

  public int Lsf(int i) => this.Words[i];
  public int PLag(int sub) => this.Words[5 + sub * 13 + 0];
  public int PGain(int sub) => this.Words[5 + sub * 13 + 1];
  public int FixedGain(int sub) => this.Words[5 + sub * 13 + 2];
  public int Pulse(int sub, int p) => this.Words[5 + sub * 13 + 3 + p];

  public void Clear() => Array.Clear(this.Words);
}
