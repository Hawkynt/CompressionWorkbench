using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Skein-512-512 from the Skein 1.3 specification.</summary>
public static class Skein512 {
  private const int BlockBytes = 64;
  private const ulong ParityConstant = 0x1BD11BDAA9FC1A22UL;
  private const int TypeMessage = 48;
  private const int TypeOutput = 63;
  private const ulong FirstFlag = 1UL << 62;
  private const ulong FinalFlag = 1UL << 63;

  private static readonly ulong[] InitialState = [
    0x4903ADFF749C51CEUL,0x0D95DE399746DF03UL,0x8FD1934127C79BCEUL,0x9A255629FF352CB1UL,
    0x5DB62599DF6CA7B0UL,0xEABE394CA9D5C3F4UL,0x991112C71A75B523UL,0xAE18A40B660FCC33UL
  ];

  private static readonly int[,] Rotations = {
    {46,36,19,37},{33,27,14,42},{17,49,36,39},{44,9,54,56},
    {39,30,34,24},{13,50,10,17},{25,29,39,43},{8,35,56,22}
  };

  public static byte[] Compute(ReadOnlySpan<byte> data) {
    var chain = InitialState.ToArray();
    var ubi = new Ubi();
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

  private static ulong[] Encrypt(ReadOnlySpan<ulong> key, ReadOnlySpan<ulong> tweak, ReadOnlySpan<ulong> block) {
    Span<ulong> kw = stackalloc ulong[17];
    var parity = ParityConstant;
    for (var i = 0; i < 8; ++i) { kw[i] = key[i]; parity ^= key[i]; }
    kw[8] = parity;
    for (var i = 0; i < 8; ++i) kw[9 + i] = kw[i];

    Span<ulong> t = stackalloc ulong[5];
    t[0]=tweak[0]; t[1]=tweak[1]; t[2]=t[0]^t[1]; t[3]=t[0]; t[4]=t[1];

    var b0=unchecked(block[0]+kw[0]); var b1=unchecked(block[1]+kw[1]);
    var b2=unchecked(block[2]+kw[2]); var b3=unchecked(block[3]+kw[3]);
    var b4=unchecked(block[4]+kw[4]); var b5=unchecked(block[5]+kw[5]+t[0]);
    var b6=unchecked(block[6]+kw[6]+t[1]); var b7=unchecked(block[7]+kw[7]);

    for (var d=1; d<18; d+=2) {
      Mix(ref b0,ref b1,Rotations[0,0]); Mix(ref b2,ref b3,Rotations[0,1]); Mix(ref b4,ref b5,Rotations[0,2]); Mix(ref b6,ref b7,Rotations[0,3]);
      Mix(ref b2,ref b1,Rotations[1,0]); Mix(ref b4,ref b7,Rotations[1,1]); Mix(ref b6,ref b5,Rotations[1,2]); Mix(ref b0,ref b3,Rotations[1,3]);
      Mix(ref b4,ref b1,Rotations[2,0]); Mix(ref b6,ref b3,Rotations[2,1]); Mix(ref b0,ref b5,Rotations[2,2]); Mix(ref b2,ref b7,Rotations[2,3]);
      Mix(ref b6,ref b1,Rotations[3,0]); Mix(ref b0,ref b7,Rotations[3,1]); Mix(ref b2,ref b5,Rotations[3,2]); Mix(ref b4,ref b3,Rotations[3,3]);
      Inject(ref b0,ref b1,ref b2,ref b3,ref b4,ref b5,ref b6,ref b7,kw,t,d);
      Mix(ref b0,ref b1,Rotations[4,0]); Mix(ref b2,ref b3,Rotations[4,1]); Mix(ref b4,ref b5,Rotations[4,2]); Mix(ref b6,ref b7,Rotations[4,3]);
      Mix(ref b2,ref b1,Rotations[5,0]); Mix(ref b4,ref b7,Rotations[5,1]); Mix(ref b6,ref b5,Rotations[5,2]); Mix(ref b0,ref b3,Rotations[5,3]);
      Mix(ref b4,ref b1,Rotations[6,0]); Mix(ref b6,ref b3,Rotations[6,1]); Mix(ref b0,ref b5,Rotations[6,2]); Mix(ref b2,ref b7,Rotations[6,3]);
      Mix(ref b6,ref b1,Rotations[7,0]); Mix(ref b0,ref b7,Rotations[7,1]); Mix(ref b2,ref b5,Rotations[7,2]); Mix(ref b4,ref b3,Rotations[7,3]);
      Inject(ref b0,ref b1,ref b2,ref b3,ref b4,ref b5,ref b6,ref b7,kw,t,d+1);
    }
    return [b0,b1,b2,b3,b4,b5,b6,b7];
  }

  private static void Mix(ref ulong a, ref ulong b, int r) { a=unchecked(a+b); b=BitOperations.RotateLeft(b,r)^a; }

  private static void Inject(ref ulong b0,ref ulong b1,ref ulong b2,ref ulong b3,ref ulong b4,ref ulong b5,ref ulong b6,ref ulong b7,ReadOnlySpan<ulong> key,ReadOnlySpan<ulong> tweak,int s) {
    var m9=s%9; var m3=s%3;
    b0=unchecked(b0+key[m9]); b1=unchecked(b1+key[m9+1]); b2=unchecked(b2+key[m9+2]); b3=unchecked(b3+key[m9+3]);
    b4=unchecked(b4+key[m9+4]); b5=unchecked(b5+key[m9+5]+tweak[m3]); b6=unchecked(b6+key[m9+6]+tweak[m3+1]); b7=unchecked(b7+key[m9+7]+(ulong)s);
  }

  private sealed class Ubi {
    private readonly byte[] _block = new byte[BlockBytes];
    private int _offset;
    private ulong _position;
    private ulong _tweak1;

    public void Reset(int type) { _position=0; _tweak1=((ulong)type<<56)|FirstFlag; _offset=0; Array.Clear(_block); }

    public void Update(ReadOnlySpan<byte> data, ulong[] chain) {
      var source=0;
      while (source<data.Length) {
        if (_offset==BlockBytes) { Process(chain); _tweak1&=~FirstFlag; _offset=0; Array.Clear(_block); }
        var take=Math.Min(data.Length-source,BlockBytes-_offset);
        data.Slice(source,take).CopyTo(_block.AsSpan(_offset));
        source+=take; _offset+=take; _position=unchecked(_position+(ulong)take);
      }
    }

    public void Finalize(ulong[] chain) { _block.AsSpan(_offset).Clear(); _tweak1|=FinalFlag; Process(chain); }

    private void Process(ulong[] chain) {
      Span<ulong> message=stackalloc ulong[8];
      for (var i=0;i<8;++i) message[i]=BinaryPrimitives.ReadUInt64LittleEndian(_block.AsSpan(i*8,8));
      Span<ulong> tweak=stackalloc ulong[2]; tweak[0]=_position; tweak[1]=_tweak1;
      var encrypted=Encrypt(chain,tweak,message);
      for (var i=0;i<8;++i) chain[i]=encrypted[i]^message[i];
    }
  }
}
