using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// Provides the Luffa-224 hash implementation.
/// </summary>
public static class Luffa224 {
  /// <summary>
  /// Computes the Luffa-224 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => LuffaCore.Compute(data, 28, 3);
}
/// <summary>
/// Provides the Luffa-256 hash implementation.
/// </summary>
public static class Luffa256 {
  /// <summary>
  /// Computes the Luffa-256 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => LuffaCore.Compute(data, 32, 3);
}
/// <summary>
/// Provides the Luffa-384 hash implementation.
/// </summary>
public static class Luffa384 {
  /// <summary>
  /// Computes the Luffa-384 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => LuffaCore.Compute(data, 48, 4);
}
/// <summary>
/// Provides the Luffa-512 hash implementation.
/// </summary>
public static class Luffa512 {
  /// <summary>
  /// Computes the Luffa-512 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => LuffaCore.Compute(data, 64, 5);
}

internal static class LuffaCore {
  private static readonly uint[][] Initial = [
    [0x6D251E69U,0x44B051E0U,0x4EAA6FB4U,0xDBF78465U,0x6E292011U,0x90152DF4U,0xEE058139U,0xDEF610BBU],
    [0xC3B44B95U,0xD9D2F256U,0x70EEE9A0U,0xDE099FA3U,0x5D9B0557U,0x8FC944B3U,0xCF1CCF0EU,0x746CD581U],
    [0xF7EFC89DU,0x5DBA5781U,0x04016CE5U,0xAD659C05U,0x0306194FU,0x666D1836U,0x24AA230AU,0x8B264AE7U],
    [0x858075D5U,0x36D79CCEU,0xE571F7D7U,0x204B1F67U,0x35870C6AU,0x57E9E923U,0x14BCB808U,0x7CDE72CEU],
    [0x6C68E9BEU,0x5EC41E22U,0xC825B7C7U,0xAFFB4363U,0xF5DF3999U,0x0FC688F1U,0xB07224CCU,0x03E86CEAU]
  ];

  private static readonly uint[][] Rc0 = [
    [0x303994A6U,0xC0E65299U,0x6CC33A12U,0xDC56983EU,0x1E00108FU,0x7800423DU,0x8F5B7882U,0x96E1DB12U],
    [0xB6DE10EDU,0x70F47AAEU,0x0707A3D4U,0x1C1E8F51U,0x707A3D45U,0xAEB28562U,0xBACA1589U,0x40A46F3EU],
    [0xFC20D9D2U,0x34552E25U,0x7AD8818FU,0x8438764AU,0xBB6DE032U,0xEDB780C8U,0xD9847356U,0xA2C78434U],
    [0xB213AFA5U,0xC84EBE95U,0x4E608A22U,0x56D858FEU,0x343B138FU,0xD0EC4E3DU,0x2CEB4882U,0xB3AD2208U],
    [0xF0D2E9E3U,0xAC11D7FAU,0x1BCB66F2U,0x6F2D9BC9U,0x78602649U,0x8EDAE952U,0x3B6BA548U,0xEDAE9520U]
  ];

