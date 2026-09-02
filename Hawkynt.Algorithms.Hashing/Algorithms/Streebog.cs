using System.Buffers.Binary;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Streebog-256 (GOST R 34.11-2012) hash function.</summary>
public static class Streebog256 {
  /// <summary>
  /// Computes the Streebog-256 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => StreebogCore.Compute(data, 32);
}

/// <summary>Streebog-512 (GOST R 34.11-2012) hash function.</summary>
public static class Streebog512 {
  /// <summary>
  /// Gets the supported hash-output sizes, in bits.
  /// </summary>
  public static global::System.Collections.Generic.IReadOnlyList<global::Hawkynt.Algorithms.Hashing.HashSizeRange> SupportedHashSizes => global::Hawkynt.Algorithms.Hashing.HashSizeSets.Bits512;

  /// <summary>
  /// Computes the Streebog-512 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => StreebogCore.Compute(data, 64);
}

internal static class StreebogCore {
  private static readonly byte[] Pi = [
    0xFC,0xEE,0xDD,0x11,0xCF,0x6E,0x31,0x16,0xFB,0xC4,0xFA,0xDA,0x23,0xC5,0x04,0x4D,
    0xE9,0x77,0xF0,0xDB,0x93,0x2E,0x99,0xBA,0x17,0x36,0xF1,0xBB,0x14,0xCD,0x5F,0xC1,
    0xF9,0x18,0x65,0x5A,0xE2,0x5C,0xEF,0x21,0x81,0x1C,0x3C,0x42,0x8B,0x01,0x8E,0x4F,
    0x05,0x84,0x02,0xAE,0xE3,0x6A,0x8F,0xA0,0x06,0x0B,0xED,0x98,0x7F,0xD4,0xD3,0x1F,
    0xEB,0x34,0x2C,0x51,0xEA,0xC8,0x48,0xAB,0xF2,0x2A,0x68,0xA2,0xFD,0x3A,0xCE,0xCC,
    0xB5,0x70,0x0E,0x56,0x08,0x0C,0x76,0x12,0xBF,0x72,0x13,0x47,0x9C,0xB7,0x5D,0x87,
    0x15,0xA1,0x96,0x29,0x10,0x7B,0x9A,0xC7,0xF3,0x91,0x78,0x6F,0x9D,0x9E,0xB2,0xB1,
    0x32,0x75,0x19,0x3D,0xFF,0x35,0x8A,0x7E,0x6D,0x54,0xC6,0x80,0xC3,0xBD,0x0D,0x57,
    0xDF,0xF5,0x24,0xA9,0x3E,0xA8,0x43,0xC9,0xD7,0x79,0xD6,0xF6,0x7C,0x22,0xB9,0x03,
    0xE0,0x0F,0xEC,0xDE,0x7A,0x94,0xB0,0xBC,0xDC,0xE8,0x28,0x50,0x4E,0x33,0x0A,0x4A,
    0xA7,0x97,0x60,0x73,0x1E,0x00,0x62,0x44,0x1A,0xB8,0x38,0x82,0x64,0x9F,0x26,0x41,
    0xAD,0x45,0x46,0x92,0x27,0x5E,0x55,0x2F,0x8C,0xA3,0xA5,0x7D,0x69,0xD5,0x95,0x3B,
    0x07,0x58,0xB3,0x40,0x86,0xAC,0x1D,0xF7,0x30,0x37,0x6B,0xE4,0x88,0xD9,0xE7,0x89,
    0xE1,0x1B,0x83,0x49,0x4C,0x3F,0xF8,0xFE,0x8D,0x53,0xAA,0x90,0xCA,0xD8,0x85,0x61,
    0x20,0x71,0x67,0xA4,0x2D,0x2B,0x09,0x5B,0xCB,0x9B,0x25,0xD0,0xBE,0xE5,0x6C,0x52,
    0x59,0xA6,0x74,0xD2,0xE6,0xF4,0xB4,0xC0,0xD1,0x66,0xAF,0xC2,0x39,0x4B,0x63,0xB6
  ];

