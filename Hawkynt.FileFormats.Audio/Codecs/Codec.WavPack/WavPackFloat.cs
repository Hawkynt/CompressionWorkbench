#pragma warning disable CS1591

namespace Codec.WavPack;

/// <summary>
/// IEEE-754 32-bit floating-point support for WavPack, a faithful port of the
/// reference <c>pack_floats.c</c> (<c>scan_float_data</c>/<c>send_float_data</c>)
/// and <c>unpack_floats.c</c> (<c>float_values</c>). WavPack stores float audio by
/// first reducing every sample to a (lossy) 24-bit signed integer — which the
/// regular integer decorrelation and entropy coder handle unchanged — and then
/// carrying the bits needed for exact reconstruction (the mantissa bits shifted
/// out, the sign of zero, denormals and the <c>±inf</c>/NaN exceptions) in a
/// separate "extension" bitstream (the <c>wvx</c> sub-block, id <c>0x0c</c>),
/// prefixed by a 32-bit CRC.
/// <para>
/// The on-disk parameters live in the <c>FLOAT_INFO</c> sub-block (id <c>0x08</c>,
/// four bytes: <c>float_flags</c>, <c>float_shift</c>, <c>float_max_exp</c>,
/// <c>float_norm_exp</c>). The f32 helpers mirror the reference accessor macros
/// (<c>get_mantissa</c>/<c>get_exponent</c>/<c>get_sign</c> and their setters):
/// the value is just the raw IEEE-754 bit pattern stored in an <see cref="int"/>,
/// never the CLR <see cref="float"/> type, so no floating-point math is involved.
/// </para>
/// </summary>
internal static class WavPackFloat {

  // float_flags bits (wavpack_local.h).
  public const int ShiftOnes = 1;       // bits left-shifted into float = '1'
  public const int ShiftSame = 2;       // bits left-shifted into float are the same
  public const int ShiftSent = 4;       // bits shifted into float are sent literally
  public const int ZerosSent = 8;       // "zeros" are not all real zeros
  public const int NegZeros = 0x10;     // contains negative zeros
  public const int Exceptions = 0x20;   // contains exceptions (inf, nan, etc.)

  /// <summary>Parsed <c>FLOAT_INFO</c> sub-block (id 0x08).</summary>
  public readonly record struct FloatInfo(int Flags, int Shift, int MaxExp, int NormExp);

  public static FloatInfo ReadFloatInfo(ReadOnlySpan<byte> p) =>
    p.Length < 4 ? default : new FloatInfo(p[0], p[1], p[2], p[3]);

  public static byte[] WriteFloatInfo(FloatInfo info) =>
    [(byte)info.Flags, (byte)info.Shift, (byte)info.MaxExp, (byte)info.NormExp];

  // ── f32 bit-field accessors (verbatim from the reference macros) ─────────────

  private static int GetMantissa(int f) => f & 0x7FFFFF;
  private static int GetMagnitude(int f) => f & 0x7FFFFFFF;
  private static int GetExponent(int f) => (f >> 23) & 0xFF;
  private static int GetSign(int f) => (f >> 31) & 0x1;

  private static void SetMantissa(ref int f, int v) => f ^= (f ^ v) & 0x7FFFFF;
  private static void SetExponent(ref int f, int v) => f ^= (int)((f ^ ((uint)v << 23)) & 0x7F800000);
  private static void SetSign(ref int f, int v) => f ^= (int)((f ^ ((uint)v << 31)) & 0x80000000);

  /// <summary>The <c>crc_x</c> WavPack stores in the wvx sub-block: a running
  /// hash of every f32's mantissa/exponent/sign (reference <c>scan_float_data</c>).
  /// Written for fidelity; the decoder does not gate output on it.</summary>
  public static uint ComputeCrc(int[] original) {
    var crc = 0xFFFFFFFFu;
    foreach (var f in original)
      crc = (uint)(crc * 27 + GetMantissa(f) * 9 + GetExponent(f) * 3 + GetSign(f));
    return crc;
  }

  // ── encode-side scan (scan_float_data) ───────────────────────────────────────

