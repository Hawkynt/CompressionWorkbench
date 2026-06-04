#pragma warning disable CS1591

namespace Codec.MonkeysAudio;

/// <summary>
/// Monkey's Audio "current" (3.98+) residual entropy stage. Each residual is
/// folded to an unsigned magnitude (zig-zag, sign in bit 0) and split into an
/// <em>overflow</em> high part (<c>magnitude &gt;&gt; k</c>) and a <c>k</c>-bit
/// remainder. The overflow part is range-coded through the 64-entry cumulative
/// table (<see cref="ApeRangeConstants.Counts"/>); its final entry is an escape
/// after which the remaining overflow is sent as a raw 32-bit field. The
/// remainder is sent as <c>k</c> raw bits. <c>k</c> tracks a running average of
/// the recent magnitudes — the <c>ksum</c>/<c>k</c> adaptation of the reference
/// SDK / ffmpeg <c>apedec.c</c> (<c>update_rice</c>).
/// <para>
/// EXACT-spec: the overflow-class cumulative table and the k-from-ksum adaptation
/// follow <c>apedec.c</c>. SELF-CONSISTENT: the encoder (<see cref="Encode"/>) is
/// the exact algebraic inverse of the decoder (<see cref="Decode"/>) — it folds,
/// splits the same way and submits the matching range-coder cells, so the pair is
/// bit-locked and lossless. DEVIATION: the reference's escape uses a nested
/// secondary range model for very large residuals; this port codes the residual
/// overflow as a single raw 32-bit field, which is self-consistent and lossless
/// but not byte-identical to a reference encoder for pathological inputs.
/// </para>
/// </summary>
internal sealed class ApeEntropy {

  // k is clamped so 1<<(k+ ...) shifts and 1u<<k masks never reach the 32-bit
  // shift-count wrap; 24-bit folded magnitudes fit comfortably below this.
  private const uint MaxK = 30;

  // Per-channel adaptive Rice state (apedec.c APERice: k + ksum).
  private sealed class RiceState {
    public uint K = 10;
    public uint KSum = 16u << 10;
  }

  private readonly RiceState[] _states;

  public ApeEntropy(int channels) {
    this._states = new RiceState[channels];
    for (var c = 0; c < channels; ++c)
      this._states[c] = new RiceState();
  }

  // zig-zag fold/unfold so a signed residual becomes an unsigned magnitude code.
  private static uint Fold(int v) => (uint)((v << 1) ^ (v >> 31));
  private static int Unfold(uint u) => (int)(u >> 1) ^ -(int)(u & 1);

  // apedec.c update_rice: adapt k toward log2 of the running magnitude average.
  private static void UpdateRice(RiceState s, uint magnitude) {
    s.KSum += ((magnitude + 1) / 2) - ((s.KSum + 16) >> 5);
    if (s.K == 0) {
      if (s.KSum >= 64) s.K = 1;
    } else if (s.KSum < 1u << (int)(s.K + 4) && s.K > 0) {
      --s.K;
    } else if (s.K < MaxK && s.KSum >= 1u << (int)(s.K + 5)) {
      ++s.K;
    }
  }

  private static readonly uint LastClass = (uint)(ApeRangeConstants.Counts.Length - 1);

  /// <summary>Decodes one signed residual for channel <paramref name="ch"/>.</summary>
  public int Decode(ApeRangeDecoder rc, int ch) {
    var s = this._states[ch];

    // Overflow high part via the cumulative class table; the last class escapes
    // to a raw 32-bit overflow count.
    var cf = rc.DecodeFrequency(ApeRangeConstants.Total);
    var cls = (uint)ApeRangeConstants.ClassForCumulative(cf);
    rc.DecodeUpdate(ApeRangeConstants.Counts[cls], ApeRangeConstants.Widths[cls]);

    var overflow = cls == LastClass ? rc.DecodeBits(32) : cls;

    var k = s.K;
    var remainder = k > 0 ? rc.DecodeBits((int)k) : 0u;
    var magnitude = (overflow << (int)k) | remainder;

    UpdateRice(s, magnitude);
    return Unfold(magnitude);
  }

  /// <summary>Encodes one signed residual for channel <paramref name="ch"/>, the
  /// exact inverse of <see cref="Decode"/>.</summary>
  public void Encode(ApeRangeEncoder rc, int ch, int value) {
    var s = this._states[ch];
    var magnitude = Fold(value);
    var k = s.K;

    var overflow = magnitude >> (int)k;
    var remainder = k > 0 ? magnitude & ((1u << (int)k) - 1) : 0u;

    if (overflow >= LastClass) {
      rc.EncodeCell(ApeRangeConstants.Counts[LastClass], ApeRangeConstants.Widths[LastClass], ApeRangeConstants.Total);
      rc.EncodeBits(overflow, 32);
    } else {
      rc.EncodeCell(ApeRangeConstants.Counts[overflow], ApeRangeConstants.Widths[overflow], ApeRangeConstants.Total);
    }

    if (k > 0)
      rc.EncodeBits(remainder, (int)k);

    UpdateRice(s, magnitude);
  }
}