  private static readonly ulong[] A = [
    0x8E20FAA72BA0B470UL,0x47107DDD9B505A38UL,0xAD08B0E0C3282D1CUL,0xD8045870EF14980EUL,
    0x6C022C38F90A4C07UL,0x3601161CF205268DUL,0x1B8E0B0E798C13C8UL,0x83478B07B2468764UL,
    0xA011D380818E8F40UL,0x5086E740CE47C920UL,0x2843FD2067ADEA10UL,0x14AFF010BDD87508UL,
    0x0AD97808D06CB404UL,0x05E23C0468365A02UL,0x8C711E02341B2D01UL,0x46B60F011A83988EUL,
    0x90DAB52A387AE76FUL,0x486DD4151C3DFDB9UL,0x24B86A840E90F0D2UL,0x125C354207487869UL,
    0x092E94218D243CBAUL,0x8A174A9EC8121E5DUL,0x4585254F64090FA0UL,0xACCC9CA9328A8950UL,
    0x9D4DF05D5F661451UL,0xC0A878A0A1330AA6UL,0x60543C50DE970553UL,0x302A1E286FC58CA7UL,
    0x18150F14B9EC46DDUL,0x0C84890AD27623E0UL,0x0642CA05693B9F70UL,0x0321658CBA93C138UL,
    0x86275DF09CE8AAA8UL,0x439DA0784E745554UL,0xAFC0503C273AA42AUL,0xD960281E9D1D5215UL,
    0xE230140FC0802984UL,0x71180A8960409A42UL,0xB60C05CA30204D21UL,0x5B068C651810A89EUL,
    0x456C34887A3805B9UL,0xAC361A443D1C8CD2UL,0x561B0D22900E4669UL,0x2B838811480723BAUL,
    0x9BCF4486248D9F5DUL,0xC3E9224312C8C1A0UL,0xEFFA11AF0964EE50UL,0xF97D86D98A327728UL,
    0xE4FA2054A80B329CUL,0x727D102A548B194EUL,0x39B008152ACB8227UL,0x9258048415EB419DUL,
    0x492C024284FBAEC0UL,0xAA16012142F35760UL,0x550B8E9E21F7A530UL,0xA48B474F9EF5DC18UL,
    0x70A6A56E2440598EUL,0x3853DC371220A247UL,0x1CA76E95091051ADUL,0x0EDD37C48A08A6D8UL,
    0x07E095624504536CUL,0x8D70C431AC02A736UL,0xC83862965601DD1BUL,0x641C314B2B8EE083UL
  ];

  private static readonly string[] RoundConstantHex = [
    "b1085bda1ecadae9ebcb2f81c0657c1f2f6a76432e45d016714eb88d7585c4fc4b7ce09192676901a2422a08a460d31505767436cc744d23dd806559f2a64507",
    "6fa3b58aa99d2f1a4fe39d460f70b5d7f3feea720a232b9861d55e0f16b501319ab5176b12d699585cb561c2db0aa7ca55dda21bd7cbcd56e679047021b19bb7",
    "f574dcac2bce2fc70a39fc286a3d843506f15e5f529c1f8bf2ea7514b1297b7bd3e20fe490359eb1c1c93a376062db09c2b6f443867adb31991e96f50aba0ab2",
    "ef1fdfb3e81566d2f948e1a05d71e4dd488e857e335c3c7d9d721cad685e353fa9d72c82ed03d675d8b71333935203be3453eaa193e837f1220cbebc84e3d12e",
    "4bea6bacad4747999a3f410c6ca923637f151c1f1686104a359e35d7800fffbdbfcd1747253af5a3dfff00b723271a167a56a27ea9ea63f5601758fd7c6cfe57",
    "ae4faeae1d3ad3d96fa4c33b7a3039c02d66c4f95142a46c187f9ab49af08ec6cffaa6b71c9ab7b40af21f66c2bec6b6bf71c57236904f35fa68407a46647d6e",
    "f4c70e16eeaac5ec51ac86febf240954399ec6c7e6bf87c9d3473e33197a93c90992abc52d822c3706476983284a05043517454ca23c4af38886564d3a14d493",
    "9b1f5b424d93c9a703e7aa020c6e41414eb7f8719c36de1e89b4443b4ddbc49af4892bcb929b069069d18d2bd1a5c42f36acc2355951a8d9a47f0dd4bf02e71e",
    "378f5a541631229b944c9ad8ec165fde3a7d3a1b258942243cd955b7e00d0984800a440bdbb2ceb17b2b8a9aa6079c540e38dc92cb1f2a607261445183235adb",
    "abbedea680056f52382ae548b2e4f3f38941e71cff8a78db1fffe18a1b3361039fe76702af69334b7a1e6c303b7652f43698fad1153bb6c374b4c7fb98459ced",
    "7bcd9ed0efc889fb3002c6cd635afe94d8fa6bbbebab076120018021148466798a1d71efea48b9caefbacd1d7d476e98dea2594ac06fd85d6bcaa4cd81f32d1b",
    "378ee767f11631bad21380b00449b17acda43c32bcdf1d77f82012d430219f9b5d80ef9d1891cc86e71da4aa88e12852faf417d5d9b21b9948bc924af11bd720"
  ];

