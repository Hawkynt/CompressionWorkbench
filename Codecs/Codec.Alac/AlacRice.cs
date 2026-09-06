#pragma warning disable CS1591

using System.Numerics;

namespace Codec.Alac;

/// <summary>
/// Adaptive Golomb/Rice residual coder, following the reference <c>ag_dec.c</c>
/// (<c>dyn_decomp</c>) and <c>ag_enc.c</c> (<c>dyn_comp</c>).
/// <para>
/// A running mean <c>mb</c> tracks recent residual magnitudes. Each symbol is coded
/// against a modulus <c>m = 2^k - 1</c> (note: <em>not</em> <c>2^k</c> — the coder is
/// Golomb, not plain Rice) where <c>k = floor(log2((mb &gt;&gt; 9) + 3))</c> clamped to the
/// cookie's <c>kb</c>. A value <c>n</c> is written as <c>n / m</c> ones, a zero, then
/// <c>n % m + 1</c> in <c>k</c> bits — and when the remainder is zero the last bit is
/// dropped, which is why the decoder peeks the suffix and steps back a bit when it
/// reads below 2. A prefix of nine ones escapes to a literal of the full channel
/// width.
/// </para>
/// <para>
/// After every symbol the mean is re-estimated as
/// <c>mb = pb*n + mb - ((pb*mb) &gt;&gt; 9)</c>. When the mean drops below <c>2^7</c> the
/// coder switches to a run-length symbol counting the following zero residuals, then
/// resets the mean and sets <c>zmode</c>, which biases the next symbol by one — the
/// sign-modifier that makes the zig-zag mapping asymmetric across a run boundary.
/// </para>
/// </summary>
internal static class AlacRice {

  private const int Qbshift = 9;
  private const int Qb = 1 << Qbshift;
  private const int Mmulshift = 2;
  private const int Mdenshift = Qbshift - Mmulshift - 1; // 6
  private const int Moff = 1 << (Mdenshift - 2);         // 16
  private const int Bitoff = 24;
  private const int MaxPrefix16 = 9;
  private const int MaxPrefix32 = 9;
  private const int MaxDatatypeBits16 = 16;
  private const uint MaxMeanClamp = 0xFFFF;

  /// <summary>The reference <c>lead()</c>: leading zero bits of a 32-bit word, 32 for zero.</summary>
  private static int Lead(uint value) => BitOperations.LeadingZeroCount(value);

  /// <summary>The reference <c>lg3a()</c>: floor-log2 of <c>x + 3</c>, used to size <c>k</c>.</summary>
  private static int Lg3a(uint x) => 31 - Lead(x + 3);

  /// <summary>Counts the leading one bits — the unary prefix length.</summary>
  private static int LeadingOnes(uint value) => BitOperations.LeadingZeroCount(~value);

  // ── Decode ─────────────────────────────────────────────────────────────────

  /// <summary>
  /// Decodes <paramref name="numSamples"/> signed residuals into <paramref name="output"/>.
  /// <paramref name="pb"/>/<paramref name="mb0"/>/<paramref name="kb"/> are the tunables from the
  /// magic cookie (with <paramref name="pb"/> already scaled by the frame's pb factor) and
  /// <paramref name="maxSize"/> is the channel width used by the escape literal.
  /// </summary>
  public static void Decode(
      AlacBitReader bits, int[] output, int numSamples, int pb, int mb0, int kb, int maxSize) {
    if (kb is < 1 or > 31)
      throw new InvalidDataException($"ALAC cookie declares an unusable Rice limit kb={kb}.");

    var mb = (uint)mb0;
    var wb = (1u << kb) - 1;
    var zmode = 0u;
    var c = 0;

    while (c < numSamples) {
      var k = Lg3a(mb >> Qbshift);
      if (k > kb)
        k = kb;
      var m = (1u << k) - 1;

      var n = DynGet32(bits, m, k, maxSize);

      // The least significant bit is the sign, biased by zmode across a run boundary.
      var decoded = n + zmode;
      var multiplier = (int)-(decoded & 1) | 1;
      output[c++] = (int)((decoded + 1) >> 1) * multiplier;

      mb = (uint)pb * decoded + mb - ((uint)pb * mb >> Qbshift);
      if (n > MaxMeanClamp)
        mb = MaxMeanClamp;

      zmode = 0;

      if (mb << Mmulshift >= Qb || c >= numSamples)
        continue;

      // The mean has collapsed: the next symbol is a run length of zero residuals.
      zmode = 1;
      k = Lead(mb) - Bitoff + (int)((mb + Moff) >> Mdenshift);
      var mz = ((1u << k) - 1) & wb;

      var run = DynGet(bits, mz, k);
      if (run > (uint)(numSamples - c))
        throw new InvalidDataException("ALAC zero run overruns the frame.");

      for (var j = 0u; j < run; ++j)
        output[c++] = 0;

      if (run >= 65535)
        zmode = 0;

      mb = 0;
    }
  }

