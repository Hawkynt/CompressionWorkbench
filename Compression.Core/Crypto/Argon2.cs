using System.Buffers.Binary;
using System.Numerics;
using Compression.Core.Checksums;

namespace Compression.Core.Crypto;

/// <summary>
/// Argon2 (version 0x13) memory-hard key derivation, implemented from RFC 9106 and checked against
/// its test vectors. Blake2b comes from <see cref="Blake2b"/>.
/// </summary>
public static class Argon2 {
  public enum Variant { Argon2d = 0, Argon2i = 1, Argon2id = 2 }

  private const int BlockWords = 128;   // 1024-byte blocks of 64-bit words
  private const int SyncPoints = 4;
  private const uint Version = 0x13;

  /// <param name="memoryKiB">Memory cost m in KiB (1 KiB blocks); at least 8 × lanes.</param>
  public static byte[] Hash(Variant type, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int memoryKiB,
      int lanes, int tagLength, ReadOnlySpan<byte> secret = default, ReadOnlySpan<byte> associated = default) {
    if (lanes < 1 || iterations < 1 || tagLength < 4 || memoryKiB < 8 * lanes)
      throw new ArgumentOutOfRangeException(nameof(memoryKiB), "Argon2 needs t ≥ 1, p ≥ 1, m ≥ 8p KiB and a tag of at least 4 bytes.");

    // H0
    var h0 = new Blake2b(64);
    var word = new byte[4];
    void Le(Blake2b h, int v) { BinaryPrimitives.WriteInt32LittleEndian(word, v); h.Update(word); }
    Le(h0, lanes); Le(h0, tagLength); Le(h0, memoryKiB); Le(h0, iterations); Le(h0, (int)Version); Le(h0, (int)type);
    Le(h0, password.Length); h0.Update(password);
    Le(h0, salt.Length); h0.Update(salt);
    Le(h0, secret.Length); h0.Update(secret);
    Le(h0, associated.Length); h0.Update(associated);
    var seed = new byte[72];
    h0.Finish().CopyTo(seed, 0);

    var segmentLength = memoryKiB / (SyncPoints * lanes);
    var laneLength = segmentLength * SyncPoints;
    var blockCount = laneLength * lanes;
    var memory = new ulong[blockCount * BlockWords];

    var bytes = new byte[1024];
    for (var l = 0; l < lanes; ++l)
      for (var k = 0; k < 2; ++k) {
        BinaryPrimitives.WriteInt32LittleEndian(seed.AsSpan(64), k);
        BinaryPrimitives.WriteInt32LittleEndian(seed.AsSpan(68), l);
        VariableHash(seed, bytes);
        LoadBlock(bytes, memory.AsSpan((l * laneLength + k) * BlockWords, BlockWords));
      }

    var zero = new ulong[BlockWords];
    var input = new ulong[BlockWords];
    var address = new ulong[BlockWords];
    var scratch = new ulong[BlockWords];
    for (var pass = 0; pass < iterations; ++pass)
      for (var slice = 0; slice < SyncPoints; ++slice)
        for (var lane = 0; lane < lanes; ++lane) {
          var independent = type == Variant.Argon2i || (type == Variant.Argon2id && pass == 0 && slice < SyncPoints / 2);
          if (independent) {
            Array.Clear(input);
            input[0] = (ulong)pass; input[1] = (ulong)lane; input[2] = (ulong)slice;
            input[3] = (ulong)blockCount; input[4] = (ulong)iterations; input[5] = (ulong)type;
          }
          var start = 0;
          if (pass == 0 && slice == 0) {
            start = 2;
            if (independent) NextAddresses(address, input, zero, scratch);
          }
          var current = lane * laneLength + slice * segmentLength + start;
          var previous = current % laneLength == 0 ? current + laneLength - 1 : current - 1;
          for (var i = start; i < segmentLength; ++i, ++current, ++previous) {
            if (current % laneLength == 1) previous = current - 1;
            ulong pseudo;
            if (independent) {
              if (i % BlockWords == 0) NextAddresses(address, input, zero, scratch);
              pseudo = address[i % BlockWords];
            } else {
              pseudo = memory[previous * BlockWords];
            }
            var refLane = (int)((pseudo >> 32) % (ulong)lanes);
            if (pass == 0 && slice == 0) refLane = lane;
            var refIndex = IndexAlpha(pass, slice, i, refLane == lane, (uint)pseudo, segmentLength, laneLength);
            FillBlock(memory.AsSpan(previous * BlockWords, BlockWords),
              memory.AsSpan((refLane * laneLength + refIndex) * BlockWords, BlockWords),
              memory.AsSpan(current * BlockWords, BlockWords), pass != 0, scratch);
          }
        }

    var final = new ulong[BlockWords];
    for (var l = 0; l < lanes; ++l) {
      var last = memory.AsSpan((l * laneLength + laneLength - 1) * BlockWords, BlockWords);
      for (var w = 0; w < BlockWords; ++w) final[w] ^= last[w];
    }
    StoreBlock(final, bytes);
    var tag = new byte[tagLength];
    VariableHash(bytes, tag);
    Array.Clear(memory);
    return tag;
  }

