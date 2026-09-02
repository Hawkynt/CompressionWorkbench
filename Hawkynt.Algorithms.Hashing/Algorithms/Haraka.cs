namespace Hawkynt.Algorithms.Hashing;

/// <summary>Haraka v2 256-bit fixed-input hash.</summary>
public static class Haraka256 {
  /// <summary>
  /// Computes the Haraka-256 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    if (data.Length != 32)
      throw new ArgumentException("Haraka-256 requires exactly 32 input bytes.", nameof(data));

    var s1 = new[] { data[..16].ToArray(), data[16..].ToArray() };
    var s2 = new[] { new byte[16], new byte[16] };
    var rc = 0;

    for (var round = 0; round < 5; ++round) {
      s1[0] = HarakaCore.AesRound(s1[0], HarakaCore.RoundConstants[rc++]);
      s1[1] = HarakaCore.AesRound(s1[1], HarakaCore.RoundConstants[rc++]);
      s1[0] = HarakaCore.AesRound(s1[0], HarakaCore.RoundConstants[rc++]);
      s1[1] = HarakaCore.AesRound(s1[1], HarakaCore.RoundConstants[rc++]);
      Mix(s1, s2);
      s1[0] = s2[0].ToArray();
      s1[1] = s2[1].ToArray();
    }

    var result = new byte[32];
    for (var i = 0; i < 16; ++i) {
      result[i] = (byte)(s2[0][i] ^ data[i]);
      result[i + 16] = (byte)(s2[1][i] ^ data[i + 16]);
    }
    return result;
  }

  private static void Mix(byte[][] source, byte[][] target) {
    for (var i = 0; i < 4; ++i) {
      target[0][i] = source[0][i];
      target[0][i + 4] = source[1][i];
      target[0][i + 8] = source[0][i + 4];
      target[0][i + 12] = source[1][i + 4];

      target[1][i] = source[0][i + 8];
      target[1][i + 4] = source[1][i + 8];
      target[1][i + 8] = source[0][i + 12];
      target[1][i + 12] = source[1][i + 12];
    }
  }
}

/// <summary>Haraka v2 512-to-256 fixed-input hash.</summary>
public static class Haraka512 {
  /// <summary>
  /// Computes the Haraka-512 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    if (data.Length != 64)
      throw new ArgumentException("Haraka-512 requires exactly 64 input bytes.", nameof(data));

    var s1 = new[] {
      data[..16].ToArray(), data[16..32].ToArray(), data[32..48].ToArray(), data[48..].ToArray()
    };
    var s2 = new[] { new byte[16], new byte[16], new byte[16], new byte[16] };
    var rc = 0;

    for (var round = 0; round < 5; ++round) {
      for (var i = 0; i < 4; ++i)
        s1[i] = HarakaCore.AesRound(s1[i], HarakaCore.RoundConstants[rc++]);
      for (var i = 0; i < 4; ++i)
        s1[i] = HarakaCore.AesRound(s1[i], HarakaCore.RoundConstants[rc++]);

      Mix(s1, s2);
      for (var i = 0; i < 4; ++i)
        s1[i] = s2[i].ToArray();
    }

    for (var block = 0; block < 4; ++block)
      for (var i = 0; i < 16; ++i)
        s1[block][i] = (byte)(s2[block][i] ^ data[block * 16 + i]);

    var result = new byte[32];
    s1[0].AsSpan(8, 8).CopyTo(result);
    s1[1].AsSpan(8, 8).CopyTo(result.AsSpan(8));
    s1[2].AsSpan(0, 8).CopyTo(result.AsSpan(16));
    s1[3].AsSpan(0, 8).CopyTo(result.AsSpan(24));
    return result;
  }

  private static void Mix(byte[][] source, byte[][] target) {
    Copy4(source[0], 12, target[0], 0); Copy4(source[2], 12, target[0], 4);
    Copy4(source[1], 12, target[0], 8); Copy4(source[3], 12, target[0], 12);

    Copy4(source[2], 0, target[1], 0); Copy4(source[0], 0, target[1], 4);
    Copy4(source[3], 0, target[1], 8); Copy4(source[1], 0, target[1], 12);

    Copy4(source[2], 4, target[2], 0); Copy4(source[0], 4, target[2], 4);
    Copy4(source[3], 4, target[2], 8); Copy4(source[1], 4, target[2], 12);

    Copy4(source[0], 8, target[3], 0); Copy4(source[2], 8, target[3], 4);
    Copy4(source[1], 8, target[3], 8); Copy4(source[3], 8, target[3], 12);
  }

  private static void Copy4(byte[] source, int sourceOffset, byte[] target, int targetOffset) =>
    source.AsSpan(sourceOffset, 4).CopyTo(target.AsSpan(targetOffset, 4));
}

