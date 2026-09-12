#pragma warning disable CS1591

namespace Codec.Tta;

/// <summary>
/// TTA's two-state adaptive Rice coder, a faithful port of the residual loop in
/// ffmpeg's <c>libavcodec/tta.c</c>. Each residual is coded as a unary prefix
/// (escaping to the k1 state) plus <c>k</c> remainder bits; the running sums
/// <c>sum0</c>/<c>sum1</c> drive the parameters <c>k0</c>/<c>k1</c> up or down so
/// the code length tracks the signal. Encoder and decoder share this state
/// machine, so they adapt identically.
/// <para>Initialised with k0 = k1 = 10 (sum0 = sum1 = 2¹⁴), exactly as the
/// reference decoder's <c>ff_tta_rice_init</c>.</para>
/// </summary>
internal sealed class TtaRice {

  // ff_tta_shift_1: 1<<i for i in 0..30, clamped to 0x80000000 through index 39,
  // then 0xFFFFFFFF at index 40.
  private static readonly uint[] Shift1 = BuildShift1();

  private static uint[] BuildShift1() {
    var t = new uint[41];
    for (var i = 0; i < 31; ++i) t[i] = 1u << i;
    for (var i = 31; i < 40; ++i) t[i] = 0x80000000u;
    t[40] = 0xFFFFFFFFu;
    return t;
  }

  // ff_tta_shift_16 = ff_tta_shift_1 + 4.
  private static uint Shift16(int i) => Shift1[i + 4];

  /// <summary>The fixed-point power-of-two table (<c>ff_tta_shift_1</c>) the coder adds back in the k1 path.</summary>
  public static uint Pow2(int i) => Shift1[i];

  private uint _sum0;
  private uint _sum1;
  private int _k0;
  private int _k1;

  public TtaRice() {
    this._k0 = 10;
    this._k1 = 10;
    this._sum0 = Shift16(10);
    this._sum1 = Shift16(10);
  }

  public int K0 => this._k0;

  /// <summary>Reads and unmaps one residual from <paramref name="reader"/>.</summary>
  public int Decode(TtaBitReader reader) {
    var unary = (uint)reader.GetUnary();
    int depth;
    int k;
    if (unary == 0) {
      depth = 0;
      k = this._k0;
    } else {
      depth = 1;
      k = this._k1;
      --unary;
    }

    var value = k != 0 ? (unary << k) + reader.GetBits(k) : unary;
    value = this.Adapt(depth, value);

    // Unmap zigzag: 0→0, 1→1, 2→-1, 3→2, …
    return unchecked(1 + (int)((value >> 1) ^ (uint)(((int)(value & 1)) - 1)));
  }

  /// <summary>Maps and writes one residual to <paramref name="writer"/>.</summary>
  public void Encode(TtaBitWriter writer, int residual) {
    // Zigzag map (inverse of the decoder's unmap): s>0 → 2s−1, s≤0 → −2s.
    var value = residual > 0 ? (uint)((residual << 1) - 1) : (uint)(-residual << 1);

    var depth = value >= this.Threshold() ? 1 : 0;
    int k;
    uint coded;
    if (depth == 1) {
      k = this._k1;
      coded = value - Pow2(this._k0);
      writer.PutUnary((int)((coded >> k) + 1));
    } else {
      k = this._k0;
      coded = value;
      writer.PutUnary((int)(coded >> k));
    }
    if (k != 0)
      writer.PutBits(coded & ((1u << k) - 1), k);

    this.Adapt(depth, coded);
  }

  // The encoder must pick the same depth the decoder would infer from the
  // unary prefix: the k1 path is taken once the value reaches the k0 escape
  // boundary ff_tta_shift_1[k0].
  private uint Threshold() => Pow2(this._k0);

  // Runs the exact sum/k adaptation from tta.c and returns the value with the
  // k0 escape constant folded back in for the depth-1 path (matching the
  // decoder, whose `value += ff_tta_shift_1[k0]` happens before sum0 adapts).
  private uint Adapt(int depth, uint value) {
    if (depth == 1) {
      this._sum1 = unchecked(this._sum1 + value - (this._sum1 >> 4));
      if (this._k1 > 0 && this._sum1 < Shift16(this._k1)) --this._k1;
      else if (this._sum1 > Shift16(this._k1 + 1)) ++this._k1;
      value += Pow2(this._k0);
    }
    this._sum0 = unchecked(this._sum0 + value - (this._sum0 >> 4));
    if (this._k0 > 0 && this._sum0 < Shift16(this._k0)) --this._k0;
    else if (this._sum0 > Shift16(this._k0 + 1)) ++this._k0;
    return value;
  }
}
