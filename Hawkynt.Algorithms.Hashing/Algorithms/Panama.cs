using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Little-endian Panama hash.</summary>
public static class PanamaLE {
  public static byte[] Compute(ReadOnlySpan<byte> data) => PanamaCore.Compute(data, true);
}

/// <summary>Big-endian Panama hash.</summary>
public static class PanamaBE {
  public static byte[] Compute(ReadOnlySpan<byte> data) => PanamaCore.Compute(data, false);
}

/// <summary>Panama-LE hermetic MAC used by the registry implementation.</summary>
public static class PanamaLEMac {
  public static byte[] Compute(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key) => PanamaCore.ComputeMac(data, key, true);
}

/// <summary>Panama-BE hermetic MAC used by the registry implementation.</summary>
public static class PanamaBEMac {
  public static byte[] Compute(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key) => PanamaCore.ComputeMac(data, key, false);
}

internal static class PanamaCore {
  private const int Stages = 32;
  private const int StageWords = 8;
  private const int BlockBytes = 32;

  public static byte[] ComputeMac(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, bool littleEndian) {
    var combined = new byte[checked(key.Length + data.Length)];
    key.CopyTo(combined);
    data.CopyTo(combined.AsSpan(key.Length));
    return Compute(combined, littleEndian);
  }

  public static byte[] Compute(ReadOnlySpan<byte> data, bool littleEndian) {
    var a = new uint[17];
    var b = new uint[Stages][];
    for (var i = 0; i < Stages; ++i)
      b[i] = new uint[StageWords];
    var bstart = 0;

    var paddedLength = checked(((data.Length + 1 + BlockBytes - 1) / BlockBytes) * BlockBytes);
    var padded = new byte[paddedLength];
    data.CopyTo(padded);
    padded[data.Length] = 0x01;

    Span<uint> input = stackalloc uint[StageWords];
    for (var offset = 0; offset < padded.Length; offset += BlockBytes) {
      for (var i = 0; i < StageWords; ++i) {
        var bytes = padded.AsSpan(offset + i * 4, 4);
        input[i] = littleEndian
          ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
          : BinaryPrimitives.ReadUInt32BigEndian(bytes);
      }
      Iterate(a, b, ref bstart, input, default, littleEndian, false);
    }

    for (var i = 0; i < 32; ++i)
      Iterate(a, b, ref bstart, default, default, littleEndian, false);

    var result = new byte[32];
    Iterate(a, b, ref bstart, default, result, littleEndian, true);
    return result;
  }

  private static void Iterate(
    uint[] a,
    uint[][] b,
    ref int bstart,
    ReadOnlySpan<uint> input,
    Span<byte> output,
    bool littleEndian,
    bool emit
  ) {
    Span<uint> c = stackalloc uint[17];

    if (emit) {
      for (var i = 0; i < StageWords; ++i) {
        var word = a[AIndex(i + 9)];
        var target = output.Slice(i * 4, 4);
        if (littleEndian)
          BinaryPrimitives.WriteUInt32LittleEndian(target, word);
        else
          BinaryPrimitives.WriteUInt32BigEndian(target, word);
      }
    }

    var b16 = b[(bstart + 16) & 31];
    var b4 = b[(bstart + Stages - 4) & 31];

    bstart = (bstart + 1) & 31;
    var b0 = b[bstart];
    var b25 = b[(bstart + Stages - 25) & 31];

    if (!input.IsEmpty) {
      for (var i = 0; i < StageWords; ++i) {
        var t = b0[i];
        b0[i] = input[i] ^ t;
        b25[(i + 6) & 7] ^= t;
      }
    } else {
      for (var i = 0; i < StageWords; ++i) {
        var t = b0[i];
        b0[i] = a[AIndex(i + 1)] ^ t;
        b25[(i + 6) & 7] ^= t;
      }
    }

    for (var i = 0; i < 17; ++i) {
      var gamma = a[AIndex(i)] ^ (a[AIndex((i + 1) % 17)] | ~a[AIndex((i + 2) % 17)]);
      var position = 5 * i % 17;
      var rotation = position * (position + 1) / 2 % 32;
      c[AIndex(position)] = BitOperations.RotateLeft(gamma, rotation);
    }

    a[AIndex(0)] = c[AIndex(0)] ^ c[AIndex(1)] ^ c[AIndex(4)] ^ 1U;

    if (!input.IsEmpty) {
      for (var i = 0; i < StageWords; ++i)
        a[AIndex(i + 1)] = c[AIndex(i + 1)] ^ c[AIndex((i + 2) % 17)] ^ c[AIndex((i + 5) % 17)] ^ input[i];
    } else {
      for (var i = 0; i < StageWords; ++i)
        a[AIndex(i + 1)] = c[AIndex(i + 1)] ^ c[AIndex((i + 2) % 17)] ^ c[AIndex((i + 5) % 17)] ^ b4[i];
    }

    for (var i = 0; i < StageWords; ++i)
      a[AIndex(i + 9)] = c[AIndex(i + 9)] ^ c[AIndex((i + 10) % 17)] ^ c[AIndex((i + 13) % 17)] ^ b16[i];
  }

  private static int AIndex(int index) => (index * 13 + 16) % 17;
}
