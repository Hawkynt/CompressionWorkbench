using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// Provides the RIPEMD-128 hash implementation.
/// </summary>
public static class Ripemd128 { public static byte[] Compute(ReadOnlySpan<byte> data) => RipemdCore.Compute(data, 128); }
/// <summary>
/// Provides the RIPEMD-160 hash implementation.
/// </summary>
public static class Ripemd160 { public static byte[] Compute(ReadOnlySpan<byte> data) => RipemdCore.Compute(data, 160); }
/// <summary>
/// Provides the RIPEMD-256 hash implementation.
/// </summary>
public static class Ripemd256 { public static byte[] Compute(ReadOnlySpan<byte> data) => RipemdCore.Compute(data, 256); }
/// <summary>
/// Provides the RIPEMD-320 hash implementation.
/// </summary>
public static class Ripemd320 { public static byte[] Compute(ReadOnlySpan<byte> data) => RipemdCore.Compute(data, 320); }

internal static class RipemdCore {
  private static readonly byte[] Rl = [
    0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15, 7,4,13,1,10,6,15,3,12,0,9,5,2,14,11,8,
    3,10,14,4,9,15,8,1,2,7,0,6,13,11,5,12, 1,9,11,10,0,8,12,4,13,3,7,15,14,5,6,2,
    4,0,5,9,7,12,2,10,14,1,3,8,11,6,15,13
  ];
  private static readonly byte[] Rr = [
    5,14,7,0,9,2,11,4,13,6,15,8,1,10,3,12, 6,11,3,7,0,13,5,10,14,15,8,12,4,9,1,2,
    15,5,1,3,7,14,6,9,11,8,12,2,10,0,4,13, 8,6,4,1,3,11,15,0,5,12,2,13,9,7,10,14,
    12,15,10,4,1,5,8,7,6,2,13,14,0,3,9,11
  ];
  private static readonly byte[] Sl = [
    11,14,15,12,5,8,7,9,11,13,14,15,6,7,9,8, 7,6,8,13,11,9,7,15,7,12,15,9,11,7,13,12,
    11,13,6,7,14,9,13,15,14,8,13,6,5,12,7,5, 11,12,14,15,14,15,9,8,9,14,5,6,8,6,5,12,
    9,15,5,11,6,8,13,12,5,12,13,14,11,8,5,6
  ];
  private static readonly byte[] Sr = [
    8,9,9,11,13,15,15,5,7,7,8,11,14,14,12,6, 9,13,15,7,12,8,9,11,7,7,12,7,6,15,13,11,
    9,7,15,11,8,6,6,14,12,13,5,14,13,13,7,5, 15,5,8,11,14,14,6,14,6,9,12,9,12,5,15,8,
    8,5,12,9,12,5,14,6,8,13,6,5,15,13,11,11
  ];
  private static readonly uint[] Kl = [0U,0x5A827999U,0x6ED9EBA1U,0x8F1BBCDCU,0xA953FD4EU];
  private static readonly uint[] Kr = [0x50A28BE6U,0x5C4DD124U,0x6D703EF3U,0x7A6D76E9U,0U];

  /// <summary>Right-line constants for the four-round variants.</summary>
  /// <remarks>
  /// RIPEMD-128 and -256 run four rounds, and their last right-line constant is
  /// zero — not the fifth-round constant the five-round variants use. Taking the
  /// value from the RIPEMD-160 table changes every word the fourth round touches.
  /// </remarks>
  private static readonly uint[] KrShort = [0x50A28BE6U,0x5C4DD124U,0x6D703EF3U,0U];

  public static byte[] Compute(ReadOnlySpan<byte> data, int bits) {
    uint[] h = bits switch {
      128 => [0x67452301U,0xEFCDAB89U,0x98BADCFEU,0x10325476U],
      160 => [0x67452301U,0xEFCDAB89U,0x98BADCFEU,0x10325476U,0xC3D2E1F0U],
      256 => [0x67452301U,0xEFCDAB89U,0x98BADCFEU,0x10325476U,0x76543210U,0xFEDCBA98U,0x89ABCDEFU,0x01234567U],
      320 => [0x67452301U,0xEFCDAB89U,0x98BADCFEU,0x10325476U,0xC3D2E1F0U,0x76543210U,0xFEDCBA98U,0x89ABCDEFU,0x01234567U,0x3C2D1E0FU],
      _ => throw new ArgumentOutOfRangeException(nameof(bits))
    };

    var paddedLength = ((data.Length + 9 + 63) / 64) * 64;
    var padded = new byte[paddedLength];
    data.CopyTo(padded);
    padded[data.Length] = 0x80;
    BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(paddedLength - 8), (ulong)data.Length * 8);

    Span<uint> x = stackalloc uint[16];
    for (var offset = 0; offset < padded.Length; offset += 64) {
      for (var i = 0; i < 16; ++i)
        x[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(offset + i * 4, 4));
      switch (bits) {
        case 128: Compress128(h, x); break;
        case 160: Compress160(h, x); break;
        case 256: Compress256(h, x); break;
        case 320: Compress320(h, x); break;
      }
    }