  /// <summary>Faithful port of <c>scan_float_data</c>. Converts the interleaved f32
  /// bit-pattern array <paramref name="values"/> in place to the (lossy) signed
  /// integers the integer coder operates on, and fills <paramref name="info"/> with
  /// the parameters needed to losslessly restore the originals. Returns the original
  /// f32 bit patterns so <see cref="SendFloatData"/> can write the extension stream.</summary>
  public static int[] ScanFloatData(int[] values, out FloatInfo info) {
    var original = (int[])values.Clone();

    var shiftedOnes = 0;
    var shiftedZeros = 0;
    var shiftedBoth = 0;
    var falseZeros = 0;
    var negZeros = 0;
    uint ordata = 0;
    var maxMag = 0;
    var maxExp = 0;
    var floatShift = 0;
    var floatFlags = 0;

    // Pass 1: find the max magnitude that does not have a reserved exponent (255).
    foreach (var f in values)
      if (GetExponent(f) < 255 && GetMagnitude(f) > maxMag)
        maxMag = GetMagnitude(f);

    // Round up so the converted integers are at most just over 24-bit signed.
    if (GetExponent(maxMag) != 0)
      maxExp = GetExponent(maxMag + 0x7F0000);

    // Pass 2: convert each float to its lossy integer and tally the shift cases.
    for (var i = 0; i < values.Length; ++i) {
      var f = values[i];
      int value;
      int shiftCount;

      if (GetExponent(f) == 255) {
        floatFlags |= Exceptions;
        value = 0x1000000;
        shiftCount = 0;
      } else if (GetExponent(f) != 0) {
        shiftCount = maxExp - GetExponent(f);
        value = 0x800000 + GetMantissa(f);
      } else {
        shiftCount = maxExp != 0 ? maxExp - 1 : 0;
        value = GetMantissa(f);
      }

      if (shiftCount < 25)
        value >>= shiftCount;
      else
        value = 0;

      if (value == 0) {
        if (GetExponent(f) != 0 || GetMantissa(f) != 0)
          ++falseZeros;
        else if (GetSign(f) != 0)
          ++negZeros;
      } else if (shiftCount != 0) {
        var mask = (1 << shiftCount) - 1;
        if ((GetMantissa(f) & mask) == 0)
          ++shiftedZeros;
        else if ((GetMantissa(f) & mask) == mask)
          ++shiftedOnes;
        else
          ++shiftedBoth;
      }

      ordata |= (uint)value;
      values[i] = GetSign(f) != 0 ? -value : value;
    }

    // Decide how the shifted-out bits are encoded.
    if (shiftedBoth != 0)
      floatFlags |= ShiftSent;
    else if (shiftedOnes != 0 && shiftedZeros == 0)
      floatFlags |= ShiftOnes;
    else if (shiftedOnes != 0 && shiftedZeros != 0)
      floatFlags |= ShiftSame;
    else if (ordata != 0 && (ordata & 1) == 0) {
      // Only zeros shift out: the data has fewer than 24/25 bits of resolution, so
      // reduce the magnitude of the encoded integers (saving those trailing zeros).
      while ((ordata & 1) == 0) {
        ++floatShift;
        ordata >>= 1;
      }

      for (var i = 0; i < values.Length; ++i)
        values[i] >>= floatShift;
    }

    if (falseZeros != 0 || negZeros != 0)
      floatFlags |= ZerosSent;
    if (negZeros != 0)
      floatFlags |= NegZeros;

    // float_norm_exp 127 means +/-1.0 maps to unity; this is the canonical value
    // for data that originated as IEEE floats in the [-1,1] range.
    info = new FloatInfo(floatFlags, floatShift, maxExp, 127);
    return original;
  }

  /// <summary>True when <see cref="ScanFloatData"/> determined an extension stream
  /// (wvx sub-block) is required for lossless restoration — i.e. anything beyond a
  /// plain integer-derived float needs the extra bits.</summary>
  public static bool NeedsExtensionStream(FloatInfo info) =>
    (info.Flags & (Exceptions | ZerosSent | ShiftSent | ShiftSame)) != 0;

  // ── encode-side extension stream (send_float_data) ───────────────────────────

  /// <summary>Faithful port of <c>send_float_data</c>: writes the lossless
  /// extension bits for the original f32 patterns to the wvx writer.</summary>
  public static void SendFloatData(WavPackBitWriter w, int[] original, FloatInfo info) {
    var maxExp = info.MaxExp;

    foreach (var f in original) {
      int value;
      int shiftCount;

      if (GetExponent(f) == 255) {
        if (GetMantissa(f) != 0) {
          w.PutBit(1);
          w.PutBits((uint)GetMantissa(f), 23);
        } else
          w.PutBit(0);

        value = 0x1000000;
        shiftCount = 0;
      } else if (GetExponent(f) != 0) {
        shiftCount = maxExp - GetExponent(f);
        value = 0x800000 + GetMantissa(f);
      } else {
        shiftCount = maxExp != 0 ? maxExp - 1 : 0;
        value = GetMantissa(f);
      }

      if (shiftCount < 25)
        value >>= shiftCount;
      else
        value = 0;

      if (value == 0) {
        if ((info.Flags & ZerosSent) != 0) {
          if (GetExponent(f) != 0 || GetMantissa(f) != 0) {
            w.PutBit(1);
            w.PutBits((uint)GetMantissa(f), 23);
            if (maxExp >= 25)
              w.PutBits((uint)GetExponent(f), 8);
            w.PutBit(GetSign(f));
          } else {
            w.PutBit(0);
            if ((info.Flags & NegZeros) != 0)
              w.PutBit(GetSign(f));
          }
        }
      } else if (shiftCount != 0) {
        if ((info.Flags & ShiftSent) != 0) {
          var data = GetMantissa(f) & ((1 << shiftCount) - 1);
          w.PutBits((uint)data, shiftCount);
        } else if ((info.Flags & ShiftSame) != 0)
          w.PutBit(GetMantissa(f) & 1);
      }
    }
  }

