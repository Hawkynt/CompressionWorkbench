using Compression.Core.Checksums;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>COMB4P(MD4, MD5) hash combiner.</summary>
public static class Comb4PMd4Md5 {
  public static byte[] Compute(ReadOnlySpan<byte> data) => Comb4P.Compute(data, Md4.Compute, Md5.Compute);
}

/// <summary>COMB4P(SHA-1, RIPEMD-160) hash combiner.</summary>
public static class Comb4PSha1Ripemd160 {
  public static byte[] Compute(ReadOnlySpan<byte> data) => Comb4P.Compute(data, Sha1.Compute, Ripemd160.Compute);
}

internal static class Comb4P {
  public delegate byte[] Hash(ReadOnlySpan<byte> data);

  public static byte[] Compute(ReadOnlySpan<byte> data, Hash hash1, Hash hash2) {
    var initial = new byte[data.Length + 1];
    data.CopyTo(initial.AsSpan(1));
    var h1 = hash1(initial);
    var h2 = hash2(initial);
    if (h1.Length != h2.Length)
      throw new InvalidOperationException("COMB4P component hashes must have equal output sizes.");

    XorInto(h1, h2);
    Round(h2, h1, 1, hash1, hash2);
    Round(h1, h2, 2, hash1, hash2);

    var result = new byte[h1.Length + h2.Length];
    h1.CopyTo(result, 0);
    h2.CopyTo(result, h1.Length);
    return result;
  }

  private static void Round(Span<byte> output, ReadOnlySpan<byte> input, byte round, Hash hash1, Hash hash2) {
    var message = new byte[input.Length + 1];
    message[0] = round;
    input.CopyTo(message.AsSpan(1));
    var a = hash1(message);
    var b = hash2(message);
    for (var i = 0; i < output.Length; ++i)
      output[i] ^= (byte)(a[i] ^ b[i]);
  }

  private static void XorInto(Span<byte> target, ReadOnlySpan<byte> other) {
    for (var i = 0; i < target.Length; ++i)
      target[i] ^= other[i];
  }
}
