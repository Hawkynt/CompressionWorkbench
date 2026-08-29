using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Skein-512-512 variant used by the DarkCrypt Total Commander plugin.</summary>
/// <remarks>
/// Preserves DarkCrypt's October-2008 Threefish-512 rotation schedule and its non-standard
/// 0x5555555555555555 key-parity constant. The UBI/configuration/output pipeline otherwise
/// follows Skein-512-512.
/// </remarks>
public static class DarkCryptSkein {
  private const int BlockBytes = 64;
  private const ulong ParityConstant = 0x5555555555555555UL;
  private const int TypeConfig = 4;
  private const int TypeMessage = 48;
  private const int TypeOutput = 63;
  private const ulong FirstFlag = 1UL << 62;
  private const ulong FinalFlag = 1UL << 63;

  private static readonly int[,] Rotations = {
    {38,30,50,53},
    {48,20,43,31},
    {34,14,15,27},
    {26,12,58,7},
    {33,49,8,42},
    {39,27,41,14},
    {29,26,11,9},
    {33,51,39,35}
  };

  public static byte[] Compute(ReadOnlySpan<byte> data) {
    var chain = new ulong[8];
    var config = new byte[32];
    config[0] = (byte)'S';
    config[1] = (byte)'H';
    config[2] = (byte)'A';
    config[3] = (byte)'3';
    config[4] = 1;
    BinaryPrimitives.WriteUInt64LittleEndian(config.AsSpan(8, 8), 512);

    var ubi = new Ubi();
    ubi.Reset(TypeConfig);
    ubi.Update(config, chain);
    ubi.Finalize(chain);

    ubi.Reset(TypeMessage);
    ubi.Update(data, chain);
    ubi.Finalize(chain);

    Span<byte> counter = stackalloc byte[8];
    counter.Clear();
    ubi.Reset(TypeOutput);
    ubi.Update(counter, chain);
    var outputWords = chain.ToArray();
    ubi.Finalize(outputWords);

    var result = new byte[64];
    for (var i = 0; i < 8; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(i * 8, 8), outputWords[i]);
    return result;
  }

  private static ulong[] EncryptThreefish512(ReadOnlySpan<ulong> key, ReadOnlySpan<ulong> tweak, ReadOnlySpan<ulong> block) {
    Span<ulong> kw = stackalloc ulong[17];
    var parity = ParityConstant;
    for (var i = 0; i < 8; ++i) {
      kw[i] = key[i];
      parity ^= key[i];
    }
    kw[8] = parity;
    for (var i = 0; i < 8; ++i)
      kw[9 + i] = kw[i];

    Span<ulong> t = stackalloc ulong[5];
    t[0] = tweak[0];
    t[1] = tweak[1];
    t[2] = t[0] ^ t[1];
    t[3] = t[0];
    t[4] = t[1];

    var b0 = unchecked(block[0] + kw[0]);
    var b1 = unchecked(block[1] + kw[1]);
    var b2 = unchecked(block[2] + kw[2]);
    var b3 = unchecked(block[3] + kw[3]);
    var b4 = unchecked(block[4] + kw[4]);
    var b5 = unchecked(block[5] + kw[5] + t[0]);
    var b6 = unchecked(block[6] + kw[6] + t[1]);
    var b7 = unchecked(block[7] + kw[7]);

    for (var d = 1; d < 18; d += 2) {
      Mix(ref b0, ref b1, Rotations[0, 0]);
      Mix(ref b2, ref b3, Rotations[0, 1]);
      Mix(ref b4, ref b5, Rotations[0, 2]);
      Mix(ref b6, ref b7, Rotations[0, 3]);

      Mix(ref b2, ref b1, Rotations[1, 0]);
      Mix(ref b4, ref b7, Rotations[1, 1]);
      Mix(ref b6, ref b5, Rotations[1, 2]);
      Mix(ref b0, ref b3, Rotations[1, 3]);

      Mix(ref b4, ref b1, Rotations[2, 0]);
      Mix(ref b6, ref b3, Rotations[2, 1]);
      Mix(ref b0, ref b5, Rotations[2, 2]);
      Mix(ref b2, ref b7, Rotations[2, 3]);

      Mix(ref b6, ref b1, Rotations[3, 0]);
      Mix(ref b0, ref b7, Rotations[3, 1]);
      Mix(ref b2, ref b5, Rotations[3, 2]);
      Mix(ref b4, ref b3, Rotations[3, 3]);

      InjectSubkey(ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, kw, t, d);

      Mix(ref b0, ref b1, Rotations[4, 0]);
      Mix(ref b2, ref b3, Rotations[4, 1]);
      Mix(ref b4, ref b5, Rotations[4, 2]);
      Mix(ref b6, ref b7, Rotations[4, 3]);

      Mix(ref b2, ref b1, Rotations[5, 0]);
      Mix(ref b4, ref b7, Rotations[5, 1]);
      Mix(ref b6, ref b5, Rotations[5, 2]);
      Mix(ref b0, ref b3, Rotations[5, 3]);

      Mix(ref b4, ref b1, Rotations[6, 0]);
      Mix(ref b6, ref b3, Rotations[6, 1]);
      Mix(ref b0, ref b5, Rotations[6, 2]);
      Mix(ref b2, ref b7, Rotations[6, 3]);

      Mix(ref b6, ref b1, Rotations[7, 0]);
      Mix(ref b0, ref b7, Rotations[7, 1]);
      Mix(ref b2, ref b5, Rotations[7, 2]);
      Mix(ref b4, ref b3, Rotations[7, 3]);

      InjectSubkey(ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, kw, t, d + 1);
    }

    return [b0,b1,b2,b3,b4,b5,b6,b7];
  }

