using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Keccak-f[1600] sponge primitives and standard Keccak/SHA-3/SHAKE variants.</summary>
public static class Keccak {
  private static readonly ulong[] RoundConstants = [
    0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL, 0x8000000080008000UL,
    0x000000000000808BUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
    0x000000000000008AUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
    0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL, 0x8000000000008003UL,
    0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800AUL, 0x800000008000000AUL,
    0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
  ];

  private static readonly int[] RotationOffsets = [
     0,  1, 62, 28, 27,
    36, 44,  6, 55, 20,
     3, 10, 43, 25, 39,
    41, 45, 15, 21,  8,
    18,  2, 61, 56, 14
  ];

  public static byte[] Compute224(ReadOnlySpan<byte> data) => Sponge(data, 144, 0x01, 28);
  public static byte[] Compute256(ReadOnlySpan<byte> data) => Sponge(data, 136, 0x01, 32);
  public static byte[] Compute384(ReadOnlySpan<byte> data) => Sponge(data, 104, 0x01, 48);
  public static byte[] Compute512(ReadOnlySpan<byte> data) => Sponge(data, 72, 0x01, 64);

  internal static byte[] Sponge(ReadOnlySpan<byte> data, int rateBytes, byte suffix, int outputBytes) {
    if (rateBytes <= 0 || rateBytes >= 200 || (rateBytes & 7) != 0)
      throw new ArgumentOutOfRangeException(nameof(rateBytes));
    if (outputBytes < 0)
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    Span<ulong> state = stackalloc ulong[25];
    state.Clear();
    var offset = 0;

    while (offset + rateBytes <= data.Length) {
      AbsorbBlock(state, data.Slice(offset, rateBytes));
      Permute(state);
      offset += rateBytes;
    }

    Span<byte> finalBlock = stackalloc byte[rateBytes];
    finalBlock.Clear();
    data[offset..].CopyTo(finalBlock);
    finalBlock[data.Length - offset] ^= suffix;
    finalBlock[rateBytes - 1] ^= 0x80;
    AbsorbBlock(state, finalBlock);
    Permute(state);

    var result = new byte[outputBytes];
    var written = 0;
    while (written < outputBytes) {
      var take = Math.Min(rateBytes, outputBytes - written);
      for (var i = 0; i < take; ++i)
        result[written + i] = (byte)(state[i >> 3] >> ((i & 7) * 8));
      written += take;
      if (written < outputBytes)
        Permute(state);
    }
    return result;
  }

  private static void AbsorbBlock(Span<ulong> state, ReadOnlySpan<byte> block) {
    for (var i = 0; i < block.Length / 8; ++i)
      state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(block[(i * 8)..]);
  }

  internal static void Permute(Span<ulong> state) {
    Span<ulong> c = stackalloc ulong[5];
    Span<ulong> d = stackalloc ulong[5];
    Span<ulong> b = stackalloc ulong[25];

    for (var round = 0; round < 24; ++round) {
      for (var x = 0; x < 5; ++x)
        c[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];

      for (var x = 0; x < 5; ++x)
        d[x] = c[(x + 4) % 5] ^ BitOperations.RotateLeft(c[(x + 1) % 5], 1);

      for (var y = 0; y < 5; ++y)
        for (var x = 0; x < 5; ++x)
          state[x + 5 * y] ^= d[x];

      for (var y = 0; y < 5; ++y) {
        for (var x = 0; x < 5; ++x) {
          var source = x + 5 * y;
          var destination = y + 5 * ((2 * x + 3 * y) % 5);
          b[destination] = BitOperations.RotateLeft(state[source], RotationOffsets[source]);
        }
      }

      for (var y = 0; y < 5; ++y)
        for (var x = 0; x < 5; ++x)
          state[x + 5 * y] = b[x + 5 * y] ^ ((~b[(x + 1) % 5 + 5 * y]) & b[(x + 2) % 5 + 5 * y]);

      state[0] ^= RoundConstants[round];
    }
  }
}

