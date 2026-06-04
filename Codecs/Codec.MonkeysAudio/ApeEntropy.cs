#pragma warning disable CS1591

namespace Codec.MonkeysAudio;

/// <summary>
/// Monkey's Audio "current" (3.99 / v3990) residual entropy stage — a byte-exact
/// port of the reference SDK's <c>CUnBitArray::DecodeValueRange</c> (decode) and
/// <c>CBitArray::EncodeValue</c> (encode). Each residual is folded to an unsigned
/// magnitude (sign in bit 0), split into an overflow quotient and a base remainder
/// around a <c>pivot = max(ksum/32, 1)</c>, and coded as: an overflow class through
/// the 64-entry cumulative table (escaping to a raw 32-bit count for the last
/// class), followed by the base (a single divisor read when <c>pivot &lt; 1&lt;&lt;16</c>,
/// or a two-piece split-factor read otherwise). The Rice <c>k</c> / <c>ksum</c>
/// state tracks the running magnitude average exactly as the reference does.
/// </summary>
internal sealed class ApeEntropy {

  /// <summary>Per-channel adaptive Rice state (SDK <c>BIT_ARRAY_STATE</c>: k + nKSum).</summary>
  public sealed class State {
    public int K = 10;
    public uint KSum = (1u << 10) * 16;

    public void Flush() {
      this.K = 10;
      this.KSum = (1u << 10) * 16;
    }
  }

  private readonly State[] _states;

  public ApeEntropy(int channels) {
    this._states = new State[channels];
    for (var c = 0; c < channels; ++c)
      this._states[c] = new State();
  }

  public void FlushStates() {
    foreach (var s in this._states)
      s.Flush();
  }

  // Reference k update ladder (BitArray.cpp / UnBitArray.cpp): step k toward log2 of
  // the running magnitude average using K_SUM_MIN_BOUNDARY.
  private static void UpdateKSum(State s, uint magnitude) {
    s.KSum += ((magnitude + 1) / 2) - ((s.KSum + 16) >> 5);
    // Reference ladder (BitArray.cpp / UnBitArray.cpp). K_SUM_MIN_BOUNDARY has 32
    // entries; k stays well within range for 24-bit magnitudes.
    if (s.KSum < ApeRangeConstants.KSumBoundary[s.K])
      --s.K;
    else if (s.KSum >= ApeRangeConstants.KSumBoundary[s.K + 1])
      ++s.K;
  }

  /// <summary>Decodes one signed residual for channel <paramref name="ch"/>
  /// (reference <c>DecodeValueRange</c>, v3990 branch).</summary>
  public int Decode(ApeRangeDecoder rc, int ch) {
    var s = this._states[ch];

    var pivot = Math.Max(s.KSum / 32u, 1u);

    // Overflow class via the cumulative table; the last class escapes to a raw
    // 32-bit overflow count.
    var rangeTotal = rc.DecodeFast(ApeRangeConstants.OverflowShift);
    var overflow = 0u;
    while (rangeTotal >= ApeRangeConstants.Counts[overflow + 1])
      ++overflow;
    rc.UpdateOverflow(ApeRangeConstants.Counts[overflow], ApeRangeConstants.Widths[overflow]);

    if (overflow == ApeRangeConstants.ModelElements - 1) {
      overflow = rc.DecodeFastWithUpdate(16) << 16;
      overflow |= rc.DecodeFastWithUpdate(16);
    }

    uint baseValue;
    if (pivot >= 1u << 16) {
      var pivotBits = 0;
      while (pivot >> pivotBits > 0)
        ++pivotBits;
      var splitFactor = 1u << (pivotBits - 16);
      var pivotA = pivot / splitFactor + 1;
      var pivotB = splitFactor;

      var baseA = rc.DecodeByDivisor(pivotA);
      var baseB = rc.DecodeByDivisor(pivotB);
      baseValue = baseA * splitFactor + baseB;
    } else {
      baseValue = rc.DecodeByDivisor(pivot);
    }

    var value = baseValue + overflow * pivot;
    UpdateKSum(s, value);

    // Convert to signed.
    return (value & 1) != 0 ? (int)(value >> 1) + 1 : -(int)(value >> 1);
  }

  /// <summary>Encodes one signed residual for channel <paramref name="ch"/>, the
  /// exact inverse of <see cref="Decode"/> (reference <c>EncodeValue</c>).</summary>
  public void Encode(ApeRangeEncoder rc, int ch, int value) {
    var s = this._states[ch];

    var magnitude = (uint)(value > 0 ? value * 2 - 1 : -value * 2);
    var originalKSum = s.KSum;

    UpdateKSum(s, magnitude);

    var pivot = Math.Max(originalKSum / 32u, 1u);
    var overflow = magnitude / pivot;
    var baseValue = magnitude - overflow * pivot;

    if (overflow < ApeRangeConstants.ModelElements - 1) {
      rc.EncodeFast(ApeRangeConstants.Widths[overflow], ApeRangeConstants.Counts[overflow], ApeRangeConstants.OverflowShift);
    } else {
      rc.EncodeFast(
        ApeRangeConstants.Widths[ApeRangeConstants.ModelElements - 1],
        ApeRangeConstants.Counts[ApeRangeConstants.ModelElements - 1],
        ApeRangeConstants.OverflowShift);
      rc.EncodeDirect((overflow >> 16) & 0xFFFF, 16);
      rc.EncodeDirect(overflow & 0xFFFF, 16);
    }

    if (pivot >= 1u << 16) {
      var pivotBits = 0;
      while (pivot >> pivotBits > 0)
        ++pivotBits;
      var splitFactor = 1u << (pivotBits - 16);
      var pivotA = pivot / splitFactor + 1;
      var pivotB = splitFactor;
      var baseA = baseValue / splitFactor;
      var baseB = baseValue % splitFactor;
      rc.EncodeByDivisor(baseA, pivotA);
      rc.EncodeByDivisor(baseB, pivotB);
    } else {
      rc.EncodeByDivisor(baseValue, pivot);
    }
  }
}
