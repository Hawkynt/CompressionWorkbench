using System.Buffers.Binary;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Minimal AES-128 encryption primitive used by hash constructions such as CHC and Haraka.</summary>
internal static class Aes128Primitive {
  private static readonly byte[] SBox = [
    0x63,0x7C,0x77,0x7B,0xF2,0x6B,0x6F,0xC5,0x30,0x01,0x67,0x2B,0xFE,0xD7,0xAB,0x76,
    0xCA,0x82,0xC9,0x7D,0xFA,0x59,0x47,0xF0,0xAD,0xD4,0xA2,0xAF,0x9C,0xA4,0x72,0xC0,
    0xB7,0xFD,0x93,0x26,0x36,0x3F,0xF7,0xCC,0x34,0xA5,0xE5,0xF1,0x71,0xD8,0x31,0x15,
    0x04,0xC7,0x23,0xC3,0x18,0x96,0x05,0x9A,0x07,0x12,0x80,0xE2,0xEB,0x27,0xB2,0x75,
    0x09,0x83,0x2C,0x1A,0x1B,0x6E,0x5A,0xA0,0x52,0x3B,0xD6,0xB3,0x29,0xE3,0x2F,0x84,
    0x53,0xD1,0x00,0xED,0x20,0xFC,0xB1,0x5B,0x6A,0xCB,0xBE,0x39,0x4A,0x4C,0x58,0xCF,
    0xD0,0xEF,0xAA,0xFB,0x43,0x4D,0x33,0x85,0x45,0xF9,0x02,0x7F,0x50,0x3C,0x9F,0xA8,
    0x51,0xA3,0x40,0x8F,0x92,0x9D,0x38,0xF5,0xBC,0xB6,0xDA,0x21,0x10,0xFF,0xF3,0xD2,
    0xCD,0x0C,0x13,0xEC,0x5F,0x97,0x44,0x17,0xC4,0xA7,0x7E,0x3D,0x64,0x5D,0x19,0x73,
    0x60,0x81,0x4F,0xDC,0x22,0x2A,0x90,0x88,0x46,0xEE,0xB8,0x14,0xDE,0x5E,0x0B,0xDB,
    0xE0,0x32,0x3A,0x0A,0x49,0x06,0x24,0x5C,0xC2,0xD3,0xAC,0x62,0x91,0x95,0xE4,0x79,
    0xE7,0xC8,0x37,0x6D,0x8D,0xD5,0x4E,0xA9,0x6C,0x56,0xF4,0xEA,0x65,0x7A,0xAE,0x08,
    0xBA,0x78,0x25,0x2E,0x1C,0xA6,0xB4,0xC6,0xE8,0xDD,0x74,0x1F,0x4B,0xBD,0x8B,0x8A,
    0x70,0x3E,0xB5,0x66,0x48,0x03,0xF6,0x0E,0x61,0x35,0x57,0xB9,0x86,0xC1,0x1D,0x9E,
    0xE1,0xF8,0x98,0x11,0x69,0xD9,0x8E,0x94,0x9B,0x1E,0x87,0xE9,0xCE,0x55,0x28,0xDF,
    0x8C,0xA1,0x89,0x0D,0xBF,0xE6,0x42,0x68,0x41,0x99,0x2D,0x0F,0xB0,0x54,0xBB,0x16
  ];

  private static readonly byte[] Rcon = [0x01,0x02,0x04,0x08,0x10,0x20,0x40,0x80,0x1B,0x36];

  public static byte[] EncryptBlock(ReadOnlySpan<byte> block, ReadOnlySpan<byte> key) {
    if (block.Length != 16)
      throw new ArgumentException("AES block must be exactly 16 bytes.", nameof(block));
    if (key.Length != 16)
      throw new ArgumentException("AES-128 key must be exactly 16 bytes.", nameof(key));

    var state = block.ToArray();
    var expandedKey = ExpandKey(key);
    AddRoundKey(state, expandedKey, 0);
    for (var round = 1; round < 10; ++round) {
      SubBytes(state);
      ShiftRows(state);
      MixColumns(state);
      AddRoundKey(state, expandedKey, round * 16);
    }
    SubBytes(state);
    ShiftRows(state);
    AddRoundKey(state, expandedKey, 160);
    return state;
  }

