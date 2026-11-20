using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Keccak-f[1600] sponge primitives and standard Keccak/SHA-3/SHAKE variants.</summary>
public static class Keccak {
  /// <summary>The four standard Keccak digest sizes.</summary>
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    new(224, 256, 32),
    new(384, 512, 128)
  ];

  private static readonly ulong[] RoundConstants = [
    0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL, 0x8000000080008000UL,
    0x000000000000808BUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
    0x000000000000008AUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
    0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL, 0x8000000000008003UL,
    0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800AUL, 0x800000008000000AUL,
    0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
  ];

  // rho rotation offsets per lane x + 5y (FIPS 202 table 2), written into Permute:
  //  0  1 62 28 27 / 36 44  6 55 20 / 3 10 43 25 39 / 41 45 15 21  8 / 18  2 61 56 14

  /// <summary>
  /// Computes the 224-bit Keccak hash of the supplied data.
  /// </summary>
  public static byte[] Compute224(ReadOnlySpan<byte> data) => Sponge(data, 144, 0x01, 28);
  /// <summary>
  /// Computes the 256-bit Keccak hash of the supplied data.
  /// </summary>
  public static byte[] Compute256(ReadOnlySpan<byte> data) => Sponge(data, 136, 0x01, 32);
  /// <summary>
  /// Computes the 384-bit Keccak hash of the supplied data.
  /// </summary>
  public static byte[] Compute384(ReadOnlySpan<byte> data) => Sponge(data, 104, 0x01, 48);
  /// <summary>
  /// Computes the 512-bit Keccak hash of the supplied data.
  /// </summary>
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

  /// <summary>Keccak-f[1600] on 25 lanes held in locals (theta, rho and pi, chi, iota per round).</summary>
  internal static void Permute(Span<ulong> state) {
    ulong a0 = state[0]; ulong a1 = state[1]; ulong a2 = state[2]; ulong a3 = state[3]; ulong a4 = state[4]; ulong a5 = state[5]; ulong a6 = state[6]; ulong a7 = state[7]; ulong a8 = state[8]; ulong a9 = state[9]; ulong a10 = state[10]; ulong a11 = state[11]; ulong a12 = state[12]; ulong a13 = state[13]; ulong a14 = state[14]; ulong a15 = state[15]; ulong a16 = state[16]; ulong a17 = state[17]; ulong a18 = state[18]; ulong a19 = state[19]; ulong a20 = state[20]; ulong a21 = state[21]; ulong a22 = state[22]; ulong a23 = state[23]; ulong a24 = state[24];
    for (var round = 0; round < 24; ++round) {
      var c0 = a0 ^ a5 ^ a10 ^ a15 ^ a20;
      var c1 = a1 ^ a6 ^ a11 ^ a16 ^ a21;
      var c2 = a2 ^ a7 ^ a12 ^ a17 ^ a22;
      var c3 = a3 ^ a8 ^ a13 ^ a18 ^ a23;
      var c4 = a4 ^ a9 ^ a14 ^ a19 ^ a24;
      var d0 = c4 ^ BitOperations.RotateLeft(c1, 1);
      var d1 = c0 ^ BitOperations.RotateLeft(c2, 1);
      var d2 = c1 ^ BitOperations.RotateLeft(c3, 1);
      var d3 = c2 ^ BitOperations.RotateLeft(c4, 1);
      var d4 = c3 ^ BitOperations.RotateLeft(c0, 1);
      a0 ^= d0;
      a1 ^= d1;
      a2 ^= d2;
      a3 ^= d3;
      a4 ^= d4;
      a5 ^= d0;
      a6 ^= d1;
      a7 ^= d2;
      a8 ^= d3;
      a9 ^= d4;
      a10 ^= d0;
      a11 ^= d1;
      a12 ^= d2;
      a13 ^= d3;
      a14 ^= d4;
      a15 ^= d0;
      a16 ^= d1;
      a17 ^= d2;
      a18 ^= d3;
      a19 ^= d4;
      a20 ^= d0;
      a21 ^= d1;
      a22 ^= d2;
      a23 ^= d3;
      a24 ^= d4;
      var b0 = a0;
      var b1 = BitOperations.RotateLeft(a6, 44);
      var b2 = BitOperations.RotateLeft(a12, 43);
      var b3 = BitOperations.RotateLeft(a18, 21);
      var b4 = BitOperations.RotateLeft(a24, 14);
      var b5 = BitOperations.RotateLeft(a3, 28);
      var b6 = BitOperations.RotateLeft(a9, 20);
      var b7 = BitOperations.RotateLeft(a10, 3);
      var b8 = BitOperations.RotateLeft(a16, 45);
      var b9 = BitOperations.RotateLeft(a22, 61);
      var b10 = BitOperations.RotateLeft(a1, 1);
      var b11 = BitOperations.RotateLeft(a7, 6);
      var b12 = BitOperations.RotateLeft(a13, 25);
      var b13 = BitOperations.RotateLeft(a19, 8);
      var b14 = BitOperations.RotateLeft(a20, 18);
      var b15 = BitOperations.RotateLeft(a4, 27);
      var b16 = BitOperations.RotateLeft(a5, 36);
      var b17 = BitOperations.RotateLeft(a11, 10);
      var b18 = BitOperations.RotateLeft(a17, 15);
      var b19 = BitOperations.RotateLeft(a23, 56);
      var b20 = BitOperations.RotateLeft(a2, 62);
      var b21 = BitOperations.RotateLeft(a8, 55);
      var b22 = BitOperations.RotateLeft(a14, 39);
      var b23 = BitOperations.RotateLeft(a15, 41);
      var b24 = BitOperations.RotateLeft(a21, 2);
      a0 = b0 ^ (~b1 & b2);
      a1 = b1 ^ (~b2 & b3);
      a2 = b2 ^ (~b3 & b4);
      a3 = b3 ^ (~b4 & b0);
      a4 = b4 ^ (~b0 & b1);
      a5 = b5 ^ (~b6 & b7);
      a6 = b6 ^ (~b7 & b8);
      a7 = b7 ^ (~b8 & b9);
      a8 = b8 ^ (~b9 & b5);
      a9 = b9 ^ (~b5 & b6);
      a10 = b10 ^ (~b11 & b12);
      a11 = b11 ^ (~b12 & b13);
      a12 = b12 ^ (~b13 & b14);
      a13 = b13 ^ (~b14 & b10);
      a14 = b14 ^ (~b10 & b11);
      a15 = b15 ^ (~b16 & b17);
      a16 = b16 ^ (~b17 & b18);
      a17 = b17 ^ (~b18 & b19);
      a18 = b18 ^ (~b19 & b15);
      a19 = b19 ^ (~b15 & b16);
      a20 = b20 ^ (~b21 & b22);
      a21 = b21 ^ (~b22 & b23);
      a22 = b22 ^ (~b23 & b24);
      a23 = b23 ^ (~b24 & b20);
      a24 = b24 ^ (~b20 & b21);
      a0 ^= RoundConstants[round];
    }
    state[0] = a0; state[1] = a1; state[2] = a2; state[3] = a3; state[4] = a4; state[5] = a5; state[6] = a6; state[7] = a7; state[8] = a8; state[9] = a9; state[10] = a10; state[11] = a11; state[12] = a12; state[13] = a13; state[14] = a14; state[15] = a15; state[16] = a16; state[17] = a17; state[18] = a18; state[19] = a19; state[20] = a20; state[21] = a21; state[22] = a22; state[23] = a23; state[24] = a24;
  }
}