/// <summary>SHA-3 family from FIPS 202.</summary>
public static class Sha3 {
  public static byte[] Compute224(ReadOnlySpan<byte> data) => Keccak.Sponge(data, 144, 0x06, 28);
  public static byte[] Compute256(ReadOnlySpan<byte> data) => Keccak.Sponge(data, 136, 0x06, 32);
  public static byte[] Compute384(ReadOnlySpan<byte> data) => Keccak.Sponge(data, 104, 0x06, 48);
  public static byte[] Compute512(ReadOnlySpan<byte> data) => Keccak.Sponge(data, 72, 0x06, 64);
}

/// <summary>SHAKE extendable-output functions from FIPS 202.</summary>
public static class Shake {
  public static byte[] Compute128(ReadOnlySpan<byte> data, int outputBytes) => Keccak.Sponge(data, 168, 0x1F, outputBytes);
  public static byte[] Compute256(ReadOnlySpan<byte> data, int outputBytes) => Keccak.Sponge(data, 136, 0x1F, outputBytes);
}

/// <summary>cSHAKE customizable XOF functions from NIST SP 800-185.</summary>
public static class CShake {
  public static byte[] Compute128(
    ReadOnlySpan<byte> data,
    int outputBytes,
    ReadOnlySpan<byte> functionName = default,
    ReadOnlySpan<byte> customization = default
  ) => Compute(data, outputBytes, 168, functionName, customization);

  public static byte[] Compute256(
    ReadOnlySpan<byte> data,
    int outputBytes,
    ReadOnlySpan<byte> functionName = default,
    ReadOnlySpan<byte> customization = default
  ) => Compute(data, outputBytes, 136, functionName, customization);

  private static byte[] Compute(
    ReadOnlySpan<byte> data,
    int outputBytes,
    int rateBytes,
    ReadOnlySpan<byte> functionName,
    ReadOnlySpan<byte> customization
  ) {
    if (functionName.IsEmpty && customization.IsEmpty)
      return Keccak.Sponge(data, rateBytes, 0x1F, outputBytes);

    var prefix = Sp800185.BytePad(Sp800185.Concat(Sp800185.EncodeString(functionName), Sp800185.EncodeString(customization)), rateBytes);
    var input = new byte[prefix.Length + data.Length];
    prefix.CopyTo(input, 0);
    data.CopyTo(input.AsSpan(prefix.Length));
    return Keccak.Sponge(input, rateBytes, 0x04, outputBytes);
  }
}

/// <summary>TupleHash from NIST SP 800-185.</summary>
public static class TupleHash {
  private static readonly byte[] FunctionName = Encoding.ASCII.GetBytes("TupleHash");

  public static byte[] Compute128(IEnumerable<ReadOnlyMemory<byte>> tuple, int outputBytes, ReadOnlySpan<byte> customization = default) =>
    Compute(tuple, outputBytes, customization, 128);

  public static byte[] Compute256(IEnumerable<ReadOnlyMemory<byte>> tuple, int outputBytes, ReadOnlySpan<byte> customization = default) =>
    Compute(tuple, outputBytes, customization, 256);

  private static byte[] Compute(IEnumerable<ReadOnlyMemory<byte>> tuple, int outputBytes, ReadOnlySpan<byte> customization, int security) {
    if (tuple is null)
      throw new ArgumentNullException(nameof(tuple));
    if (outputBytes < 0)
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    var parts = new List<byte[]>();
    var total = 0;
    foreach (var item in tuple) {
      var encoded = Sp800185.EncodeString(item.Span);
      parts.Add(encoded);
      total = checked(total + encoded.Length);
    }

    var trailer = Sp800185.RightEncode((ulong)outputBytes * 8);
    var input = new byte[checked(total + trailer.Length)];
    var offset = 0;
    foreach (var part in parts) {
      part.CopyTo(input, offset);
      offset += part.Length;
    }
    trailer.CopyTo(input, offset);

    return security == 128
      ? CShake.Compute128(input, outputBytes, FunctionName, customization)
      : CShake.Compute256(input, outputBytes, FunctionName, customization);
  }
}