  /// <summary>Applies one AES encryption round (SubBytes, ShiftRows, MixColumns, AddRoundKey).</summary>
  internal static void Round(Span<byte> state, ReadOnlySpan<byte> roundKey) {
    if (state.Length != 16 || roundKey.Length != 16)
      throw new ArgumentException("AES round state and key must be exactly 16 bytes.");
    SubBytes(state);
    ShiftRows(state);
    MixColumns(state);
    for (var i = 0; i < 16; ++i)
      state[i] ^= roundKey[i];
  }

  private static byte[] ExpandKey(ReadOnlySpan<byte> key) {
    var expanded = new byte[176];
    key.CopyTo(expanded);
    Span<byte> temp = stackalloc byte[4];
    var generated = 16;
    var rcon = 0;
    while (generated < expanded.Length) {
      expanded.AsSpan(generated - 4, 4).CopyTo(temp);
      if ((generated & 15) == 0) {
        var first = temp[0];
        temp[0] = SBox[temp[1]];
        temp[1] = SBox[temp[2]];
        temp[2] = SBox[temp[3]];
        temp[3] = SBox[first];
        temp[0] ^= Rcon[rcon++];
      }
      for (var i = 0; i < 4; ++i) {
        expanded[generated] = (byte)(expanded[generated - 16] ^ temp[i]);
        ++generated;
      }
    }
    return expanded;
  }

  private static void AddRoundKey(Span<byte> state, ReadOnlySpan<byte> key, int offset) {
    for (var i = 0; i < 16; ++i)
      state[i] ^= key[offset + i];
  }

  private static void SubBytes(Span<byte> state) {
    for (var i = 0; i < 16; ++i)
      state[i] = SBox[state[i]];
  }

  private static void ShiftRows(Span<byte> state) {
    Span<byte> copy = stackalloc byte[16];
    state.CopyTo(copy);
    state[0] = copy[0]; state[4] = copy[4]; state[8] = copy[8]; state[12] = copy[12];
    state[1] = copy[5]; state[5] = copy[9]; state[9] = copy[13]; state[13] = copy[1];
    state[2] = copy[10]; state[6] = copy[14]; state[10] = copy[2]; state[14] = copy[6];
    state[3] = copy[15]; state[7] = copy[3]; state[11] = copy[7]; state[15] = copy[11];
  }

  private static void MixColumns(Span<byte> state) {
    for (var column = 0; column < 4; ++column) {
      var i = column * 4;
      var a0 = state[i];
      var a1 = state[i + 1];
      var a2 = state[i + 2];
      var a3 = state[i + 3];
      state[i] = (byte)(Mul2(a0) ^ Mul3(a1) ^ a2 ^ a3);
      state[i + 1] = (byte)(a0 ^ Mul2(a1) ^ Mul3(a2) ^ a3);
      state[i + 2] = (byte)(a0 ^ a1 ^ Mul2(a2) ^ Mul3(a3));
      state[i + 3] = (byte)(Mul3(a0) ^ a1 ^ a2 ^ Mul2(a3));
    }
  }

  private static byte Mul2(byte value) => (byte)((value << 1) ^ ((value & 0x80) != 0 ? 0x1B : 0));
  private static byte Mul3(byte value) => (byte)(Mul2(value) ^ value);
}