  private static readonly uint[][] Rc4 = [
    [0xE0337818U,0x441BA90DU,0x7F34D442U,0x9389217FU,0xE5A8BCE6U,0x5274BAF4U,0x26889BA7U,0x9A226E9DU],
    [0x01685F3DU,0x05A17CF4U,0xBD09CACAU,0xF4272B28U,0x144AE5CCU,0xFAA7AE2BU,0x2E48F1C1U,0xB923C704U],
    [0xE25E72C1U,0xE623BB72U,0x5C58A4A4U,0x1E38E2E7U,0x78E38B9DU,0x27586719U,0x36EDA57FU,0x703AACE7U],
    [0xE028C9BFU,0x44756F91U,0x7E8FCE32U,0x956548BEU,0xFE191BE2U,0x3CB226E5U,0x5944A28EU,0xA1C4C355U],
    [0x5090D577U,0x2D1925ABU,0xB46496ACU,0xD1925AB0U,0x29131AB6U,0x0FC053C3U,0x3F014F0CU,0xFC053C31U]
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes, int chains) {
    var state = new uint[chains][];
    for (var i=0;i<chains;++i)
      state[i]=Initial[i].ToArray();

    Span<uint> message=stackalloc uint[8];
    var offset=0;
    while (offset+32<=data.Length) {
      Decode(data.Slice(offset,32),message);
      InjectAndPermute(state,message);
      offset+=32;
    }

    Span<byte> final=stackalloc byte[32];
    final.Clear();
    data[offset..].CopyTo(final);
    final[data.Length-offset]=0x80;
    Span<uint> finalMessage=stackalloc uint[8];
    Decode(final,finalMessage);
    Span<uint> zero=stackalloc uint[8];
    zero.Clear();

    var words=new List<uint>();
    var rounds=chains==3?2:3;
    for (var round=0;round<rounds;++round) {
      InjectAndPermute(state,round==0?finalMessage:zero);
      if (chains==3 && round==1) {
        var combined=Combine(state);
        for (var i=0;i<(outputBytes+3)/4;++i) words.Add(combined[i]);
      } else if (chains>=4 && round==1) {
        var combined=Combine(state);
        for (var i=0;i<8;++i) words.Add(combined[i]);
      } else if (chains>=4 && round==2) {
        var combined=Combine(state);
        for (var i=0;i<(outputBytes-32+3)/4;++i) words.Add(combined[i]);
      }
    }

    var result=new byte[outputBytes];
    Span<byte> bytes=stackalloc byte[4];
    var written=0;
    foreach (var word in words) {
      if (written>=outputBytes) break;
      BinaryPrimitives.WriteUInt32BigEndian(bytes,word);
      var take=Math.Min(4,outputBytes-written);
      bytes[..take].CopyTo(result.AsSpan(written));
      written+=take;
    }
    return result;
  }

  private static void Decode(ReadOnlySpan<byte> block, Span<uint> message) {
    for (var i=0;i<8;++i)
      message[i]=BinaryPrimitives.ReadUInt32BigEndian(block.Slice(i*4,4));
  }

  private static uint[] Combine(uint[][] state) {
    var output=new uint[8];
    for (var i=0;i<8;++i) {
      var value=0U;
      foreach (var chain in state) value^=chain[i];
      output[i]=value;
    }
    return output;
  }

  private static void InjectAndPermute(uint[][] state, ReadOnlySpan<uint> message) {
    switch (state.Length) {
      case 3: Inject3(state[0],state[1],state[2],message); break;
      case 4: Inject4(state[0],state[1],state[2],state[3],message); break;
      case 5: Inject5(state[0],state[1],state[2],state[3],state[4],message); break;
    }
    Permute(state);
  }

  private static void Permute(uint[][] state) {
    for (var chain=1;chain<state.Length;++chain)
      for (var i=4;i<8;++i)
        state[chain][i]=BitOperations.RotateLeft(state[chain][i],chain);

    for (var round=0;round<8;++round)
      for (var chain=0;chain<state.Length;++chain)
        Step(state[chain],Rc0[chain][round],Rc4[chain][round]);
  }

  private static void Step(uint[] v,uint rc0,uint rc4) {
    SubCrumb(v,0,1,2,3);
    SubCrumb(v,5,6,7,4);
    MixWord(v,0,4); MixWord(v,1,5); MixWord(v,2,6); MixWord(v,3,7);
    v[0]^=rc0; v[4]^=rc4;
  }

  private static void SubCrumb(uint[] v,int i0,int i1,int i2,int i3) {
    var tmp=v[i0];
    v[i0]|=v[i1]; v[i2]^=v[i3]; v[i1]=~v[i1]; v[i0]^=v[i3];
    v[i3]&=tmp; v[i1]^=v[i3]; v[i3]^=v[i2]; v[i2]&=v[i0]; v[i0]=~v[i0];
    v[i2]^=v[i1]; v[i1]|=v[i3]; tmp^=v[i1]; v[i3]^=v[i2]; v[i2]&=v[i1]; v[i1]^=v[i0]; v[i0]=tmp;
  }

  private static void MixWord(uint[] v,int u,int w) {
    v[w]^=v[u]; v[u]=BitOperations.RotateLeft(v[u],2)^v[w]; v[w]=BitOperations.RotateLeft(v[w],14)^v[u];
    v[u]=BitOperations.RotateLeft(v[u],10)^v[w]; v[w]=BitOperations.RotateLeft(v[w],1);
  }

