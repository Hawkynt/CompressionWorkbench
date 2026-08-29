using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Google HighwayHash portable 64-, 128-, and 256-bit keyed hash.</summary>
public static class HighwayHash {
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    HashSizeRange.Exact(64),
    HashSizeRange.Exact(128),
    HashSizeRange.Exact(256)
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, int hashSizeBits = 64) {
    if (key.Length != 32)
      throw new ArgumentException("HighwayHash requires a 256-bit key.", nameof(key));
    if (!SupportedHashSizes.Supports(hashSizeBits))
      throw new ArgumentOutOfRangeException(nameof(hashSizeBits));

    var state = new State(key);
    var offset = 0;
    while (offset + 32 <= data.Length) {
      state.Update(data.Slice(offset, 32));
      offset += 32;
    }
    if (offset != data.Length)
      state.UpdateRemainder(data[offset..]);

    return hashSizeBits switch {
      64 => state.Finalize64(),
      128 => state.Finalize128(),
      256 => state.Finalize256(),
      _ => throw new UnreachableException()
    };
  }

  private sealed class State {
    private readonly ulong[] _v0 = new ulong[4];
    private readonly ulong[] _v1 = new ulong[4];
    private readonly ulong[] _mul0 = [
      0xDBE6D5D5FE4CCE2FUL, 0xA4093822299F31D0UL,
      0x13198A2E03707344UL, 0x243F6A8885A308D3UL
    ];
    private readonly ulong[] _mul1 = [
      0x3BD39E10CB0EF593UL, 0xC0ACF169B5F18A8CUL,
      0xBE5466CF34E90C6CUL, 0x452821E638D01377UL
    ];

    public State(ReadOnlySpan<byte> key) {
      for (var lane = 0; lane < 4; ++lane) {
        var keyLane = BinaryPrimitives.ReadUInt64LittleEndian(key.Slice(lane * 8, 8));
        _v0[lane] = _mul0[lane] ^ keyLane;
        _v1[lane] = _mul1[lane] ^ Rotate64By32(keyLane);
      }
    }

    public void Update(ReadOnlySpan<byte> packet) {
      Span<ulong> lanes = stackalloc ulong[4];
      for (var lane = 0; lane < 4; ++lane)
        lanes[lane] = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(lane * 8, 8));
      Update(lanes);
    }

    public void UpdateRemainder(ReadOnlySpan<byte> bytes) {
      var size = bytes.Length;
      var sizePair = ((ulong)size << 32) + (uint)size;
      for (var lane = 0; lane < 4; ++lane) {
        _v0[lane] = unchecked(_v0[lane] + sizePair);
        var low = BitOperations.RotateLeft((uint)_v1[lane], size);
        var high = BitOperations.RotateLeft((uint)(_v1[lane] >> 32), size);
        _v1[lane] = low | ((ulong)high << 32);
      }

      var sizeMod4 = size & 3;
      var remainderOffset = size & ~3;
      Span<byte> packet = stackalloc byte[32];
      packet.Clear();
      bytes[..remainderOffset].CopyTo(packet);

      if ((size & 16) != 0) {
        var last4 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(size - 4, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(packet[28..], last4);
      } else if (sizeMod4 != 0) {
        var remainder = bytes[remainderOffset..];
        var last3 = (ulong)remainder[0]
          | ((ulong)remainder[sizeMod4 >> 1] << 8)
          | ((ulong)remainder[sizeMod4 - 1] << 16);
        BinaryPrimitives.WriteUInt64LittleEndian(packet[16..], last3);
      }

      Update(packet);
    }

    public byte[] Finalize64() {
      for (var i = 0; i < 4; ++i)
        PermuteAndUpdate();
      var result = new byte[8];
      BinaryPrimitives.WriteUInt64LittleEndian(result, unchecked(_v0[0] + _v1[0] + _mul0[0] + _mul1[0]));
      return result;
    }

    public byte[] Finalize128() {
      for (var i = 0; i < 6; ++i)
        PermuteAndUpdate();
      var result = new byte[16];
      BinaryPrimitives.WriteUInt64LittleEndian(result, unchecked(_v0[0] + _mul0[0] + _v1[2] + _mul1[2]));
      BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), unchecked(_v0[1] + _mul0[1] + _v1[3] + _mul1[3]));
      return result;
    }

    public byte[] Finalize256() {
      for (var i = 0; i < 10; ++i)
        PermuteAndUpdate();

      ModularReduction(
        unchecked(_v1[1] + _mul1[1]), unchecked(_v1[0] + _mul1[0]),
        unchecked(_v0[1] + _mul0[1]), unchecked(_v0[0] + _mul0[0]),
        out var r1, out var r0);
      ModularReduction(
        unchecked(_v1[3] + _mul1[3]), unchecked(_v1[2] + _mul1[2]),
        unchecked(_v0[3] + _mul0[3]), unchecked(_v0[2] + _mul0[2]),
        out var r3, out var r2);

      var result = new byte[32];
      BinaryPrimitives.WriteUInt64LittleEndian(result, r0);
      BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), r1);
      BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), r2);
      BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), r3);
      return result;
    }

    private void Update(ReadOnlySpan<ulong> packet) {
      unchecked {
        for (var lane = 0; lane < 4; ++lane)
          _v1[lane] += packet[lane] + _mul0[lane];

        for (var lane = 0; lane < 4; ++lane) {
          _mul0[lane] ^= (uint)_v1[lane] * (_v0[lane] >> 32);
          _v0[lane] += _mul1[lane];
          _mul1[lane] ^= (uint)_v0[lane] * (_v1[lane] >> 32);
        }

        ZipperMergeAndAdd(_v1[1], _v1[0], ref _v0[1], ref _v0[0]);
        ZipperMergeAndAdd(_v1[3], _v1[2], ref _v0[3], ref _v0[2]);
        ZipperMergeAndAdd(_v0[1], _v0[0], ref _v1[1], ref _v1[0]);
        ZipperMergeAndAdd(_v0[3], _v0[2], ref _v1[3], ref _v1[2]);
      }
    }

    private void PermuteAndUpdate() {
      Span<ulong> permuted = stackalloc ulong[4];
      permuted[0] = Rotate64By32(_v0[2]);
      permuted[1] = Rotate64By32(_v0[3]);
      permuted[2] = Rotate64By32(_v0[0]);
      permuted[3] = Rotate64By32(_v0[1]);
      Update(permuted);
    }
  }

  private static ulong Rotate64By32(ulong value) => (value >> 32) | (value << 32);

  private static ulong MaskByte(ulong value, int index) => value & (0xFFUL << (index * 8));

  private static void ZipperMergeAndAdd(ulong v1, ulong v0, ref ulong add1, ref ulong add0) {
    unchecked {
      add0 += ((MaskByte(v0, 3) + MaskByte(v1, 4)) >> 24)
        + ((MaskByte(v0, 5) + MaskByte(v1, 6)) >> 16)
        + MaskByte(v0, 2)
        + (MaskByte(v0, 1) << 32)
        + (MaskByte(v1, 7) >> 8)
        + (v0 << 56);
      add1 += ((MaskByte(v1, 3) + MaskByte(v0, 4)) >> 24)
        + MaskByte(v1, 2)
        + (MaskByte(v1, 5) >> 16)
        + (MaskByte(v1, 1) << 24)
        + (MaskByte(v0, 6) >> 8)
        + (MaskByte(v1, 0) << 48)
        + MaskByte(v0, 7);
    }
  }

  private static void ModularReduction(ulong a3Unmasked, ulong a2, ulong a1, ulong a0, out ulong m1, out ulong m0) {
    var a3 = a3Unmasked & 0x3FFFFFFFFFFFFFFFUL;
    var a3Shift1 = a3;
    var a2Shift1 = a2;
    var a3Shift2 = a3;
    var a2Shift2 = a2;
    Shift128Left(ref a3Shift1, ref a2Shift1, 1);
    Shift128Left(ref a3Shift2, ref a2Shift2, 2);
    m1 = a1 ^ a3Shift1 ^ a3Shift2;
    m0 = a0 ^ a2Shift1 ^ a2Shift2;
  }

  private static void Shift128Left(ref ulong high, ref ulong low, int bits) {
    var shiftedHigh = high << bits;
    var topBits = low >> (64 - bits);
    low <<= bits;
    high = shiftedHigh | topBits;
  }
}
