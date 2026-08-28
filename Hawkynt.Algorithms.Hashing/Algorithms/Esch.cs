using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Esch256 based on SPARKLE-384.</summary>
public static class Esch256 {
  public static byte[] Compute(ReadOnlySpan<byte> data) => EschCore.Compute(data, 32, 12, 7, 11);
}

/// <summary>Esch384 based on SPARKLE-512.</summary>
public static class Esch384 {
  public static byte[] Compute(ReadOnlySpan<byte> data) => EschCore.Compute(data, 48, 16, 8, 12);
}

internal static class EschCore {
  private const int RateBytes = 16;

  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes, int stateWords, int slimSteps, int bigSteps) {
    var state = new uint[stateWords];
    var offset = 0;

    while (offset + RateBytes < data.Length) {
      Mix(state, data.Slice(offset, RateBytes), 0);
      SparklePermutation.Permute(state, slimSteps);
      offset += RateBytes;
    }

    Span<byte> final = stackalloc byte[RateBytes];
    final.Clear();
    var remaining = data.Length - offset;
    data[offset..].CopyTo(final);
    var domain = remaining == RateBytes ? 2 : 1;
    if (remaining != RateBytes)
      final[remaining] = 0x80;
    Mix(state, final, domain);
    SparklePermutation.Permute(state, bigSteps);

    var result = new byte[outputBytes];
    Span<byte> rate = stackalloc byte[RateBytes];
    var written = 0;
    while (written < outputBytes) {
      var take = Math.Min(RateBytes, outputBytes - written);
      for (var i = 0; i < 4; ++i)
        BinaryPrimitives.WriteUInt32LittleEndian(rate.Slice(i * 4, 4), state[i]);
      rate[..take].CopyTo(result.AsSpan(written));
      written += take;
      if (written < outputBytes)
        SparklePermutation.Permute(state, slimSteps);
    }
    return result;
  }

  private static void Mix(Span<uint> state, ReadOnlySpan<byte> block, int domain) {
    Span<uint> words = stackalloc uint[4];
    for (var i = 0; i < 4; ++i)
      words[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));

    var tx = Linear(words[0] ^ words[2]);
    var ty = Linear(words[1] ^ words[3]);
    state[0] ^= words[0] ^ ty;
    state[1] ^= words[1] ^ tx;
    state[2] ^= words[2] ^ ty;
    state[3] ^= words[3] ^ tx;

    if (state.Length == 12) {
      if (domain != 0)
        state[5] ^= (uint)domain << 24;
      state[4] ^= ty;
      state[5] ^= tx;
    } else {
      if (domain != 0)
        state[7] ^= (uint)domain << 24;
      state[4] ^= ty;
      state[5] ^= tx;
      state[6] ^= ty;
      state[7] ^= tx;
    }
  }

  private static uint Linear(uint value) => BitOperations.RotateLeft(value ^ unchecked(value << 16), 16);
}

/// <summary>SPARKLE permutation core used by Esch-family hashes.</summary>
internal static class SparklePermutation {
  private static readonly uint[] Constants = [
    0xB7E15162U,0xBF715880U,0x38B4DA56U,0x324E7738U,
    0xBB1185EBU,0x4F7C7B57U,0xCFBFA1C8U,0xC2B3293DU,
    0xB7E15162U,0xBF715880U,0x38B4DA56U,0x324E7738U
  ];

  public static void Permute(Span<uint> state, int steps) {
    if (state.Length is not 12 and not 16)
      throw new ArgumentException("SPARKLE state must contain 12 or 16 32-bit words.", nameof(state));

    var branches = state.Length / 2;
    Span<uint> x = stackalloc uint[8];
    Span<uint> y = stackalloc uint[8];
    for (var i = 0; i < branches; ++i) {
      x[i] = state[2 * i];
      y[i] = state[2 * i + 1];
    }

    for (var step = 0; step < steps; ++step) {
      y[0] ^= Constants[step];
      y[1] ^= (uint)step;
      for (var i = 0; i < branches; ++i)
        Alzette(ref x[i], ref y[i], Constants[i]);

      if (branches == 6)
        LinearLayer6(x, y);
      else
        LinearLayer8(x, y);
    }

    for (var i = 0; i < branches; ++i) {
      state[2 * i] = x[i];
      state[2 * i + 1] = y[i];
    }
  }

  private static void Alzette(ref uint x, ref uint y, uint constant) {
    x = unchecked(x + BitOperations.RotateLeft(y, 1));
    y ^= BitOperations.RotateLeft(x, 8);
    x ^= constant;
    x = unchecked(x + BitOperations.RotateLeft(y, 15));
    y ^= BitOperations.RotateLeft(x, 15);
    x ^= constant;
    x = unchecked(x + y);
    y ^= BitOperations.RotateLeft(x, 1);
    x ^= constant;
    x = unchecked(x + BitOperations.RotateLeft(y, 8));
    y ^= BitOperations.RotateLeft(x, 16);
    x ^= constant;
  }

  private static uint Linear(uint value) => BitOperations.RotateLeft(value ^ unchecked(value << 16), 16);

  private static void LinearLayer6(Span<uint> x, Span<uint> y) {
    var tx = Linear(x[0] ^ x[1] ^ x[2]);
    var ty = Linear(y[0] ^ y[1] ^ y[2]);

    var x0 = x[0]; var x1 = x[1]; var x2 = x[2]; var x5 = x[5];
    var y0 = y[0]; var y1 = y[1]; var y2 = y[2]; var y5 = y[5];

    y[3] ^= tx;
    y[4] ^= tx;
    tx ^= y5;
    y[5] = y2;
    y[2] = y[3] ^ y0;
    y[3] = y0;
    y[0] = y[4] ^ y1;
    y[4] = y1;
    y[1] = tx ^ y[5];

    x[3] ^= ty;
    x[4] ^= ty;
    ty ^= x5;
    x[5] = x2;
    x[2] = x[3] ^ x0;
    x[3] = x0;
    x[0] = x[4] ^ x1;
    x[4] = x1;
    x[1] = ty ^ x[5];
  }

  private static void LinearLayer8(Span<uint> x, Span<uint> y) {
    var tx = Linear(x[0] ^ x[1] ^ x[2] ^ x[3]);
    var ty = Linear(y[0] ^ y[1] ^ y[2] ^ y[3]);

    var x0 = x[0]; var x1 = x[1]; var x2 = x[2]; var x3 = x[3]; var x7 = x[7];
    var y0 = y[0]; var y1 = y[1]; var y2 = y[2]; var y3 = y[3]; var y7 = y[7];

    y[4] ^= tx;
    y[5] ^= tx;
    y[6] ^= tx;
    tx ^= y7;
    y[7] = y3;
    y[3] = y[4] ^ y0;
    y[4] = y0;
    y[0] = y[5] ^ y1;
    y[5] = y1;
    y[1] = y[6] ^ y2;
    y[6] = y2;
    y[2] = tx ^ y[7];

    x[4] ^= ty;
    x[5] ^= ty;
    x[6] ^= ty;
    ty ^= x7;
    x[7] = x3;
    x[3] = x[4] ^ x0;
    x[4] = x0;
    x[0] = x[5] ^ x1;
    x[5] = x1;
    x[1] = x[6] ^ x2;
    x[6] = x2;
    x[2] = ty ^ x[7];
  }
}
