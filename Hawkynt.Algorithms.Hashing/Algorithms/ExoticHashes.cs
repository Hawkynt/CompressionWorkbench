using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Keccak variant used by the DarkCrypt Total Commander plugin.</summary>
/// <remarks>Uses the first 18 Keccak-f[1600] rounds, a fixed 64-byte rate and the byte suffix 01 40 40 01 followed by zero fill.</remarks>
public static class DarkCryptKeccak {
  private const int RateBytes = 64;
  private const int OutputBytes = 64;
  private static readonly byte[] PaddingSuffix = [0x01, 0x40, 0x40, 0x01];
  private static readonly ulong[] RoundConstants = [
    0x0000000000000001UL,0x0000000000008082UL,0x800000000000808AUL,0x8000000080008000UL,
    0x000000000000808BUL,0x0000000080000001UL,0x8000000080008081UL,0x8000000000008009UL,
    0x000000000000008AUL,0x0000000000000088UL,0x0000000080008009UL,0x000000008000000AUL,
    0x000000008000808BUL,0x800000000000008BUL,0x8000000000008089UL,0x8000000000008003UL,
    0x8000000000008002UL,0x8000000000000080UL
  ];
  private static readonly int[] RotationOffsets = [
    0,1,62,28,27,36,44,6,55,20,3,10,43,25,39,41,45,15,21,8,18,2,61,56,14
  ];

  /// <summary>
  /// Computes the Dark Crypt Keccak hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    var streamLength = checked(data.Length + PaddingSuffix.Length);
    var paddedLength = checked(((streamLength + RateBytes - 1) / RateBytes) * RateBytes);
    var stream = new byte[paddedLength];
    data.CopyTo(stream);
    PaddingSuffix.CopyTo(stream, data.Length);

    Span<ulong> state = stackalloc ulong[25];
    state.Clear();
    for (var offset = 0; offset < stream.Length; offset += RateBytes) {
      for (var lane = 0; lane < RateBytes / 8; ++lane)
        state[lane] ^= BinaryPrimitives.ReadUInt64LittleEndian(stream.AsSpan(offset + lane * 8, 8));
      Permute18(state);
    }

    var result = new byte[OutputBytes];
    for (var lane = 0; lane < OutputBytes / 8; ++lane)
      BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(lane * 8, 8), state[lane]);
    return result;
  }

  private static void Permute18(Span<ulong> state) {
    Span<ulong> c = stackalloc ulong[5];
    Span<ulong> d = stackalloc ulong[5];
    Span<ulong> b = stackalloc ulong[25];
    for (var round = 0; round < RoundConstants.Length; ++round) {
      for (var x = 0; x < 5; ++x)
        c[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];
      for (var x = 0; x < 5; ++x)
        d[x] = c[(x + 4) % 5] ^ BitOperations.RotateLeft(c[(x + 1) % 5], 1);
      for (var y = 0; y < 5; ++y)
        for (var x = 0; x < 5; ++x)
          state[x + 5 * y] ^= d[x];
      for (var y = 0; y < 5; ++y)
        for (var x = 0; x < 5; ++x) {
          var source = x + 5 * y;
          b[y + 5 * ((2 * x + 3 * y) % 5)] = BitOperations.RotateLeft(state[source], RotationOffsets[source]);
        }
      for (var y = 0; y < 5; ++y)
        for (var x = 0; x < 5; ++x)
          state[x + 5 * y] = b[x + 5 * y] ^ (~b[(x + 1) % 5 + 5 * y] & b[(x + 2) % 5 + 5 * y]);
      state[0] ^= RoundConstants[round];
    }
  }
}

/// <summary>GIMLI-24-HASH lightweight 256-bit hash.</summary>
public static class Gimli24Hash {
  private const int RateBytes = 16;

  /// <summary>
  /// Computes the Gimli-24 Hash hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    Span<uint> state = stackalloc uint[12];
    state.Clear();
    var offset = 0;
    while (offset + RateBytes <= data.Length) {
      for (var i = 0; i < 4; ++i)
        state[i] ^= BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + i * 4, 4));
      Permute(state);
      offset += RateBytes;
    }

    Span<byte> stateBytes = stackalloc byte[48];
    WriteState(state, stateBytes);
    var tail = data[offset..];
    for (var i = 0; i < tail.Length; ++i)
      stateBytes[i] ^= tail[i];
    stateBytes[tail.Length] ^= 0x01;
    stateBytes[47] ^= 0x01;
    ReadState(stateBytes, state);
    Permute(state);

    var result = new byte[32];
    WriteState(state, stateBytes);
    stateBytes[..16].CopyTo(result);
    Permute(state);
    WriteState(state, stateBytes);
    stateBytes[..16].CopyTo(result.AsSpan(16));
    return result;
  }

  private static void Permute(Span<uint> state) {
    for (var round = 24; round > 0; --round) {
      for (var column = 0; column < 4; ++column) {
        var x = BitOperations.RotateLeft(state[column], 24);
        var y = BitOperations.RotateLeft(state[4 + column], 9);
        var z = state[8 + column];
        state[8 + column] = x ^ (z << 1) ^ ((y & z) << 2);
        state[4 + column] = y ^ x ^ ((x | z) << 1);
        state[column] = z ^ y ^ ((x & y) << 3);
      }

      if ((round & 3) == 0) {
        (state[0], state[1]) = (state[1], state[0]);
        (state[2], state[3]) = (state[3], state[2]);
        state[0] ^= 0x9E377900U ^ (uint)round;
      } else if ((round & 3) == 2) {
        (state[0], state[2]) = (state[2], state[0]);
        (state[1], state[3]) = (state[3], state[1]);
      }
    }
  }

  private static void WriteState(ReadOnlySpan<uint> state, Span<byte> bytes) {
    for (var i = 0; i < 12; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(i * 4, 4), state[i]);
  }

  private static void ReadState(ReadOnlySpan<byte> bytes, Span<uint> state) {
    for (var i = 0; i < 12; ++i)
      state[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i * 4, 4));
  }
}