/// <summary>SHA-3 family from FIPS 202.</summary>
public static class Sha3 {
  /// <summary>
  /// Computes the 224-bit Sha-3 hash of the supplied data.
  /// </summary>
  public static byte[] Compute224(ReadOnlySpan<byte> data) => Keccak.Sponge(data, 144, 0x06, 28);
  /// <summary>
  /// Computes the 256-bit Sha-3 hash of the supplied data.
  /// </summary>
  public static byte[] Compute256(ReadOnlySpan<byte> data) => Keccak.Sponge(data, 136, 0x06, 32);
  /// <summary>
  /// Computes the 384-bit Sha-3 hash of the supplied data.
  /// </summary>
  public static byte[] Compute384(ReadOnlySpan<byte> data) => Keccak.Sponge(data, 104, 0x06, 48);
  /// <summary>
  /// Computes the 512-bit Sha-3 hash of the supplied data.
  /// </summary>
  public static byte[] Compute512(ReadOnlySpan<byte> data) => Keccak.Sponge(data, 72, 0x06, 64);
}

/// <summary>SHAKE extendable-output functions from FIPS 202.</summary>
public static class Shake {
  /// <summary>
  /// Computes the 128-bit Shake hash of the supplied data.
  /// </summary>
  public static byte[] Compute128(ReadOnlySpan<byte> data, int outputBytes) => Keccak.Sponge(data, 168, 0x1F, outputBytes);
  /// <summary>
  /// Computes the 256-bit Shake hash of the supplied data.
  /// </summary>
  public static byte[] Compute256(ReadOnlySpan<byte> data, int outputBytes) => Keccak.Sponge(data, 136, 0x1F, outputBytes);
}

/// <summary>cSHAKE customizable XOF functions from NIST SP 800-185.</summary>
public static class CShake {
  /// <summary>
  /// Computes the 128-bit C Shake hash of the supplied data.
  /// </summary>
  public static byte[] Compute128(
    ReadOnlySpan<byte> data,
    int outputBytes,
    ReadOnlySpan<byte> functionName = default,
    ReadOnlySpan<byte> customization = default
  ) => Compute(data, outputBytes, 168, functionName, customization);

  /// <summary>
  /// Computes the 256-bit C Shake hash of the supplied data.
  /// </summary>
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

  /// <summary>
  /// Computes the 128-bit Tuple Hash hash of the supplied data.
  /// </summary>
  public static byte[] Compute128(IEnumerable<ReadOnlyMemory<byte>> tuple, int outputBytes, ReadOnlySpan<byte> customization = default) =>
    Compute(tuple, outputBytes, customization, 128);

  /// <summary>
  /// Computes the 256-bit Tuple Hash hash of the supplied data.
  /// </summary>
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

  /// <summary>
  /// Computes the 128-bit Parallel Hash hash of the supplied data.
  /// </summary>
  public static byte[] Compute128(ReadOnlySpan<byte> data, int blockBytes, int outputBytes, ReadOnlySpan<byte> customization = default) =>
    Compute(data, blockBytes, outputBytes, customization, 128);

  /// <summary>
  /// Computes the 256-bit Parallel Hash hash of the supplied data.
  /// </summary>
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
