#pragma warning disable CS1591
namespace Codec.AmrWb;

/// <summary>
/// Unpacked AMR-WB frame parameters, mirroring ffmpeg <c>AMRWBFrame</c>/<c>AMRWBSubFrame</c>
/// (amrwbdata.h). Flat uint16 layout: vad(0), isp_id[0..6](1..7), then per subframe f
/// (base 8+f*12): adap, ltp, vq_gain, hb_gain, pul_ih[0..3], pul_il[0..3]. The <c>order_*</c>
/// tables index directly into <see cref="Words"/>.
/// </summary>
internal sealed class AmrWbFrame {
  public const int WordCount = 1 + 7 + 4 * 12;
  public readonly int[] Words = new int[WordCount];

  public int Vad => this.Words[0];
  public int IspId(int i) => this.Words[1 + i];
  private int Base(int sub) => 8 + sub * 12;
  public int Adap(int sub) => this.Words[Base(sub) + 0];
  public int Ltp(int sub) => this.Words[Base(sub) + 1];
  public int VqGain(int sub) => this.Words[Base(sub) + 2];
  public int HbGain(int sub) => this.Words[Base(sub) + 3];
  public int PulIh(int sub, int t) => this.Words[Base(sub) + 4 + t];
  public int PulIl(int sub, int t) => this.Words[Base(sub) + 8 + t];

  public void Clear() => System.Array.Clear(this.Words);
}