/// <summary>Cipher Hash Construction using the source registry's default AES-128 Matyas-Meyer-Oseas construction.</summary>
public static class ChcHash {
  private const int BlockBytes = 16;

  /// <summary>
  /// Computes the Chc Hash hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    Span<byte> zero = stackalloc byte[BlockBytes];
    zero.Clear();
    var state = Aes128Primitive.EncryptBlock(zero, zero);

    var offset = 0;
    while (offset + BlockBytes <= data.Length) {
      Compress(state, data.Slice(offset, BlockBytes));
      offset += BlockBytes;
    }

    var remaining = data.Length - offset;
    var currentLength = remaining + 1;
    var zeroCount = currentLength > BlockBytes - 8
      ? BlockBytes - currentLength + BlockBytes - 8
      : BlockBytes - 8 - currentLength;
    var padding = new byte[1 + zeroCount + 8];
    padding[0] = 0x80;
    BinaryPrimitives.WriteUInt64LittleEndian(padding.AsSpan(padding.Length - 8), checked((ulong)data.Length * 8));

    Span<byte> finalInput = stackalloc byte[BlockBytes * 2];
    var finalLength = remaining + padding.Length;
    data[offset..].CopyTo(finalInput);
    padding.CopyTo(finalInput[remaining..]);
    for (var i = 0; i < finalLength; i += BlockBytes)
      Compress(state, finalInput.Slice(i, BlockBytes));

    return state;
  }

  private static void Compress(Span<byte> state, ReadOnlySpan<byte> block) {
    var encrypted = Aes128Primitive.EncryptBlock(block, state);
    for (var i = 0; i < BlockBytes; ++i)
      state[i] ^= (byte)(encrypted[i] ^ block[i]);
  }
}

/// <summary>Modification Detection Code 2 (MDC-2), including both OpenSSL padding modes carried by the JavaScript source.</summary>
public static class Mdc2 {
  /// <summary>
  /// Computes the Mdc-2 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data, int paddingType = 1) {
    if (paddingType is not 1 and not 2)
      throw new ArgumentOutOfRangeException(nameof(paddingType), "MDC-2 padding type must be 1 or 2.");

    Span<byte> h = stackalloc byte[8];
    Span<byte> hh = stackalloc byte[8];
    h.Fill(0x52);
    hh.Fill(0x25);

    var offset = 0;
    while (offset + 8 <= data.Length) {
      ProcessBlock(h, hh, data.Slice(offset, 8));
      offset += 8;
    }

    var remaining = data.Length - offset;
    if (remaining > 0 || paddingType == 2) {
      Span<byte> block = stackalloc byte[8];
      block.Clear();
      data[offset..].CopyTo(block);
      if (paddingType == 2)
        block[remaining] = 0x80;
      ProcessBlock(h, hh, block);
    }

    var result = new byte[16];
    h.CopyTo(result);
    hh.CopyTo(result.AsSpan(8));
    return result;
  }

  private static void ProcessBlock(Span<byte> h, Span<byte> hh, ReadOnlySpan<byte> block) {
    Span<byte> key1 = stackalloc byte[8];
    Span<byte> key2 = stackalloc byte[8];
    h.CopyTo(key1);
    hh.CopyTo(key2);
    key1[0] = (byte)((key1[0] & 0x9F) | 0x40);
    key2[0] = (byte)((key2[0] & 0x9F) | 0x20);
    SetOddParity(key1);
    SetOddParity(key2);

    var d = DesPrimitive.EncryptBlock(block, key1);
    var dd = DesPrimitive.EncryptBlock(block, key2);
    Span<byte> nextH = stackalloc byte[8];
    Span<byte> nextHh = stackalloc byte[8];
    for (var i = 0; i < 4; ++i) {
      nextH[i] = (byte)(block[i] ^ d[i]);
      nextHh[i] = (byte)(block[i] ^ dd[i]);
    }
    for (var i = 4; i < 8; ++i) {
      nextH[i] = (byte)(block[i] ^ dd[i]);
      nextHh[i] = (byte)(block[i] ^ d[i]);
    }
    nextH.CopyTo(h);
    nextHh.CopyTo(hh);
  }

  private static void SetOddParity(Span<byte> key) {
    for (var i = 0; i < key.Length; ++i)
      if ((BitOperations.PopCount((uint)key[i]) & 1) == 0)
        key[i] ^= 1;
  }
}
