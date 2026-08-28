using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>SHA-384, SHA-512, SHA-512/224 and SHA-512/256 from FIPS 180-4.</summary>
public static class Sha512Family {
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    new(224, 256, 32),
    new(384, 512, 128)
  ];

  private static readonly ulong[] K = [
    0x428A2F98D728AE22UL, 0x7137449123EF65CDUL, 0xB5C0FBCFEC4D3B2FUL, 0xE9B5DBA58189DBBCUL,
    0x3956C25BF348B538UL, 0x59F111F1B605D019UL, 0x923F82A4AF194F9BUL, 0xAB1C5ED5DA6D8118UL,
    0xD807AA98A3030242UL, 0x12835B0145706FBEUL, 0x243185BE4EE4B28CUL, 0x550C7DC3D5FFB4E2UL,
    0x72BE5D74F27B896FUL, 0x80DEB1FE3B1696B1UL, 0x9BDC06A725C71235UL, 0xC19BF174CF692694UL,
    0xE49B69C19EF14AD2UL, 0xEFBE4786384F25E3UL, 0x0FC19DC68B8CD5B5UL, 0x240CA1CC77AC9C65UL,
    0x2DE92C6F592B0275UL, 0x4A7484AA6EA6E483UL, 0x5CB0A9DCBD41FBD4UL, 0x76F988DA831153B5UL,
    0x983E5152EE66DFABUL, 0xA831C66D2DB43210UL, 0xB00327C898FB213FUL, 0xBF597FC7BEEF0EE4UL,
    0xC6E00BF33DA88FC2UL, 0xD5A79147930AA725UL, 0x06CA6351E003826FUL, 0x142929670A0E6E70UL,
    0x27B70A8546D22FFCUL, 0x2E1B21385C26C926UL, 0x4D2C6DFC5AC42AEDUL, 0x53380D139D95B3DFUL,
    0x650A73548BAF63DEUL, 0x766A0ABB3C77B2A8UL, 0x81C2C92E47EDAEE6UL, 0x92722C851482353BUL,
    0xA2BFE8A14CF10364UL, 0xA81A664BBC423001UL, 0xC24B8B70D0F89791UL, 0xC76C51A30654BE30UL,
    0xD192E819D6EF5218UL, 0xD69906245565A910UL, 0xF40E35855771202AUL, 0x106AA07032BBD1B8UL,
    0x19A4C116B8D2D0C8UL, 0x1E376C085141AB53UL, 0x2748774CDF8EEB99UL, 0x34B0BCB5E19B48A8UL,
    0x391C0CB3C5C95A63UL, 0x4ED8AA4AE3418ACBUL, 0x5B9CCA4F7763E373UL, 0x682E6FF3D6B2B8A3UL,
    0x748F82EE5DEFB2FCUL, 0x78A5636F43172F60UL, 0x84C87814A1F0AB72UL, 0x8CC702081A6439ECUL,
    0x90BEFFFA23631E28UL, 0xA4506CEBDE82BDE9UL, 0xBEF9A3F7B2C67915UL, 0xC67178F2E372532BUL,
    0xCA273ECEEA26619CUL, 0xD186B8C721C0C207UL, 0xEADA7DD6CDE0EB1EUL, 0xF57D4F7FEE6ED178UL,
    0x06F067AA72176FBAUL, 0x0A637DC5A2C898A6UL, 0x113F9804BEF90DAEUL, 0x1B710B35131C471BUL,
    0x28DB77F523047D84UL, 0x32CAAB7B40C72493UL, 0x3C9EBE0A15C9BEBCUL, 0x431D67C49C100D4CUL,
    0x4CC5D4BECD3E42B6UL, 0x597F299CFC657E2AUL, 0x5FCB6FAB3AD6FAECUL, 0x6C44198C4A475817UL
  ];

  private static readonly ulong[] Sha512Initial = [
    0x6A09E667F3BCC908UL, 0xBB67AE8584CAA73BUL, 0x3C6EF372FE94F82BUL, 0xA54FF53A5F1D36F1UL,
    0x510E527FADE682D1UL, 0x9B05688C2B3E6C1FUL, 0x1F83D9ABFB41BD6BUL, 0x5BE0CD19137E2179UL
  ];

  private static readonly ulong[] Sha384Initial = [
    0xCBBB9D5DC1059ED8UL, 0x629A292A367CD507UL, 0x9159015A3070DD17UL, 0x152FECD8F70E5939UL,
    0x67332667FFC00B31UL, 0x8EB44A8768581511UL, 0xDB0C2E0D64F98FA7UL, 0x47B5481DBEFA4FA4UL
  ];

  private static readonly ulong[] Sha512_224Initial = [
    0x8C3D37C819544DA2UL, 0x73E1996689DCD4D6UL, 0x1DFAB7AE32FF9C82UL, 0x679DD514582F9FCFUL,
    0x0F6D2B697BD44DA8UL, 0x77E36F7304C48942UL, 0x3F9D85A86A1D36C8UL, 0x1112E6AD91D692A1UL
  ];

  private static readonly ulong[] Sha512_256Initial = [
    0x22312194FC2BF72CUL, 0x9F555FA3C84C64C2UL, 0x2393B86B6F53B151UL, 0x963877195940EABDUL,
    0x96283EE2A88EFFE3UL, 0xBE5E1E2553863992UL, 0x2B0199FC2C85B8AAUL, 0x0EB72DDC81C52CA2UL
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 512) => hashSizeBits switch {
    224 => ComputeCore(data, Sha512_224Initial, 28),
    256 => ComputeCore(data, Sha512_256Initial, 32),
    384 => ComputeCore(data, Sha384Initial, 48),
    512 => ComputeCore(data, Sha512Initial, 64),
    _ => throw new ArgumentOutOfRangeException(nameof(hashSizeBits))
  };

  public static byte[] Compute512(ReadOnlySpan<byte> data) => Compute(data, 512);
  public static byte[] Compute384(ReadOnlySpan<byte> data) => Compute(data, 384);
  public static byte[] Compute512_224(ReadOnlySpan<byte> data) => Compute(data, 224);
  public static byte[] Compute512_256(ReadOnlySpan<byte> data) => Compute(data, 256);

  private static byte[] ComputeCore(ReadOnlySpan<byte> data, ReadOnlySpan<ulong> initial, int outputBytes) {
    var state = initial.ToArray();

    var paddingBytes = 1 + 16;
    var paddedLength = checked(((data.Length + paddingBytes + 127) / 128) * 128);
    var buffer = new byte[paddedLength];
    data.CopyTo(buffer);
    buffer[data.Length] = 0x80;

    var bitLength = (ulong)data.Length * 8UL;
    BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(paddedLength - 16), 0);
    BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(paddedLength - 8), bitLength);

    Span<ulong> schedule = stackalloc ulong[80];
    for (var offset = 0; offset < buffer.Length; offset += 128) {
      var block = buffer.AsSpan(offset, 128);
      for (var i = 0; i < 16; ++i)
        schedule[i] = BinaryPrimitives.ReadUInt64BigEndian(block[(i * 8)..]);
      for (var i = 16; i < 80; ++i)
        schedule[i] = unchecked(SmallSigma1(schedule[i - 2]) + schedule[i - 7] + SmallSigma0(schedule[i - 15]) + schedule[i - 16]);

      var a = state[0];
      var b = state[1];
      var c = state[2];
      var d = state[3];
      var e = state[4];
      var f = state[5];
      var g = state[6];
      var h = state[7];

      for (var i = 0; i < 80; ++i) {
        var t1 = unchecked(h + BigSigma1(e) + Ch(e, f, g) + K[i] + schedule[i]);
        var t2 = unchecked(BigSigma0(a) + Maj(a, b, c));
        h = g;
        g = f;
        f = e;
        e = unchecked(d + t1);
        d = c;
        c = b;
        b = a;
        a = unchecked(t1 + t2);
      }

      state[0] = unchecked(state[0] + a);
      state[1] = unchecked(state[1] + b);
      state[2] = unchecked(state[2] + c);
      state[3] = unchecked(state[3] + d);
      state[4] = unchecked(state[4] + e);
      state[5] = unchecked(state[5] + f);
      state[6] = unchecked(state[6] + g);
      state[7] = unchecked(state[7] + h);
    }

    var full = new byte[64];
    for (var i = 0; i < 8; ++i)
      BinaryPrimitives.WriteUInt64BigEndian(full.AsSpan(i * 8), state[i]);
    return full[..outputBytes];
  }

  private static ulong Ch(ulong x, ulong y, ulong z) => (x & y) ^ (~x & z);
  private static ulong Maj(ulong x, ulong y, ulong z) => (x & y) ^ (x & z) ^ (y & z);
  private static ulong BigSigma0(ulong x) => BitOperations.RotateRight(x, 28) ^ BitOperations.RotateRight(x, 34) ^ BitOperations.RotateRight(x, 39);
  private static ulong BigSigma1(ulong x) => BitOperations.RotateRight(x, 14) ^ BitOperations.RotateRight(x, 18) ^ BitOperations.RotateRight(x, 41);
  private static ulong SmallSigma0(ulong x) => BitOperations.RotateRight(x, 1) ^ BitOperations.RotateRight(x, 8) ^ (x >> 7);
  private static ulong SmallSigma1(ulong x) => BitOperations.RotateRight(x, 19) ^ BitOperations.RotateRight(x, 61) ^ (x >> 6);
}