/// <summary>ParallelHash from NIST SP 800-185.</summary>
public static class ParallelHash {
  private static readonly byte[] FunctionName = Encoding.ASCII.GetBytes("ParallelHash");

  public static byte[] Compute128(ReadOnlySpan<byte> data, int blockBytes, int outputBytes, ReadOnlySpan<byte> customization = default) =>
    Compute(data, blockBytes, outputBytes, customization, 128);

  public static byte[] Compute256(ReadOnlySpan<byte> data, int blockBytes, int outputBytes, ReadOnlySpan<byte> customization = default) =>
    Compute(data, blockBytes, outputBytes, customization, 256);

  private static byte[] Compute(ReadOnlySpan<byte> data, int blockBytes, int outputBytes, ReadOnlySpan<byte> customization, int security) {
    if (blockBytes <= 0)
      throw new ArgumentOutOfRangeException(nameof(blockBytes));
    if (outputBytes < 0)
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    var blocks = (data.Length + blockBytes - 1) / blockBytes;
    var chainingBytes = security == 128 ? 32 : 64;
    var encodedBlockSize = Sp800185.LeftEncode((ulong)blockBytes);
    var encodedBlocks = Sp800185.RightEncode((ulong)blocks);
    var encodedOutput = Sp800185.RightEncode((ulong)outputBytes * 8);
    var innerLength = checked(encodedBlockSize.Length + blocks * chainingBytes + encodedBlocks.Length + encodedOutput.Length);
    var inner = new byte[innerLength];
    var offset = 0;
    encodedBlockSize.CopyTo(inner, offset);
    offset += encodedBlockSize.Length;

    for (var i = 0; i < blocks; ++i) {
      var chunk = data.Slice(i * blockBytes, Math.Min(blockBytes, data.Length - i * blockBytes));
      var digest = security == 128 ? Shake.Compute128(chunk, chainingBytes) : Shake.Compute256(chunk, chainingBytes);
      digest.CopyTo(inner, offset);
      offset += digest.Length;
    }

    encodedBlocks.CopyTo(inner, offset);
    offset += encodedBlocks.Length;
    encodedOutput.CopyTo(inner, offset);

    return security == 128
      ? CShake.Compute128(inner, outputBytes, FunctionName, customization)
      : CShake.Compute256(inner, outputBytes, FunctionName, customization);
  }
}

internal static class Sp800185 {
  public static byte[] LeftEncode(ulong value) {
    var n = 1;
    while (n < 8 && (value >> (8 * n)) != 0)
      ++n;

    var result = new byte[n + 1];
    result[0] = (byte)n;
    for (var i = 0; i < n; ++i)
      result[n - i] = (byte)(value >> (8 * i));
    return result;
  }

  public static byte[] RightEncode(ulong value) {
    var n = 1;
    while (n < 8 && (value >> (8 * n)) != 0)
      ++n;

    var result = new byte[n + 1];
    for (var i = 0; i < n; ++i)
      result[n - 1 - i] = (byte)(value >> (8 * i));
    result[n] = (byte)n;
    return result;
  }

  public static byte[] EncodeString(ReadOnlySpan<byte> value) =>
    Concat(LeftEncode((ulong)value.Length * 8), value.ToArray());

  public static byte[] BytePad(ReadOnlySpan<byte> value, int width) {
    if (width <= 0)
      throw new ArgumentOutOfRangeException(nameof(width));

    var prefix = LeftEncode((ulong)width);
    var unpadded = checked(prefix.Length + value.Length);
    var length = checked(((unpadded + width - 1) / width) * width);
    var result = new byte[length];
    prefix.CopyTo(result, 0);
    value.CopyTo(result.AsSpan(prefix.Length));
    return result;
  }

  public static byte[] Concat(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) {
    var result = new byte[checked(first.Length + second.Length)];
    first.CopyTo(result);
    second.CopyTo(result.AsSpan(first.Length));
    return result;
  }
}