internal static class HarakaCore {
  internal static readonly byte[][] RoundConstants = [
    Hex("9D7B8175F0FEC5B20AC020E64C708406"), Hex("17F7082FA46B0F646BA0F388E1B4668B"),
    Hex("1491029F609D02CF9884F2532DDE0234"), Hex("794F5BFDAFBCF3BB084F7B2EE6EAD60E"),
    Hex("447039BE1CCDEE798B447248CBB0CFCB"), Hex("7B058A2BED35538DB732906EEECDEA7E"),
    Hex("1BEF4FDA612741E2D07C2E5E438FC267"), Hex("3B0BC71FE2FD5F6707CCCAAFB0D92429"),
    Hex("EE65D4B9CA8FDBECE97F86E6F1634DAB"), Hex("337E03AD4F402A5B64CDB7D484BF301C"),
    Hex("0098F68D2E8B0269BF231794B90BCCB2"), Hex("8A2D9D5CC89EAA4A72556FDEA67804FA"),
    Hex("D49F12292E4FFA0E122A776B2B9FB4DF"), Hex("EE126ABBAE11D63236A249F44403A11E"),
    Hex("A6ECA89CC900965F8400054B884904AF"), Hex("EC93E527E3C7A2784F9C199DD85E0221"),
    Hex("7301D482CD2E28B9B7C959A7F8AA3ABF"), Hex("6B7D3010D9EFF23717B086610D706062"),
    Hex("C69AFCF65391C28143043021C245CA5A"), Hex("3A94D136E892AF2CBB686B223C972392"),
    Hex("B47110E558B9BA6CEB8658223892BFD3"), Hex("8D12E124DDFD3D9377C6F0AEE53C86DB"),
    Hex("B11222CBE38DE4839CA0EBFF686260BB"), Hex("7DF72BC74E1AB92D9CD1E4E2DCD34B73"),
    Hex("4E92B32CC415144B431B3061C347BB43"), Hex("9968EB16DD31B203F6EF07E7A875A7DB"),
    Hex("2C47CA7E02235E8E7759753C4B61F36D"), Hex("F91786B8B9E51B6D777DDED6175AA7CD"),
    Hex("5DEE46A99D066C9DAAE9A86BF0436BEC"), Hex("C127F33B591153A22B3357F950691ECB"),
    Hex("D9D00E605303EDE49C61DA00750CEE2C"), Hex("50A3A463BCBABB80AB0CE996A1A5B1F0"),
    Hex("39CA8D9330DE0DAB8829965E02B13DAE"), Hex("42B4752EA8F314880BA454D5388FBB17"),
    Hex("F6160A3679B7B6AED77F425F5B8ABB34"), Hex("DEAFBAFF1859CE433854E5CB4152F626"),
    Hex("78C99E83F79CCAA26A02F3B9549AE94C"), Hex("35129022286EC040BEF7DF1B1AA551AE"),
    Hex("CF59A6480FBC73C12BD27EBA3C61C1A0"), Hex("A19DC5E9FDBDD64A8882280203CC6A75")
  ];

  internal static byte[] AesRound(ReadOnlySpan<byte> state, ReadOnlySpan<byte> roundKey) {
    Span<byte> shifted = stackalloc byte[16];
    shifted[0] = SBox(state[0]); shifted[1] = SBox(state[5]); shifted[2] = SBox(state[10]); shifted[3] = SBox(state[15]);
    shifted[4] = SBox(state[4]); shifted[5] = SBox(state[9]); shifted[6] = SBox(state[14]); shifted[7] = SBox(state[3]);
    shifted[8] = SBox(state[8]); shifted[9] = SBox(state[13]); shifted[10] = SBox(state[2]); shifted[11] = SBox(state[7]);
    shifted[12] = SBox(state[12]); shifted[13] = SBox(state[1]); shifted[14] = SBox(state[6]); shifted[15] = SBox(state[11]);

    var result = new byte[16];
    for (var column = 0; column < 4; ++column) {
      var i = column * 4;
      var a = shifted[i]; var b = shifted[i + 1]; var c = shifted[i + 2]; var d = shifted[i + 3];
      result[i] = (byte)(MulX(a) ^ MulX(b) ^ b ^ c ^ d);
      result[i + 1] = (byte)(a ^ MulX(b) ^ MulX(c) ^ c ^ d);
      result[i + 2] = (byte)(a ^ b ^ MulX(c) ^ MulX(d) ^ d);
      result[i + 3] = (byte)(MulX(a) ^ a ^ b ^ c ^ MulX(d));
    }

    for (var i = 0; i < 16; ++i)
      result[i] ^= roundKey[i];
    return result;
  }

  private static byte SBox(byte value) {
    if (value == 0)
      return 0x63;

    var inverse = Pow(value, 254);
    var transformed = inverse
      ^ RotateByte(inverse, 1)
      ^ RotateByte(inverse, 2)
      ^ RotateByte(inverse, 3)
      ^ RotateByte(inverse, 4)
      ^ 0x63;
    return (byte)transformed;
  }

  private static byte Pow(byte value, int exponent) {
    byte result = 1;
    var factor = value;
    while (exponent != 0) {
      if ((exponent & 1) != 0)
        result = Multiply(result, factor);
      factor = Multiply(factor, factor);
      exponent >>= 1;
    }
    return result;
  }

  private static byte Multiply(byte left, byte right) {
    var a = left;
    var b = right;
    byte result = 0;
    for (var i = 0; i < 8; ++i) {
      if ((b & 1) != 0)
        result ^= a;
      var high = (a & 0x80) != 0;
      a <<= 1;
      if (high)
        a ^= 0x1B;
      b >>= 1;
    }
    return result;
  }

  private static int RotateByte(byte value, int bits) => ((value << bits) | (value >> (8 - bits))) & 0xFF;
  private static byte MulX(byte value) => (byte)(((value & 0x7F) << 1) ^ ((value >> 7) * 0x1B));
  private static byte[] Hex(string value) => Convert.FromHexString(value);
}
