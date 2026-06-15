#pragma warning disable CS1591

namespace Codec.Alac;

/// <summary>
/// Adaptive Golomb/Rice residual coder — modelled on Apple's <c>ag_dec.c</c>
/// (<c>dyn_decomp</c>) and <c>ag_enc.c</c> (<c>dyn_comp</c>) from the open-source
/// ALAC reference. The coder keeps a running mean <c>mb</c> of recent residual
/// magnitudes; the per-symbol Rice parameter <c>k</c> is derived from that mean and
/// clamped by the cookie's <c>kb</c>, and <c>mb</c> is re-estimated after every value
/// using the <c>pb</c>/<c>mb</c> tunables. Long runs of zero residuals collapse into a
/// single run-length symbol, exactly as the reference escapes via its <c>BITOFF</c>
/// path. The values handled here are the unsigned (zig-zag) residuals the dynamic
/// predictor consumes; the encoder and decoder are exact inverses of one another.
/// </summary>
internal static class AlacRice {

  private const int Qbshift = 9;
  private const int Mmulshift = 2;
  private const int Mdenshift = Qbshift - Mmulshift - 1; // 6
  private const int Moff = 1 << (Mdenshift - 2);          // 16
  private const int Maxrun = 255;

  /// <summary>Count of leading zero bits in a 32-bit word (32 for zero).</summary>
  private static int Lead(uint value)
    => value == 0 ? 32 : System.Numerics.BitOperations.LeadingZeroCount(value);

  /// <summary>Apple's <c>lg3a</c>: a cheap floor-log2 of <c>x + 3</c> used to size <c>k</c>.</summary>
  private static int Lg3a(uint x) => 31 - Lead(x + 3);

  /// <summary>Re-estimates the running mean after observing residual <paramref name="del"/>.</summary>
  private static uint UpdateMean(uint mb, uint pb, uint del)
    => pb * del + mb - ((pb * mb) >> Qbshift);

  // ── Decode ─────────────────────────────────────────────────────────────────

  /// <summary>
  /// Decodes <paramref name="numSamples"/> unsigned residuals into <paramref name="output"/>
  /// at <paramref name="outOffset"/>. <paramref name="kb"/>/<paramref name="pbIn"/>/<paramref name="mbIn"/>
  /// are the cookie tunables; <paramref name="sampleBits"/> is the per-channel sample width.
  /// </summary>
  public static void Decode(
      AlacBitReader bits, int[] output, int outOffset, int numSamples,
      int kb, int pbIn, int mbIn, int sampleBits) {
    var mb = (uint)mbIn;
    var pb = (uint)pbIn;
    var c = 0;

    while (c < numSamples) {
      var k = Lg3a(mb >> Qbshift);
      if (k > kb) k = kb;
      if (k < 1) k = 1;

      var del = ReadValue(bits, k, sampleBits);
      output[outOffset + c++] = (int)del;
      mb = UpdateMean(mb, pb, del);

      if (del == 0 && c < numSamples) {
        var kRun = Lg3a((uint)(Moff + (mb >> Mdenshift)));
        if (kRun > kb) kRun = kb;
        if (kRun < 1) kRun = 1;
        var run = (int)ReadValue(bits, kRun, sampleBits);
        var fill = Math.Min(run, numSamples - c);
        for (var i = 0; i < fill; ++i)
          output[outOffset + c + i] = 0;
        c += fill;
        mb = 0;
      }
    }
  }

  /// <summary>
  /// Reads one adaptive-Rice symbol: a unary prefix (capped) plus a <paramref name="k"/>-bit
  /// suffix, escaping to a full <paramref name="sampleBits"/>-bit literal when the prefix
  /// saturates. Mirrors <see cref="WriteValue"/> bit-for-bit.
  /// </summary>
  private static uint ReadValue(AlacBitReader bits, int k, int sampleBits) {
    var maxPrefix = Math.Max(1, sampleBits - k);

    var prefix = 0;
    while (prefix < maxPrefix && bits.ReadOne() != 0)
      ++prefix;

    if (prefix >= maxPrefix)
      return bits.Read(sampleBits);

    var suffix = bits.Read(k);
    return (uint)(prefix << k) | suffix;
  }

  // ── Encode ─────────────────────────────────────────────────────────────────

  /// <summary>
  /// Encodes <paramref name="numSamples"/> unsigned residuals from <paramref name="input"/> at
  /// <paramref name="inOffset"/> with the same adaptive scheme, so <see cref="Decode"/>
  /// reconstructs them exactly.
  /// </summary>
  public static void Encode(
      AlacBitWriter bits, int[] input, int inOffset, int numSamples,
      int kb, int pbIn, int mbIn, int sampleBits) {
    var mb = (uint)mbIn;
    var pb = (uint)pbIn;
    var c = 0;

    while (c < numSamples) {
      var k = Lg3a(mb >> Qbshift);
      if (k > kb) k = kb;
      if (k < 1) k = 1;

      var del = (uint)input[inOffset + c++];
      WriteValue(bits, del, k, sampleBits);
      mb = UpdateMean(mb, pb, del);

      if (del == 0 && c < numSamples) {
        var run = 0;
        while (c + run < numSamples && input[inOffset + c + run] == 0 && run < Maxrun)
          ++run;

        var kRun = Lg3a((uint)(Moff + (mb >> Mdenshift)));
        if (kRun > kb) kRun = kb;
        if (kRun < 1) kRun = 1;
        WriteValue(bits, (uint)run, kRun, sampleBits);
        c += run;
        mb = 0;
      }
    }
  }

  /// <summary>Writes one adaptive-Rice symbol; the exact inverse of <see cref="ReadValue"/>.</summary>
  private static void WriteValue(AlacBitWriter bits, uint value, int k, int sampleBits) {
    var maxPrefix = Math.Max(1, sampleBits - k);
    var prefix = (int)(value >> k);

    if (prefix >= maxPrefix) {
      for (var i = 0; i < maxPrefix; ++i)
        bits.WriteOne(1);
      bits.Write(value, sampleBits);
      return;
    }

    for (var i = 0; i < prefix; ++i)
      bits.WriteOne(1);
    bits.WriteOne(0);
    bits.Write(value & ((1u << k) - 1u), k);
  }
}