  // ── decode-side reconstruction (float_values) ────────────────────────────────

  /// <summary>Faithful port of <c>float_values</c>: rebuilds the IEEE-754 bit
  /// patterns from the decoded integer <paramref name="values"/> using the wvx
  /// reader (when present) and the parsed <paramref name="info"/>. The result is
  /// written back into <paramref name="values"/> as raw float bit patterns.</summary>
  public static void FloatValues(int[] values, FloatInfo info, WavPackBitReader? wvx,
      int minShiftedZeros, int maxShiftedOnes) {
    if (wvx == null) {
      FloatValuesNoWvx(values, info);
      return;
    }

    for (var i = 0; i < values.Length; ++i) {
      var shiftCount = 0;
      var exp = info.MaxExp;
      var outval = 0;
      var v = values[i];

      if (v == 0) {
        if ((info.Flags & ZerosSent) != 0) {
          if (wvx.GetBit() != 0) {
            SetMantissa(ref outval, (int)wvx.GetBits(23));
            if (exp >= 25)
              SetExponent(ref outval, (int)wvx.GetBits(8));
            SetSign(ref outval, wvx.GetBit());
          } else if ((info.Flags & NegZeros) != 0)
            SetSign(ref outval, wvx.GetBit());
        }
      } else {
        v = (int)((uint)v << (info.Shift & 0x1F));

        if (v < 0) {
          v = -v;
          SetSign(ref outval, 1);
        }

        if (v == 0x1000000) {
          if (wvx.GetBit() != 0)
            SetMantissa(ref outval, (int)wvx.GetBits(23));
          SetExponent(ref outval, 255);
        } else {
          if (exp != 0)
            while ((v & 0x800000) == 0 && --exp != 0) {
              ++shiftCount;
              v = (int)((uint)v << 1);
            }

          if ((shiftCount &= 0x1F) != 0) {
            if ((info.Flags & ShiftOnes) != 0 ||
                ((info.Flags & ShiftSame) != 0 && wvx.GetBit() != 0))
              v |= (1 << shiftCount) - 1;
            else if ((info.Flags & ShiftSent) != 0) {
              var mask = (1 << shiftCount) - 1;
              var numZeros = 0;

              if (maxShiftedOnes != 0 && shiftCount > maxShiftedOnes)
                numZeros = shiftCount - maxShiftedOnes;

              if (minShiftedZeros > numZeros)
                numZeros = minShiftedZeros > shiftCount ? shiftCount : minShiftedZeros;

              if ((shiftCount -= numZeros) > 0) {
                var temp = (int)wvx.GetBits(shiftCount);
                v |= (temp << numZeros) & mask;
              }
            }
          }

          SetMantissa(ref outval, v);
          SetExponent(ref outval, exp);
        }
      }

      values[i] = outval;
    }
  }

  /// <summary>Port of <c>float_values_nowvx</c>: reconstruction when no extension
  /// stream is present (the float data was originally integer-derived).</summary>
  private static void FloatValuesNoWvx(int[] values, FloatInfo info) {
    for (var i = 0; i < values.Length; ++i) {
      var shiftCount = 0;
      var exp = info.MaxExp;
      var outval = 0;
      var v = values[i];

      if (v != 0) {
        v = (int)((uint)v << (info.Shift & 0x1F));

        if (v < 0) {
          v = -v;
          SetSign(ref outval, 1);
        }

        if (v >= 0x1000000) {
          while ((v & 0xF000000) != 0) {
            v >>= 1;
            ++exp;
          }
        } else if (exp != 0) {
          while ((v & 0x800000) == 0 && --exp != 0) {
            ++shiftCount;
            v = (int)((uint)v << 1);
          }

          if ((shiftCount &= 0x1F) != 0 && (info.Flags & ShiftOnes) != 0)
            v |= (1 << shiftCount) - 1;
        }

        SetMantissa(ref outval, v);
        SetExponent(ref outval, exp);
      }

      values[i] = outval;
    }
  }
}
