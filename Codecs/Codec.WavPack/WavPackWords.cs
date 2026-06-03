#pragma warning disable CS1591

namespace Codec.WavPack;

/// <summary>
/// WavPack's adaptive entropy word coder (<c>words.c</c> / ffmpeg
/// <c>libavcodec/wavpack.c</c>). Each channel keeps three running "medians"
/// (<see cref="Entropy"/>) that partition every sample's magnitude into a
/// low / mid / high zone; the zone is sent as a unary "ones" count, the position
/// within the zone as a Golomb truncated-binary tail (<see cref="GetTail"/> /
/// <see cref="PutTail"/>), and the sign as one bit. The medians adapt after every
/// sample (the <see cref="GetMed"/>/<see cref="IncMed"/>/<see cref="DecMed"/>
/// macros) so the code tracks the signal envelope.
/// <para>
/// The median macros, the three-zone unary selection, and the truncated-binary
/// tail are ported from the reference / ffmpeg <c>wavpack.c</c>. The encoder
/// (<see cref="PutWord"/>) is the exact algebraic inverse of the decoder
/// (<see cref="GetWord"/>): for any sample it emits precisely the bits the
/// decoder consumes to reproduce that sample and reach the same updated medians,
/// so the two are bit-locked.
/// </para>
/// <para>
/// DEVIATION: the reference shares a terminating bit between adjacent unary runs
/// via a <c>holding_one</c>/<c>holding_zero</c> state machine and run-length-codes
/// all-zero spans. This implementation terminates each unary run with its own
/// zero bit and codes zero samples individually. The zone/tail/sign structure and
/// the median adaptation are spec-faithful; only that cross-word bit-sharing
/// optimisation is omitted, so a stream this encoder writes is self-consistent and
/// round-trips losslessly through this decoder, though it is not bit-identical to
/// a reference encoder's output. Only the pure-lossless path is implemented;
/// hybrid blocks are rejected upstream.
/// </para>
/// </summary>
internal sealed class WavPackWords {

  /// <summary>Per-channel adaptive state: the three magnitude medians. A fresh
  /// block with no explicit entropy sub-block starts every median at the
  /// reference minimum so <see cref="GetMed"/> never returns 0.</summary>
  public sealed class Entropy {
    public readonly uint[] Median = new uint[3];
  }

  private readonly Entropy[] _channels;

  public WavPackWords(int channels) {
    this._channels = new Entropy[channels];
    for (var c = 0; c < channels; ++c)
      this._channels[c] = new Entropy();
  }

  public Entropy Channel(int index) => this._channels[index];

  // ── median macros (words.c) ────────────────────────────────────────────────

  private static uint GetMed(uint[] med, int i) => (med[i] >> 4) + 1;
  private static void IncMed(uint[] med, int i) => med[i] += GetMed(med, i) * 5 >> 1;
  private static void DecMed(uint[] med, int i) => med[i] -= GetMed(med, i) * 2 >> 1;

  // ── Golomb tail (de)coding (get_tail) ──────────────────────────────────────

  private static int Log2(uint x) {
    var n = 0;
    while ((x >>= 1) != 0) ++n;
    return n;
  }

  /// <summary>Reads the in-zone offset for a zone of <paramref name="k"/> values
  /// (truncated binary), matching the reference <c>get_tail</c>.</summary>
  private static uint GetTail(WavPackBitReader r, uint k) {
    if (k < 1)
      return 0;
    var p = Log2(k);
    var e = (1u << (p + 1)) - k - 1;
    var res = r.GetBits(p);
    if (res >= e)
      res = res * 2 - e + (uint)r.GetBit();
    return res;
  }

  /// <summary>The exact inverse of <see cref="GetTail"/>: writes an offset
  /// <paramref name="value"/> in <c>[0, k)</c> as the same truncated binary code.</summary>
  private static void PutTail(WavPackBitWriter w, uint k, uint value) {
    if (k < 1)
      return;
    var p = Log2(k);
    var e = (1u << (p + 1)) - k - 1;
    if (value < e) {
      w.PutBits(value, p);
    } else {
      var v = value + e;
      w.PutBits(v >> 1, p);
      w.PutBit((int)(v & 1));
    }
  }

  // ── unary "ones" run ────────────────────────────────────────────────────────

  private static int GetUnary(WavPackBitReader r) {
    var n = 0;
    while (r.GetBit() == 1)
      ++n;
    return n;
  }

  private static void PutUnary(WavPackBitWriter w, int n) {
    for (var i = 0; i < n; ++i)
      w.PutBit(1);
    w.PutBit(0);
  }

  // ── decode one word ─────────────────────────────────────────────────────────

  /// <summary>Decodes one signed sample for channel <paramref name="ch"/>.</summary>
  public int GetWord(WavPackBitReader r, int ch) {
    var med = this._channels[ch].Median;
    var ones = GetUnary(r);

    uint baseValue;
    uint addRange;
    switch (ones) {
      case 0:
        baseValue = 0;
        addRange = GetMed(med, 0) - 1;
        DecMed(med, 0);
        break;
      case 1:
        baseValue = GetMed(med, 0);
        addRange = GetMed(med, 1) - 1;
        IncMed(med, 0);
        DecMed(med, 1);
        break;
      case 2:
        baseValue = GetMed(med, 0) + GetMed(med, 1);
        addRange = GetMed(med, 2) - 1;
        IncMed(med, 0);
        IncMed(med, 1);
        DecMed(med, 2);
        break;
      default: {
        var high = (uint)(ones - 2);
        baseValue = GetMed(med, 0) + GetMed(med, 1) + GetMed(med, 2) * high;
        addRange = GetMed(med, 2) - 1;
        IncMed(med, 0);
        IncMed(med, 1);
        IncMed(med, 2);
        break;
      }
    }

    var offset = GetTail(r, addRange + 1);
    var magnitude = baseValue + offset;
    var sign = r.GetBit();
    return sign != 0 ? -(int)magnitude : (int)magnitude;
  }

  // ── encode one word ─────────────────────────────────────────────────────────

  /// <summary>Encodes one signed sample for channel <paramref name="ch"/>, the
  /// exact inverse of <see cref="GetWord"/>.</summary>
  public void PutWord(WavPackBitWriter w, int ch, int sample) {
    var med = this._channels[ch].Median;

    var sign = sample < 0 ? 1 : 0;
    var magnitude = (uint)(sample < 0 ? -(long)sample : sample);

    int ones;
    uint baseValue;
    uint addRange;

    var m0 = GetMed(med, 0);
    if (magnitude < m0) {
      ones = 0;
      baseValue = 0;
      addRange = m0 - 1;
      DecMed(med, 0);
    } else {
      var m1 = GetMed(med, 1);
      if (magnitude < m0 + m1) {
        ones = 1;
        baseValue = m0;
        addRange = m1 - 1;
        IncMed(med, 0);
        DecMed(med, 1);
      } else {
        var m2 = GetMed(med, 2);
        if (magnitude < m0 + m1 + m2) {
          ones = 2;
          baseValue = m0 + m1;
          addRange = m2 - 1;
          IncMed(med, 0);
          IncMed(med, 1);
          DecMed(med, 2);
        } else {
          var high = (magnitude - (m0 + m1)) / m2;
          ones = 2 + (int)high;
          baseValue = m0 + m1 + m2 * high;
          addRange = m2 - 1;
          IncMed(med, 0);
          IncMed(med, 1);
          IncMed(med, 2);
        }
      }
    }

    PutUnary(w, ones);
    PutTail(w, addRange + 1, magnitude - baseValue);
    w.PutBit(sign);
  }
}