/// <summary>Minimal DES encryption primitive used by MDC-2.</summary>
internal static class DesPrimitive {
  private static readonly int[] InitialPermutation = [
    58,50,42,34,26,18,10,2,60,52,44,36,28,20,12,4,
    62,54,46,38,30,22,14,6,64,56,48,40,32,24,16,8,
    57,49,41,33,25,17,9,1,59,51,43,35,27,19,11,3,
    61,53,45,37,29,21,13,5,63,55,47,39,31,23,15,7
  ];
  private static readonly int[] FinalPermutation = [
    40,8,48,16,56,24,64,32,39,7,47,15,55,23,63,31,
    38,6,46,14,54,22,62,30,37,5,45,13,53,21,61,29,
    36,4,44,12,52,20,60,28,35,3,43,11,51,19,59,27,
    34,2,42,10,50,18,58,26,33,1,41,9,49,17,57,25
  ];
  private static readonly int[] Expansion = [
    32,1,2,3,4,5,4,5,6,7,8,9,8,9,10,11,12,13,
    12,13,14,15,16,17,16,17,18,19,20,21,20,21,22,23,24,25,
    24,25,26,27,28,29,28,29,30,31,32,1
  ];
  private static readonly int[] Permutation = [
    16,7,20,21,29,12,28,17,1,15,23,26,5,18,31,10,
    2,8,24,14,32,27,3,9,19,13,30,6,22,11,4,25
  ];
  private static readonly int[] Pc1 = [
    57,49,41,33,25,17,9,1,58,50,42,34,26,18,
    10,2,59,51,43,35,27,19,11,3,60,52,44,36,
    63,55,47,39,31,23,15,7,62,54,46,38,30,22,
    14,6,61,53,45,37,29,21,13,5,28,20,12,4
  ];
  private static readonly int[] Pc2 = [
    14,17,11,24,1,5,3,28,15,6,21,10,23,19,12,4,26,8,
    16,7,27,20,13,2,41,52,31,37,47,55,30,40,51,45,33,48,
    44,49,39,56,34,53,46,42,50,36,29,32
  ];
  private static readonly int[] Shifts = [1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1];
  private static readonly byte[][] SBoxes = [
    [14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0,15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13],
    [15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,3,13,4,7,15,2,8,14,12,0,1,10,6,9,11,5,0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9],
    [10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12],
    [7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,3,15,0,6,10,1,13,8,9,4,5,11,12,7,2,14],
    [2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3],
    [12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13],
    [4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12],
    [13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11]
  ];

  public static byte[] EncryptBlock(ReadOnlySpan<byte> block, ReadOnlySpan<byte> key) {
    if (block.Length != 8)
      throw new ArgumentException("DES block must be exactly 8 bytes.", nameof(block));
    if (key.Length != 8)
      throw new ArgumentException("DES key must be exactly 8 bytes.", nameof(key));

    var blockValue = BinaryPrimitives.ReadUInt64BigEndian(block);
    var keyValue = BinaryPrimitives.ReadUInt64BigEndian(key);
    var subkeys = BuildSubkeys(keyValue);
    var ip = Permute(blockValue, 64, InitialPermutation);
    var left = (uint)(ip >> 32);
    var right = (uint)ip;

    for (var round = 0; round < 16; ++round) {
      var next = left ^ Feistel(right, subkeys[round]);
      left = right;
      right = next;
    }

    var preOutput = ((ulong)right << 32) | left;
    var outputValue = Permute(preOutput, 64, FinalPermutation);
    var result = new byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(result, outputValue);
    return result;
  }

  private static ulong[] BuildSubkeys(ulong key) {
    var reduced = Permute(key, 64, Pc1);
    var c = (uint)((reduced >> 28) & 0x0FFFFFFF);
    var d = (uint)(reduced & 0x0FFFFFFF);
    var result = new ulong[16];
    for (var round = 0; round < 16; ++round) {
      c = Rotate28(c, Shifts[round]);
      d = Rotate28(d, Shifts[round]);
      result[round] = Permute(((ulong)c << 28) | d, 56, Pc2);
    }
    return result;
  }

  private static uint Feistel(uint right, ulong subkey) {
    var expanded = Permute(right, 32, Expansion) ^ subkey;
    uint substituted = 0;
    for (var box = 0; box < 8; ++box) {
      var six = (int)((expanded >> (42 - box * 6)) & 0x3F);
      var row = ((six & 0x20) >> 4) | (six & 1);
      var column = (six >> 1) & 0x0F;
      substituted = (substituted << 4) | SBoxes[box][row * 16 + column];
    }
    return (uint)Permute(substituted, 32, Permutation);
  }

  private static uint Rotate28(uint value, int count) =>
    ((value << count) | (value >> (28 - count))) & 0x0FFFFFFF;

  private static ulong Permute(ulong input, int inputBits, ReadOnlySpan<int> table) {
    ulong result = 0;
    foreach (var position in table)
      result = (result << 1) | ((input >> (inputBits - position)) & 1UL);
    return result;
  }
}
