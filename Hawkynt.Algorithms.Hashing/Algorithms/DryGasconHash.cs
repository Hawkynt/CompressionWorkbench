using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>DryGASCON hash family from the NIST lightweight-cryptography finalist.</summary>
public static class DryGasconHash {
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    HashSizeRange.Exact(256),
    HashSizeRange.Exact(512)
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 256) => hashSizeBits switch {
    256 => Compute128(data),
    512 => Compute256(data),
    _ => throw new ArgumentOutOfRangeException(nameof(hashSizeBits))
  };

  private static byte[] Compute128(ReadOnlySpan<byte> data) {
    var state = new DrySponge128();
    const uint associatedData = 2U << 10;
    const uint final = 1U << 9;
    const uint padded = 1U << 8;

    if (data.IsEmpty) {
      state.Domain = associatedData | final | padded;
      state.AbsorbAndSqueeze([]);
    } else {
      var offset = 0;
      while (data.Length - offset > 16) {
        state.AbsorbAndSqueeze(data.Slice(offset, 16));
        offset += 16;
      }

      var last = data[offset..];
      state.Domain = associatedData | final | (last.Length < 16 ? padded : 0);
      state.AbsorbAndSqueeze(last);
    }

    var result = new byte[32];
    state.Output.CopyTo(result, 0);
    state.Squeeze();
    state.Output.CopyTo(result, 16);
    return result;
  }

  private static byte[] Compute256(ReadOnlySpan<byte> data) {
    var state = new DrySponge256();
    const uint associatedData = 2U << 4;
    const uint final = 1U << 3;
    const uint padded = 1U << 2;

    if (data.IsEmpty) {
      state.Domain = associatedData | final | padded;
      state.Absorb([]);
      state.Squeeze();
    } else {
      var offset = 0;
      while (data.Length - offset > 16) {
        state.Absorb(data.Slice(offset, 16));
        state.PermuteOnly();
        offset += 16;
      }

      var last = data[offset..];
      state.Domain = associatedData | final | (last.Length < 16 ? padded : 0);
      state.Absorb(last);
      state.Squeeze();
    }

    var result = new byte[64];
    for (var block = 0; block < 4; ++block) {
      if (block != 0)
        state.Squeeze();
      state.Output.CopyTo(result, block * 16);
    }
    return result;
  }

  private sealed class DrySponge128 {
    private static readonly byte[] Initial = Convert.FromHexString(
      "243F6A8885A308D313198A2E03707344" +
      "243F6A8885A308D313198A2E03707344" +
      "243F6A8885A308D3" +
      "A4093822299F31D0082EFA98EC4E6C89");

    private readonly uint[] _state = new uint[10];
    private readonly byte[] _x = new byte[16];
    public byte[] Output { get; } = new byte[16];
    public uint Domain { get; set; }

    public DrySponge128() {
      for (var lane = 0; lane < 5; ++lane) {
        _state[lane * 2] = BinaryPrimitives.ReadUInt32LittleEndian(Initial.AsSpan(lane * 8, 4));
        _state[lane * 2 + 1] = BinaryPrimitives.ReadUInt32LittleEndian(Initial.AsSpan(lane * 8 + 4, 4));
      }
      Initial.AsSpan(40, 16).CopyTo(_x);
    }

    public void AbsorbAndSqueeze(ReadOnlySpan<byte> input) {
      Span<byte> block = stackalloc byte[16];
      block.Clear();
      input.CopyTo(block);
      if (input.Length < block.Length)
        block[input.Length] = 0x01;
      Mix(block);
      Squeeze();
      Domain = 0;
    }

    private uint SelectX(uint index) => BinaryPrimitives.ReadUInt32LittleEndian(_x.AsSpan(checked((int)index) * 4, 4));

    private void Mix(ReadOnlySpan<byte> data) {
      var ds = Domain;
      Span<uint> values = stackalloc uint[14];
      values[0] = (uint)(data[0] | data[1] << 8);
      values[1] = (uint)(data[1] >> 2 | data[2] << 6);
      values[2] = (uint)(data[2] >> 4 | data[3] << 4);
      values[3] = (uint)(data[3] >> 6 | data[4] << 2);
      values[4] = (uint)(data[5] | data[6] << 8);
      values[5] = (uint)(data[6] >> 2 | data[7] << 6);
      values[6] = (uint)(data[7] >> 4 | data[8] << 4);
      values[7] = (uint)(data[8] >> 6 | data[9] << 2);
      values[8] = (uint)(data[10] | data[11] << 8);
      values[9] = (uint)(data[11] >> 2 | data[12] << 6);
      values[10] = (uint)(data[12] >> 4 | data[13] << 4);
      values[11] = (uint)(data[13] >> 6 | data[14] << 2);
      values[12] = data[15] ^ ds;
      values[13] = ds >> 10;

      for (var i = 0; i < 13; ++i) {
        MixRound(values[i] & 0x3FF);
        Gascon128Round(_state, 0);
      }
      MixRound(values[13] & 0x3FF);
    }

    private void MixRound(uint value) {
      for (var lane = 0; lane < 5; ++lane)
        _state[lane * 2] ^= SelectX((value >> (lane * 2)) & 3);
    }

    public void Squeeze() {
      Output.AsSpan().Clear();
      Span<byte> block = stackalloc byte[16];
      for (var round = 0; round < 7; ++round) {
        Gascon128Round(_state, round);
        BinaryPrimitives.WriteUInt32LittleEndian(block, _state[0] ^ _state[5]);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], _state[1] ^ _state[6]);
        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], _state[2] ^ _state[7]);
        BinaryPrimitives.WriteUInt32LittleEndian(block[12..], _state[3] ^ _state[4]);
        if (round == 0)
          block.CopyTo(Output);
        else
          for (var i = 0; i < Output.Length; ++i)
            Output[i] ^= block[i];
      }
    }
  }

  private sealed class DrySponge256 {
    private static readonly byte[] Initial = Convert.FromHexString(
      "243F6A8885A308D313198A2E03707344" +
      "A4093822299F31D0082EFA98EC4E6C89" +
      "243F6A8885A308D313198A2E03707344" +
      "A4093822299F31D0082EFA98EC4E6C89" +
      "243F6A8885A308D3" +
      "452821E638D01377BE5466CF34E90C6C");

    private readonly uint[] _state = new uint[18];
    private readonly byte[] _x = new byte[16];
    public byte[] Output { get; } = new byte[16];
    public uint Domain { get; set; }

    public DrySponge256() {
      for (var lane = 0; lane < 9; ++lane) {
        _state[lane * 2] = BinaryPrimitives.ReadUInt32LittleEndian(Initial.AsSpan(lane * 8, 4));
        _state[lane * 2 + 1] = BinaryPrimitives.ReadUInt32LittleEndian(Initial.AsSpan(lane * 8 + 4, 4));
      }
      Initial.AsSpan(72, 16).CopyTo(_x);
    }

    private uint SelectX(uint index) => BinaryPrimitives.ReadUInt32LittleEndian(_x.AsSpan(checked((int)index) * 4, 4));

    public void Absorb(ReadOnlySpan<byte> input) {
      Span<byte> block = stackalloc byte[16];
      block.Clear();
      input.CopyTo(block);
      if (input.Length < block.Length)
        block[input.Length] = 0x01;
      Mix(block);
    }

    private void Mix(ReadOnlySpan<byte> data) {
      Span<uint> values = stackalloc uint[7];
      values[0] = (uint)(data[0] | data[1] << 8 | data[2] << 16);
      values[1] = (uint)(data[2] >> 2 | data[3] << 6 | data[4] << 14);
      values[2] = (uint)(data[4] >> 4 | data[5] << 4 | data[6] << 12);
      values[3] = (uint)(data[6] >> 6 | data[7] << 2 | data[8] << 10);
      values[4] = (uint)(data[9] | data[10] << 8 | data[11] << 16);
      values[5] = (uint)(data[11] >> 2 | data[12] << 6 | data[13] << 14);
      values[6] = (uint)(data[13] >> 4 | data[14] << 4 | data[15] << 12);
      foreach (var value in values) {
        MixRound(value);
        Gascon256Round(_state, 0);
      }
      MixRound((data[15] >> 6) ^ Domain);
      Domain = 0;
    }

    private void MixRound(uint value) {
      for (var lane = 0; lane < 9; ++lane)
        _state[lane * 2] ^= SelectX((value >> (lane * 2)) & 3);
    }

    public void PermuteOnly() {
      for (var round = 0; round < 8; ++round)
        Gascon256Round(_state, round);
    }

    public void Squeeze() {
      Output.AsSpan().Clear();
      Span<byte> block = stackalloc byte[16];
      for (var round = 0; round < 8; ++round) {
        Gascon256Round(_state, round);
        BinaryPrimitives.WriteUInt32LittleEndian(block, _state[0] ^ _state[5] ^ _state[10] ^ _state[15]);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], _state[1] ^ _state[6] ^ _state[11] ^ _state[12]);
        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], _state[2] ^ _state[7] ^ _state[8] ^ _state[13]);
        BinaryPrimitives.WriteUInt32LittleEndian(block[12..], _state[3] ^ _state[4] ^ _state[9] ^ _state[14]);
        if (round == 0)
          block.CopyTo(Output);
        else
          for (var i = 0; i < Output.Length; ++i)
            Output[i] ^= block[i];
      }
    }
  }

  private static void Gascon128Round(Span<uint> state, int round) {
    state[4] ^= (uint)(((0x0F - round) << 4) | round);
    Substitute(state, 5, 2);
    Diffuse(state, 0, true, 9, false, 14);
    Diffuse(state, 1, true, 30, false, 19);
    Diffuse(state, 2, true, 0, false, 3);
    Diffuse(state, 3, false, 5, true, 8);
    Diffuse(state, 4, true, 3, false, 20);
  }

  private static void Gascon256Round(Span<uint> state, int round) {
    state[8] ^= (uint)(((0x0F - round) << 4) | round);
    Substitute(state, 9, 4);
    Diffuse(state, 0, true, 9, false, 14);
    Diffuse(state, 1, true, 30, false, 19);
    Diffuse(state, 2, true, 0, false, 3);
    Diffuse(state, 3, false, 5, true, 8);
    Diffuse(state, 4, true, 3, false, 20);
    Diffuse(state, 5, true, 15, false, 13);
    Diffuse(state, 6, true, 26, false, 29);
    Diffuse(state, 7, true, 4, false, 23);
    Diffuse(state, 8, true, 21, false, 25);
  }

  private static void Substitute(Span<uint> state, int lanes, int invertLane) {
    Span<uint> x = stackalloc uint[9];
    Span<uint> t = stackalloc uint[9];
    for (var half = 0; half < 2; ++half) {
      for (var i = 0; i < lanes; ++i)
        x[i] = state[i * 2 + half];

      if (lanes == 5) {
        x[0] ^= x[4]; x[2] ^= x[1]; x[4] ^= x[3];
      } else {
        x[0] ^= x[8]; x[2] ^= x[1]; x[4] ^= x[3]; x[6] ^= x[5]; x[8] ^= x[7];
      }

      for (var i = 0; i < lanes; ++i)
        t[i] = ~x[i] & x[(i + 1) % lanes];
      for (var i = 0; i < lanes; ++i)
        x[i] ^= t[(i + 1) % lanes];

      if (lanes == 5) {
        x[1] ^= x[0]; x[3] ^= x[2]; x[0] ^= x[4];
      } else {
        x[1] ^= x[0]; x[3] ^= x[2]; x[5] ^= x[4]; x[7] ^= x[6]; x[0] ^= x[8];
      }
      x[invertLane] = ~x[invertLane];

      for (var i = 0; i < lanes; ++i)
        state[i * 2 + half] = x[i];
    }
  }

  private static void Diffuse(Span<uint> state, int lane, bool firstOdd, int firstBits, bool secondOdd, int secondBits) {
    var low = state[lane * 2];
    var high = state[lane * 2 + 1];
    var first = firstOdd ? RotateOdd(low, high, firstBits) : RotateEven(low, high, firstBits);
    var second = secondOdd ? RotateOdd(low, high, secondBits) : RotateEven(low, high, secondBits);
    state[lane * 2] = low ^ first.Low ^ second.Low;
    state[lane * 2 + 1] = high ^ first.High ^ second.High;
  }

  private static (uint Low, uint High) RotateEven(uint low, uint high, int bits) {
    bits &= 63;
    if (bits < 32)
      return (BitOperations.RotateRight(low, bits), BitOperations.RotateRight(high, bits));
    bits -= 32;
    return (BitOperations.RotateRight(high, bits), BitOperations.RotateRight(low, bits));
  }

  private static (uint Low, uint High) RotateOdd(uint low, uint high, int bits) =>
    (BitOperations.RotateRight(high, bits), BitOperations.RotateRight(low, (bits + 1) & 31));
}