  private static int IndexAlpha(int pass, int slice, int index, bool sameLane, uint pseudo, int segmentLength, int laneLength) {
    long area;
    if (pass == 0) {
      if (slice == 0) area = index - 1;
      else if (sameLane) area = (long)slice * segmentLength + index - 1;
      else area = (long)slice * segmentLength + (index == 0 ? -1 : 0);
    } else {
      area = sameLane ? laneLength - segmentLength + index - 1 : laneLength - segmentLength + (index == 0 ? -1 : 0);
    }
    var x = (ulong)pseudo * pseudo >> 32;
    var relative = (ulong)area - 1 - ((ulong)area * x >> 32);
    var startPosition = pass == 0 || slice == SyncPoints - 1 ? 0 : (slice + 1) * segmentLength;
    return (int)(((ulong)startPosition + relative) % (ulong)laneLength);
  }

  private static void NextAddresses(ulong[] address, ulong[] input, ulong[] zero, ulong[] scratch) {
    ++input[6];
    FillBlock(zero, input, address, false, scratch);
    FillBlock(zero, address, address, false, scratch);
  }

  /// <summary>next = G(prev, ref) (XORed into next when <paramref name="xor"/>).</summary>
  private static void FillBlock(ReadOnlySpan<ulong> prev, ReadOnlySpan<ulong> reference, Span<ulong> next, bool xor, ulong[] r) {
    Span<ulong> tmp = stackalloc ulong[BlockWords];
    for (var w = 0; w < BlockWords; ++w) {
      r[w] = prev[w] ^ reference[w];
      tmp[w] = xor ? r[w] ^ next[w] : r[w];
    }
    for (var i = 0; i < 8; ++i)
      Permute(r, 16 * i, 16 * i + 1, 16 * i + 2, 16 * i + 3, 16 * i + 4, 16 * i + 5, 16 * i + 6, 16 * i + 7,
        16 * i + 8, 16 * i + 9, 16 * i + 10, 16 * i + 11, 16 * i + 12, 16 * i + 13, 16 * i + 14, 16 * i + 15);
    for (var i = 0; i < 8; ++i)
      Permute(r, 2 * i, 2 * i + 1, 2 * i + 16, 2 * i + 17, 2 * i + 32, 2 * i + 33, 2 * i + 48, 2 * i + 49,
        2 * i + 64, 2 * i + 65, 2 * i + 80, 2 * i + 81, 2 * i + 96, 2 * i + 97, 2 * i + 112, 2 * i + 113);
    for (var w = 0; w < BlockWords; ++w) next[w] = tmp[w] ^ r[w];
  }

  private static void Permute(ulong[] v, int v0, int v1, int v2, int v3, int v4, int v5, int v6, int v7,
      int v8, int v9, int v10, int v11, int v12, int v13, int v14, int v15) {
    G(v, v0, v4, v8, v12); G(v, v1, v5, v9, v13); G(v, v2, v6, v10, v14); G(v, v3, v7, v11, v15);
    G(v, v0, v5, v10, v15); G(v, v1, v6, v11, v12); G(v, v2, v7, v8, v13); G(v, v3, v4, v9, v14);
  }

  private static ulong BlaMka(ulong x, ulong y) => x + y + 2 * (x & 0xFFFFFFFF) * (y & 0xFFFFFFFF);

  private static void G(ulong[] v, int a, int b, int c, int d) {
    v[a] = BlaMka(v[a], v[b]); v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
    v[c] = BlaMka(v[c], v[d]); v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
    v[a] = BlaMka(v[a], v[b]); v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
    v[c] = BlaMka(v[c], v[d]); v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
  }

  /// <summary>H′ (RFC 9106 §3.3): Blake2b-based hash of arbitrary output length.</summary>
  private static void VariableHash(ReadOnlySpan<byte> input, Span<byte> output) {
    Span<byte> length = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(length, output.Length);
    if (output.Length <= 64) {
      var h = new Blake2b(output.Length);
      h.Update(length);
      h.Update(input);
      h.Finish().CopyTo(output);
      return;
    }
    var first = new Blake2b(64);
    first.Update(length);
    first.Update(input);
    var v = first.Finish();
    var r = (output.Length + 31) / 32 - 2;
    var o = 0;
    v.AsSpan(0, 32).CopyTo(output);
    o += 32;
    for (var i = 2; i <= r; ++i) {
      v = Blake2b.Compute(v, 64);
      v.AsSpan(0, 32).CopyTo(output[o..]);
      o += 32;
    }
    Blake2b.Compute(v, output.Length - 32 * r).CopyTo(output[o..]);
  }

  private static void LoadBlock(ReadOnlySpan<byte> bytes, Span<ulong> block) {
    for (var w = 0; w < BlockWords; ++w) block[w] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(8 * w)..]);
  }

  private static void StoreBlock(ReadOnlySpan<ulong> block, Span<byte> bytes) {
    for (var w = 0; w < BlockWords; ++w) BinaryPrimitives.WriteUInt64LittleEndian(bytes[(8 * w)..], block[w]);
  }
}