  private static readonly ulong[][] Tables = CreateTables();
  private static readonly ulong[][] RoundConstants = CreateRoundConstants();

  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes) {
    if (outputBytes is not (32 or 64))
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    var initial = outputBytes == 32 ? 0x0101010101010101UL : 0UL;
    var h = new ulong[8];
    if (initial != 0)
      Array.Fill(h, initial);
    var n = new ulong[8];
    var sigma = new ulong[8];

    var offset = 0;
    while (offset + 64 <= data.Length) {
      ProcessBlock(h, n, sigma, data.Slice(offset, 64));
      offset += 64;
    }

    var remaining = data.Length - offset;
    Span<byte> finalBlock = stackalloc byte[64];
    finalBlock.Clear();
    data[offset..].CopyTo(finalBlock);
    finalBlock[remaining] = 0x01;

    Span<ulong> padded = stackalloc ulong[8];
    ReadWords(finalBlock, padded);
    Compress(h, n, padded);
    AddBits(n, remaining * 8);
    AddBlock(sigma, padded);

    Span<ulong> zero = stackalloc ulong[8];
    zero.Clear();
    Compress(h, zero, n);
    Compress(h, zero, sigma);

    var firstWord = outputBytes == 32 ? 4 : 0;
    var result = new byte[outputBytes];
    for (var i = firstWord; i < 8; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan((i - firstWord) * 8, 8), h[i]);
    return result;
  }

  private static void ProcessBlock(ulong[] h, ulong[] n, ulong[] sigma, ReadOnlySpan<byte> block) {
    Span<ulong> message = stackalloc ulong[8];
    ReadWords(block, message);
    Compress(h, n, message);
    AddBits(n, 512);
    AddBlock(sigma, message);
  }

  private static void ReadWords(ReadOnlySpan<byte> bytes, Span<ulong> words) {
    for (var i = 0; i < 8; ++i)
      words[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i * 8, 8));
  }

  private static void Compress(ulong[] h, ReadOnlySpan<ulong> n, ReadOnlySpan<ulong> message) {
    Span<ulong> key = stackalloc ulong[8];
    for (var i = 0; i < 8; ++i)
      key[i] = h[i] ^ n[i];
    ApplyLps(key);

    Span<ulong> encrypted = stackalloc ulong[8];
    Encrypt(key, message, encrypted);
    for (var i = 0; i < 8; ++i)
      h[i] ^= encrypted[i] ^ message[i];
  }

  private static void Encrypt(ReadOnlySpan<ulong> keyInput, ReadOnlySpan<ulong> message, Span<ulong> result) {
    Span<ulong> state = stackalloc ulong[8];
    Span<ulong> key = stackalloc ulong[8];
    message.CopyTo(state);
    keyInput.CopyTo(key);

    for (var round = 0; round < 12; ++round) {
      for (var i = 0; i < 8; ++i)
        state[i] ^= key[i];
      ApplyLps(state);

      for (var i = 0; i < 8; ++i)
        key[i] ^= RoundConstants[round][i];
      ApplyLps(key);
    }

    for (var i = 0; i < 8; ++i)
      result[i] = state[i] ^ key[i];
  }

  private static void ApplyLps(Span<ulong> data) {
    Span<ulong> result = stackalloc ulong[8];
    for (var byteIndex = 0; byteIndex < 8; ++byteIndex) {
      result[byteIndex] =
        Tables[7][(byte)(data[0] >> (byteIndex * 8))] ^
        Tables[6][(byte)(data[1] >> (byteIndex * 8))] ^
        Tables[5][(byte)(data[2] >> (byteIndex * 8))] ^
        Tables[4][(byte)(data[3] >> (byteIndex * 8))] ^
        Tables[3][(byte)(data[4] >> (byteIndex * 8))] ^
        Tables[2][(byte)(data[5] >> (byteIndex * 8))] ^
        Tables[1][(byte)(data[6] >> (byteIndex * 8))] ^
        Tables[0][(byte)(data[7] >> (byteIndex * 8))];
    }
    result.CopyTo(data);
  }

  private static void AddBits(Span<ulong> value, int bits) {
    unchecked {
      var sum = value[0] + (ulong)bits;
      var carry = sum < value[0] ? 1UL : 0UL;
      value[0] = sum;
      for (var i = 1; carry != 0 && i < 8; ++i) {
        sum = value[i] + carry;
        carry = sum < value[i] ? 1UL : 0UL;
        value[i] = sum;
      }
    }
  }

  private static void AddBlock(Span<ulong> destination, ReadOnlySpan<ulong> source) {
    unchecked {
      ulong carry = 0;
      for (var i = 0; i < 8; ++i) {
        var before = destination[i];
        var sum = before + source[i];
        var carry1 = sum < before ? 1UL : 0UL;
        var sumWithCarry = sum + carry;
        var carry2 = sumWithCarry < sum ? 1UL : 0UL;
        destination[i] = sumWithCarry;
        carry = carry1 | carry2;
      }
    }
  }

  private static ulong[][] CreateTables() {
    var tables = new ulong[8][];
    for (var row = 0; row < 8; ++row)
      tables[row] = new ulong[256];

    for (var input = 0; input < 256; ++input) {
      var substituted = Pi[input];
      for (var row = 0; row < 8; ++row) {
        ulong contribution = 0;
        for (var bit = 0; bit < 8; ++bit)
          if ((substituted & (1 << bit)) != 0)
            contribution ^= A[row * 8 + 7 - bit];
        tables[row][input] = contribution;
      }
    }
    return tables;
  }

  private static ulong[][] CreateRoundConstants() {
    var result = new ulong[12][];
    for (var round = 0; round < result.Length; ++round) {
      result[round] = new ulong[8];
      var hex = RoundConstantHex[round];
      for (var word = 0; word < 8; ++word)
        result[round][word] = Convert.ToUInt64(hex.Substring((7 - word) * 16, 16), 16);
    }
    return result;
  }
}