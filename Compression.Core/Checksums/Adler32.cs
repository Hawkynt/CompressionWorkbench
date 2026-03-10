using System.Runtime.Intrinsics;

namespace Compression.Core.Checksums;

/// <summary>
/// Adler-32 checksum as used by zlib, with Nmax optimization and SIMD vectorization.
/// </summary>
public sealed class Adler32 : IChecksum {
  private const uint Mod = 65521; // largest prime less than 2^16
  private const int Nmax = 5552;  // max bytes before modulus is needed

  private uint _a = 1;
  private uint _b;

  /// <inheritdoc />
  public uint Value => (this._b << 16) | this._a;

  /// <inheritdoc />
  public void Reset() {
    this._a = 1;
    this._b = 0;
  }

  /// <inheritdoc />
  public void Update(byte b) {
    this._a = (this._a + b) % Mod;
    this._b = (this._b + this._a) % Mod;
  }

  /// <inheritdoc />
  public void Update(ReadOnlySpan<byte> data) {
    uint a = this._a;
    uint b = this._b;
    int offset = 0;
    int remaining = data.Length;

    while (remaining > 0) {
      int blockLen = Math.Min(remaining, Nmax);

      if (Vector256.IsHardwareAccelerated && blockLen >= 32) {
        UpdateSimd(data.Slice(offset, blockLen), ref a, ref b);
      } else {
        for (int i = 0; i < blockLen; ++i) {
          a += data[offset + i];
          b += a;
        }
      }

      a %= Mod;
      b %= Mod;
      offset += blockLen;
      remaining -= blockLen;
    }

    this._a = a;
    this._b = b;
  }

  private static void UpdateSimd(ReadOnlySpan<byte> block, ref uint a, ref uint b) {
    // Process 32 bytes per iteration.
    // For each group of 32 bytes at position p within the block:
    //   a_new = a_old + sum(bytes[p..p+31])
    //   b_new = b_old + 32*a_old + 32*byte[p] + 31*byte[p+1] + ... + 1*byte[p+31]
    //
    // We accumulate using vectors to avoid per-byte scalar overhead.
    int i = 0;
    int simdEnd = block.Length - 31;

    // Weight vector for b accumulation: byte[0] gets weight 32, byte[1] gets 31, etc.
    var weights = Vector256.Create(
      (short)32, 31, 30, 29, 28, 27, 26, 25,
      24, 23, 22, 21, 20, 19, 18, 17);
    var weights2 = Vector256.Create(
      (short)16, 15, 14, 13, 12, 11, 10, 9,
      8, 7, 6, 5, 4, 3, 2, 1);

    while (i < simdEnd) {
      var bytes = Vector256.Create<byte>(block.Slice(i));

      // Widen to 16-bit and accumulate sum for 'a'
      var (lo16, hi16) = Vector256.Widen(bytes);
      var sumVec16 = lo16 + hi16;
      // Horizontal sum of 16x ushort → total byte sum
      uint byteSum = Vector256.Sum(sumVec16);

      // Weighted sum for 'b': multiply each byte position by its weight
      var weightedLo = SumWidenedProducts(lo16, weights);
      var weightedHi = SumWidenedProducts(hi16, weights2);
      uint weightedSum = weightedLo + weightedHi;

      b += 32 * a + weightedSum;
      a += byteSum;

      i += 32;
    }

    // Scalar tail
    for (; i < block.Length; ++i) {
      a += block[i];
      b += a;
    }
  }

  private static uint SumWidenedProducts(Vector256<ushort> values, Vector256<short> weights) {
    // Multiply 16-bit values by weights, widen to 32-bit, and sum
    var valSigned = values.AsInt16();
    var (prodLo, prodHi) = Vector256.Widen(valSigned);
    var (wLo, wHi) = Vector256.Widen(weights);
    var result = prodLo * wLo + prodHi * wHi;
    return (uint)Vector256.Sum(result);
  }

  /// <summary>
  /// Computes the Adler-32 of the given data in a single call.
  /// </summary>
  /// <param name="data">The data to checksum.</param>
  /// <returns>The Adler-32 value.</returns>
  public static uint Compute(ReadOnlySpan<byte> data) {
    var adler = new Adler32();
    adler.Update(data);
    return adler.Value;
  }
}