  /// <summary>
  /// Reads one residual symbol: a unary prefix of at most nine ones, then a
  /// <paramref name="k"/>-bit suffix that is one bit shorter when its value is below two.
  /// A saturated prefix escapes to a <paramref name="maxbits"/>-wide literal.
  /// </summary>
  private static uint DynGet32(AlacBitReader bits, uint m, int k, int maxbits) {
    var prefix = LeadingOnes(bits.Peek(32));

    if (prefix >= MaxPrefix32) {
      bits.Advance(MaxPrefix32);
      return bits.Read(maxbits);
    }

    bits.Advance(prefix + 1);
    var result = (uint)prefix * m;
    if (k == 1)
      return (uint)prefix;

    var suffix = bits.Peek(k);
    if (suffix < 2) {
      bits.Advance(k - 1);
      return result;
    }

    bits.Advance(k);
    return result + suffix - 1;
  }

  /// <summary>
  /// Reads one zero-run length. Same shape as <see cref="DynGet32"/> but the escape
  /// literal is a fixed 16 bits and there is no special case for <c>k == 1</c>.
  /// </summary>
  private static uint DynGet(AlacBitReader bits, uint m, int k) {
    var prefix = LeadingOnes(bits.Peek(32));

    if (prefix >= MaxPrefix16) {
      bits.Advance(MaxPrefix16);
      return bits.Read(MaxDatatypeBits16);
    }

    bits.Advance(prefix + 1);
    var result = (uint)prefix * m;

    var suffix = bits.Peek(k);
    if (suffix < 2) {
      bits.Advance(k - 1);
      return result;
    }

    bits.Advance(k);
    return result + suffix - 1;
  }

  // ── Encode ─────────────────────────────────────────────────────────────────

  /// <summary>
  /// Encodes <paramref name="numSamples"/> signed residuals with the same adaptive scheme,
  /// so <see cref="Decode"/> reconstructs them exactly.
  /// </summary>
  public static void Encode(
      AlacBitWriter bits, int[] input, int numSamples, int pb, int mb0, int kb, int bitSize) {
    var mb = (uint)mb0;
    var wb = (1u << kb) - 1;
    var zmode = 0u;
    var c = 0;

    while (c < numSamples) {
      var k = Lg3a(mb >> Qbshift);
      if (k > kb)
        k = kb;
      var m = (1u << k) - 1;

      var del = input[c++];

      // Zig-zag with the zmode bias, exactly inverting the decoder's sign step.
      var isNegative = del >> 31;
      var magnitude = (del ^ isNegative) - isNegative;
      var n = (uint)((magnitude << 1) - (isNegative & 1)) - zmode;

      DynCode32(bits, bitSize, m, k, n);

      mb = (uint)pb * (n + zmode) + mb - ((uint)pb * mb >> Qbshift);
      if (n > MaxMeanClamp)
        mb = MaxMeanClamp;

      zmode = 0;

      if (mb << Mmulshift >= Qb || c >= numSamples)
        continue;

      zmode = 1;
      var run = 0u;
      while (c < numSamples && input[c] == 0) {
        ++c;
        ++run;
        if (run < 65535)
          continue;
        zmode = 0;
        break;
      }

      k = Lead(mb) - Bitoff + (int)((mb + Moff) >> Mdenshift);
      var mz = ((1u << k) - 1) & wb;

      DynCode(bits, mz, k, run);
      mb = 0;
    }
  }

  /// <summary>Writes one residual symbol; the exact inverse of <see cref="DynGet32"/>.</summary>
  private static void DynCode32(AlacBitWriter bits, int maxbits, uint m, int k, uint n) {
    if (m == 0)
      throw new InvalidOperationException("ALAC Golomb modulus collapsed to zero.");

    var div = n / m;
    if (div < MaxPrefix32) {
      var mod = n - m * div;
      var de = mod == 0 ? 1u : 0u;
      var numBits = (int)(div + (uint)k + 1 - de);
      if (numBits <= 25) {
        var value = (((1u << (int)div) - 1) << (numBits - (int)div)) + mod + 1 - de;
        bits.Write(value, numBits);
        return;
      }
    }

    bits.Write((1u << MaxPrefix32) - 1, MaxPrefix32);
    bits.Write(n, maxbits);
  }

  /// <summary>Writes one zero-run length; the exact inverse of <see cref="DynGet"/>.</summary>
  private static void DynCode(AlacBitWriter bits, uint m, int k, uint n) {
    if (m == 0)
      throw new InvalidOperationException("ALAC Golomb modulus collapsed to zero.");

    var div = n / m;
    if (div < MaxPrefix16) {
      var mod = n % m;
      var de = mod == 0 ? 1u : 0u;
      var numBits = (int)(div + (uint)k + 1 - de);
      if (numBits <= MaxPrefix16 + MaxDatatypeBits16) {
        var value = (((1u << (int)div) - 1) << (numBits - (int)div)) + mod + 1 - de;
        bits.Write(value, numBits);
        return;
      }
    }

    bits.Write((((1u << MaxPrefix16) - 1) << MaxDatatypeBits16) + n, MaxPrefix16 + MaxDatatypeBits16);
  }
}