  private static void M2(Span<uint> destination, ReadOnlySpan<uint> source) {
    Span<uint> copy=stackalloc uint[8];
    source.CopyTo(copy);
    var tmp=copy[7];
    destination[7]=copy[6]; destination[6]=copy[5]; destination[5]=copy[4];
    destination[4]=copy[3]^tmp; destination[3]=copy[2]^tmp; destination[2]=copy[1]; destination[1]=copy[0]^tmp; destination[0]=tmp;
  }

  private static void Inject3(uint[] v0,uint[] v1,uint[] v2,ReadOnlySpan<uint> message) {
    Span<uint> a=stackalloc uint[8]; Span<uint> m=stackalloc uint[8]; message.CopyTo(m);
    for (var i=0;i<8;++i) a[i]=v0[i]^v1[i]^v2[i];
    M2(a,a);
    for (var i=0;i<8;++i) v0[i]^=a[i]^m[i];
    M2(m,m); for (var i=0;i<8;++i) v1[i]^=a[i]^m[i];
    M2(m,m); for (var i=0;i<8;++i) v2[i]^=a[i]^m[i];
  }

  private static void Inject4(uint[] v0,uint[] v1,uint[] v2,uint[] v3,ReadOnlySpan<uint> message) {
    Span<uint> m=stackalloc uint[8]; Span<uint> a=stackalloc uint[8]; Span<uint> b=stackalloc uint[8]; message.CopyTo(m);
    for (var i=0;i<8;++i) { a[i]=v0[i]^v1[i]^v2[i]^v3[i]; b[i]=v2[i]^v3[i]; }
    M2(a,a);
    for (var i=0;i<8;++i) { v0[i]^=a[i]; v1[i]^=a[i]; v2[i]^=a[i]; v3[i]^=a[i]; }
    M2(b,v0); for (var i=0;i<8;++i) b[i]^=v3[i];
    M2(v3,v3); for (var i=0;i<8;++i) v3[i]^=v2[i];
    M2(v2,v2); for (var i=0;i<8;++i) v2[i]^=v1[i];
    M2(v1,v1); for (var i=0;i<8;++i) v1[i]^=v0[i];
    for (var i=0;i<8;++i) v0[i]=b[i]^m[i];
    M2(m,m); for (var i=0;i<8;++i) v1[i]^=m[i];
    M2(m,m); for (var i=0;i<8;++i) v2[i]^=m[i];
    M2(m,m); for (var i=0;i<8;++i) v3[i]^=m[i];
  }

  private static void Inject5(uint[] v0,uint[] v1,uint[] v2,uint[] v3,uint[] v4,ReadOnlySpan<uint> message) {
    Span<uint> m=stackalloc uint[8]; Span<uint> a=stackalloc uint[8]; Span<uint> b=stackalloc uint[8]; message.CopyTo(m);
    for (var i=0;i<8;++i) { a[i]=v0[i]^v1[i]^v2[i]^v3[i]^v4[i]; b[i]=v2[i]^v3[i]; }
    M2(a,a);
    for (var i=0;i<8;++i) { v0[i]^=a[i]; v1[i]^=a[i]; v2[i]^=a[i]; v3[i]^=a[i]; v4[i]^=a[i]; }
    M2(b,v0); for (var i=0;i<8;++i) b[i]^=v1[i];
    M2(v1,v1); for (var i=0;i<8;++i) v1[i]^=v2[i];
    M2(v2,v2); for (var i=0;i<8;++i) v2[i]^=v3[i];
    M2(v3,v3); for (var i=0;i<8;++i) v3[i]^=v4[i];
    M2(v4,v4); for (var i=0;i<8;++i) v4[i]^=v0[i];
    M2(v0,b); for (var i=0;i<8;++i) v0[i]^=v4[i];
    M2(v4,v4); for (var i=0;i<8;++i) v4[i]^=v3[i];
    M2(v3,v3); for (var i=0;i<8;++i) v3[i]^=v2[i];
    M2(v2,v2); for (var i=0;i<8;++i) v2[i]^=v1[i];
    M2(v1,v1); for (var i=0;i<8;++i) { v1[i]^=b[i]; v0[i]^=m[i]; }
    M2(m,m); for (var i=0;i<8;++i) v1[i]^=m[i];
    M2(m,m); for (var i=0;i<8;++i) v2[i]^=m[i];
    M2(m,m); for (var i=0;i<8;++i) v3[i]^=m[i];
    M2(m,m); for (var i=0;i<8;++i) v4[i]^=m[i];
  }
}
