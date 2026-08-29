using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>CubeHash16+16/32+16-256.</summary>
public static class CubeHash256 {
  private static readonly uint[] Iv = [
    0xEA2BD4B4U,0xCCD6F29FU,0x63117E71U,0x35481EAEU,0x22512D5BU,0xE5D94E63U,0x7E624131U,0xF4CC12BEU,
    0xC2D0B696U,0x42AF2070U,0xD0720C35U,0x3361DA8CU,0x28CCECA4U,0x8EF8AD83U,0x4680AC00U,0x40E5FBABU,
    0xD89041C3U,0x6107FBD5U,0x6C859D41U,0xF0B26679U,0x09392549U,0x5FA25603U,0x65C892FDU,0x93CB6285U,
    0x2AF2B5AEU,0x9E4B4E60U,0x774ABFDDU,0x85254725U,0x15815AEBU,0x4AB6AAD6U,0x9CDAF8AFU,0xD6032C0AU
  ];
  public static byte[] Compute(ReadOnlySpan<byte> data) => CubeHashCore.Compute(data, 32, Iv);
}

/// <summary>CubeHash16+16/32+16-512.</summary>
public static class CubeHash512 {
  private static readonly uint[] Iv = [
    0x2AEA2A61U,0x50F494D4U,0x2D538B8BU,0x4167D83EU,0x3FEE2313U,0xC701CF8CU,0xCC39968EU,0x50AC5695U,
    0x4D42C787U,0xA647A8B3U,0x97CF0BEFU,0x825B4537U,0xEEF864D2U,0xF22090C4U,0xD0E5CD33U,0xA23911AEU,
    0xFCD398D9U,0x148FE485U,0x1B017BEFU,0xB6444532U,0x6A536159U,0x2FF5781CU,0x91FA7934U,0x0DBADEA9U,
    0xD65C8A2BU,0xA5A70E75U,0xB1C62456U,0xBC796576U,0x1921C8F7U,0xE7989AF1U,0x7795D246U,0xD43E3B44U
  ];
  public static byte[] Compute(ReadOnlySpan<byte> data) => CubeHashCore.Compute(data, 64, Iv);
}

internal static class CubeHashCore {
  private const int Rate = 32;
  private const int Rounds = 16;

  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes, ReadOnlySpan<uint> iv) {
    Span<uint> state = stackalloc uint[32];
    iv.CopyTo(state);
    var offset = 0;
    while (offset + Rate <= data.Length) {
      Absorb(state, data.Slice(offset, Rate));
      offset += Rate;
    }

    Span<byte> final = stackalloc byte[Rate];
    final.Clear();
    data[offset..].CopyTo(final);
    final[data.Length - offset] = 0x80;
    Absorb(state, final);
    state[31] ^= 1;
    for (var i = 0; i < 10 * Rounds; ++i)
      Transform(state);

    var result = new byte[outputBytes];
    for (var i = 0; i < outputBytes; ++i)
      result[i] = (byte)(state[i >> 2] >> ((i & 3) * 8));
    return result;
  }

  private static void Absorb(Span<uint> state, ReadOnlySpan<byte> block) {
    for (var i = 0; i < Rate / 4; ++i)
      state[i] ^= BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
    for (var i = 0; i < Rounds; ++i)
      Transform(state);
  }

  private static void Transform(Span<uint> x) {
    for (var i = 0; i < 16; ++i)
      x[16 + i] = unchecked(x[16 + i] + x[i]);
    for (var i = 0; i < 16; ++i)
      x[i] = BitOperations.RotateLeft(x[i], 7);
    for (var i = 0; i < 8; ++i)
      (x[i], x[8 + i]) = (x[8 + i], x[i]);
    for (var i = 0; i < 16; ++i)
      x[i] ^= x[16 + i];
    for (var i = 0; i < 16; i += 4) {
      (x[16 + i], x[18 + i]) = (x[18 + i], x[16 + i]);
      (x[17 + i], x[19 + i]) = (x[19 + i], x[17 + i]);
    }
    for (var i = 0; i < 16; ++i)
      x[16 + i] = unchecked(x[16 + i] + x[i]);
    for (var i = 0; i < 16; ++i)
      x[i] = BitOperations.RotateLeft(x[i], 11);
    for (var i = 0; i < 16; i += 8) {
      for (var j = 0; j < 4; ++j)
        (x[i + j], x[i + j + 4]) = (x[i + j + 4], x[i + j]);
    }
    for (var i = 0; i < 16; ++i)
      x[i] ^= x[16 + i];
    for (var i = 0; i < 16; i += 2)
      (x[16 + i], x[17 + i]) = (x[17 + i], x[16 + i]);
  }
}