  private static void Mix(ref ulong even, ref ulong odd, int rotation) {
    even = unchecked(even + odd);
    odd = BitOperations.RotateLeft(odd, rotation) ^ even;
  }

  private static void InjectSubkey(
    ref ulong b0, ref ulong b1, ref ulong b2, ref ulong b3,
    ref ulong b4, ref ulong b5, ref ulong b6, ref ulong b7,
    ReadOnlySpan<ulong> key, ReadOnlySpan<ulong> tweak, int subkey
  ) {
    var mod9 = subkey % 9;
    var mod3 = subkey % 3;
    b0 = unchecked(b0 + key[mod9]);
    b1 = unchecked(b1 + key[mod9 + 1]);
    b2 = unchecked(b2 + key[mod9 + 2]);
    b3 = unchecked(b3 + key[mod9 + 3]);
    b4 = unchecked(b4 + key[mod9 + 4]);
    b5 = unchecked(b5 + key[mod9 + 5] + tweak[mod3]);
    b6 = unchecked(b6 + key[mod9 + 6] + tweak[mod3 + 1]);
    b7 = unchecked(b7 + key[mod9 + 7] + (ulong)subkey);
  }

  private sealed class Ubi {
    private readonly byte[] _block = new byte[BlockBytes];
    private int _offset;
    private ulong _position;
    private ulong _tweak1;

    public void Reset(int type) {
      _position = 0;
      _tweak1 = ((ulong)type << 56) | FirstFlag;
      _offset = 0;
      Array.Clear(_block);
    }

    public void Update(ReadOnlySpan<byte> data, ulong[] chain) {
      var source = 0;
      while (source < data.Length) {
        if (_offset == BlockBytes) {
          ProcessBlock(chain);
          _tweak1 &= ~FirstFlag;
          _offset = 0;
          Array.Clear(_block);
        }
        var take = Math.Min(data.Length - source, BlockBytes - _offset);
        data.Slice(source, take).CopyTo(_block.AsSpan(_offset));
        source += take;
        _offset += take;
        _position = unchecked(_position + (ulong)take);
      }
    }

    public void Finalize(ulong[] chain) {
      _block.AsSpan(_offset).Clear();
      _tweak1 |= FinalFlag;
      ProcessBlock(chain);
    }

    private void ProcessBlock(ulong[] chain) {
      Span<ulong> message = stackalloc ulong[8];
      for (var i = 0; i < 8; ++i)
        message[i] = BinaryPrimitives.ReadUInt64LittleEndian(_block.AsSpan(i * 8, 8));
      Span<ulong> tweak = stackalloc ulong[2];
      tweak[0] = _position;
      tweak[1] = _tweak1;
      var encrypted = EncryptThreefish512(chain, tweak, message);
      for (var i = 0; i < 8; ++i)
        chain[i] = encrypted[i] ^ message[i];
    }
  }
}
