using System.Buffers.Binary;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// Provides the Groestl-224 hash implementation.
/// </summary>
public static class Groestl224 {
  /// <summary>
  /// Computes the Groestl-224 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => GroestlCore.Compute(data, 28);
}
/// <summary>
/// Provides the Groestl-256 hash implementation.
/// </summary>
public static class Groestl256 {
  /// <summary>
  /// Computes the Groestl-256 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => GroestlCore.Compute(data, 32);
}
/// <summary>
/// Provides the Groestl-384 hash implementation.
/// </summary>
public static class Groestl384 {
  /// <summary>
  /// Computes the Groestl-384 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => GroestlCore.Compute(data, 48);
}
/// <summary>
/// Provides the Groestl-512 hash implementation.
/// </summary>
public static class Groestl512 {
  /// <summary>
  /// Computes the Groestl-512 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => GroestlCore.Compute(data, 64);
}

internal static class GroestlCore {
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

  private static readonly byte[,] MixMatrix = {
    {2,2,3,4,5,3,5,7},
    {7,2,2,3,4,5,3,5},
    {5,7,2,2,3,4,5,3},
    {3,5,7,2,2,3,4,5},
    {5,3,5,7,2,2,3,4},
    {4,5,3,5,7,2,2,3},
    {3,4,5,3,5,7,2,2},
    {2,3,4,5,3,5,7,2}
  };

  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes) {
    if (outputBytes is not (28 or 32 or 48 or 64))
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    var stateBytes = outputBytes <= 32 ? 64 : 128;
    var columns = stateBytes / 8;
    var state = new byte[stateBytes];
    BinaryPrimitives.WriteUInt64BigEndian(state.AsSpan(stateBytes - 8, 8), (ulong)outputBytes * 8UL);

    var fullBlocks = data.Length / stateBytes;
    for (var block = 0; block < fullBlocks; ++block)
      Compress(state, data.Slice(block * stateBytes, stateBytes), columns);

    var remainder = data.Length % stateBytes;
    var paddedBlocks = remainder <= stateBytes - 9 ? 1 : 2;
    var padded = new byte[paddedBlocks * stateBytes];
    data[(fullBlocks * stateBytes)..].CopyTo(padded);
    padded[remainder] = 0x80;
    var totalBlocks = checked((ulong)fullBlocks + (ulong)paddedBlocks);
    BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(padded.Length - 8, 8), totalBlocks);

    for (var offset = 0; offset < padded.Length; offset += stateBytes)
      Compress(state, padded.AsSpan(offset, stateBytes), columns);

    var transformed = state.ToArray();
    PermuteP(transformed, columns);
    for (var i = 0; i < stateBytes; ++i)
      transformed[i] ^= state[i];

    return transformed.AsSpan(stateBytes - outputBytes, outputBytes).ToArray();
  }

  private static void Compress(byte[] state, ReadOnlySpan<byte> message, int columns) {
    var p = new byte[state.Length];
    var q = message.ToArray();
    for (var i = 0; i < state.Length; ++i)
      p[i] = (byte)(state[i] ^ message[i]);

    PermuteP(p, columns);
    PermuteQ(q, columns);
    for (var i = 0; i < state.Length; ++i)
      state[i] ^= (byte)(p[i] ^ q[i]);
  }

  private static void PermuteP(byte[] state, int columns) {
    var rounds = columns == 8 ? 10 : 14;
    ReadOnlySpan<int> shifts = columns == 8 ? [0,1,2,3,4,5,6,7] : [0,1,2,3,4,5,6,11];
    for (var round = 0; round < rounds; ++round) {
      for (var column = 0; column < columns; ++column)
        state[column * 8] ^= (byte)((column << 4) ^ round);
      SubBytes(state);
      ShiftRows(state, columns, shifts);
      MixBytes(state, columns);
    }
  }

  private static void PermuteQ(byte[] state, int columns) {
    var rounds = columns == 8 ? 10 : 14;
    ReadOnlySpan<int> shifts = columns == 8 ? [1,3,5,7,0,2,4,6] : [1,3,5,11,0,2,4,6];
    for (var round = 0; round < rounds; ++round) {
      for (var column = 0; column < columns; ++column) {
        var baseOffset = column * 8;
        for (var row = 0; row < 7; ++row)
          state[baseOffset + row] ^= 0xFF;
        state[baseOffset + 7] ^= (byte)(0xFF ^ (column << 4) ^ round);
      }
      SubBytes(state);
      ShiftRows(state, columns, shifts);
      MixBytes(state, columns);
    }
  }

  private static void SubBytes(byte[] state) {
    for (var i = 0; i < state.Length; ++i)
      state[i] = SBox[state[i]];
  }

  private static void ShiftRows(byte[] state, int columns, ReadOnlySpan<int> shifts) {
    var copy = state.ToArray();
    for (var row = 0; row < 8; ++row) {
      var shift = shifts[row];
      for (var column = 0; column < columns; ++column)
        state[column * 8 + row] = copy[((column + shift) % columns) * 8 + row];
    }
  }

  private static void MixBytes(byte[] state, int columns) {
    Span<byte> input = stackalloc byte[8];
    Span<byte> output = stackalloc byte[8];
    for (var column = 0; column < columns; ++column) {
      state.AsSpan(column * 8, 8).CopyTo(input);
      for (var row = 0; row < 8; ++row) {
        byte value = 0;
        for (var i = 0; i < 8; ++i)
          value ^= Multiply(MixMatrix[row, i], input[i]);
        output[row] = value;
      }
      output.CopyTo(state.AsSpan(column * 8, 8));
    }
  }

  private static byte Multiply(byte left, byte right) {
    byte result = 0;
    var a = left;
    var b = right;
    while (b != 0) {
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
}