    var result = new byte[bits / 8];
    for (var i = 0; i < h.Length; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), h[i]);
    return result;
  }

  private static uint F(int n, uint x, uint y, uint z) => n switch {
    0 => x ^ y ^ z,
    1 => (x & y) | (~x & z),
    2 => (x | ~y) ^ z,
    3 => (x & z) | (y & ~z),
    4 => x ^ (y | ~z),
    _ => 0
  };

  private static void Compress128(uint[] h, ReadOnlySpan<uint> x) {
    var al=h[0]; var bl=h[1]; var cl=h[2]; var dl=h[3];
    var ar=al; var br=bl; var cr=cl; var dr=dl;
    for (var i=0;i<64;++i) {
      var round=i>>4;
      var tl=BitOperations.RotateLeft(unchecked(al + F(round,bl,cl,dl) + x[Rl[i]] + Kl[round]), Sl[i]);
      al=dl; dl=cl; cl=bl; bl=tl;
      var rr=3-round;
      var tr=BitOperations.RotateLeft(unchecked(ar + F(rr,br,cr,dr) + x[Rr[i]] + KrShort[round]), Sr[i]);
      ar=dr; dr=cr; cr=br; br=tr;
    }
    var t=unchecked(h[1]+cl+dr);
    h[1]=unchecked(h[2]+dl+ar);
    h[2]=unchecked(h[3]+al+br);
    h[3]=unchecked(h[0]+bl+cr);
    h[0]=t;
  }

  private static void Compress160(uint[] h, ReadOnlySpan<uint> x) {
    var al=h[0]; var bl=h[1]; var cl=h[2]; var dl=h[3]; var el=h[4];
    var ar=al; var br=bl; var cr=cl; var dr=dl; var er=el;
    for (var i=0;i<80;++i) {
      var round=i>>4;
      var tl=unchecked(BitOperations.RotateLeft(unchecked(al + F(round,bl,cl,dl) + x[Rl[i]] + Kl[round]), Sl[i]) + el);
      al=el; el=dl; dl=BitOperations.RotateLeft(cl,10); cl=bl; bl=tl;
      var rr=4-round;
      var tr=unchecked(BitOperations.RotateLeft(unchecked(ar + F(rr,br,cr,dr) + x[Rr[i]] + Kr[round]), Sr[i]) + er);
      ar=er; er=dr; dr=BitOperations.RotateLeft(cr,10); cr=br; br=tr;
    }
    var t=unchecked(h[1]+cl+dr);
    h[1]=unchecked(h[2]+dl+er);
    h[2]=unchecked(h[3]+el+ar);
    h[3]=unchecked(h[4]+al+br);
    h[4]=unchecked(h[0]+bl+cr);
    h[0]=t;
  }

  private static void Compress256(uint[] h, ReadOnlySpan<uint> x) {
    var a=h[0]; var b=h[1]; var c=h[2]; var d=h[3];
    var aa=h[4]; var bb=h[5]; var cc=h[6]; var dd=h[7];
    for (var i=0;i<64;++i) {
      var round=i>>4;
      var tl=BitOperations.RotateLeft(unchecked(a + F(round,b,c,d) + x[Rl[i]] + Kl[round]), Sl[i]);
      a=d; d=c; c=b; b=tl;
      var rr=3-round;
      var tr=BitOperations.RotateLeft(unchecked(aa + F(rr,bb,cc,dd) + x[Rr[i]] + KrShort[round]), Sr[i]);
      aa=dd; dd=cc; cc=bb; bb=tr;
      if ((i & 15)==15) {
        switch (round) {
          case 0: (a,aa)=(aa,a); break;
          case 1: (b,bb)=(bb,b); break;
          case 2: (c,cc)=(cc,c); break;
          case 3: (d,dd)=(dd,d); break;
        }
      }
    }
    h[0]=unchecked(h[0]+a); h[1]=unchecked(h[1]+b); h[2]=unchecked(h[2]+c); h[3]=unchecked(h[3]+d);
    h[4]=unchecked(h[4]+aa); h[5]=unchecked(h[5]+bb); h[6]=unchecked(h[6]+cc); h[7]=unchecked(h[7]+dd);
  }

  private static void Compress320(uint[] h, ReadOnlySpan<uint> x) {
    var a=h[0]; var b=h[1]; var c=h[2]; var d=h[3]; var e=h[4];
    var aa=h[5]; var bb=h[6]; var cc=h[7]; var dd=h[8]; var ee=h[9];
    for (var i=0;i<80;++i) {
      var round=i>>4;
      var tl=unchecked(BitOperations.RotateLeft(unchecked(a + F(round,b,c,d) + x[Rl[i]] + Kl[round]), Sl[i]) + e);
      a=e; e=d; d=BitOperations.RotateLeft(c,10); c=b; b=tl;
      var rr=4-round;
      var tr=unchecked(BitOperations.RotateLeft(unchecked(aa + F(rr,bb,cc,dd) + x[Rr[i]] + Kr[round]), Sr[i]) + ee);
      aa=ee; ee=dd; dd=BitOperations.RotateLeft(cc,10); cc=bb; bb=tr;
      if ((i & 15)==15) {
        switch (round) {
          case 0: (b,bb)=(bb,b); break;
          case 1: (d,dd)=(dd,d); break;
          case 2: (a,aa)=(aa,a); break;
          case 3: (c,cc)=(cc,c); break;
          case 4: (e,ee)=(ee,e); break;
        }
      }
    }
    h[0]=unchecked(h[0]+a); h[1]=unchecked(h[1]+b); h[2]=unchecked(h[2]+c); h[3]=unchecked(h[3]+d); h[4]=unchecked(h[4]+e);
    h[5]=unchecked(h[5]+aa); h[6]=unchecked(h[6]+bb); h[7]=unchecked(h[7]+cc); h[8]=unchecked(h[8]+dd); h[9]=unchecked(h[9]+ee);
  }
}
