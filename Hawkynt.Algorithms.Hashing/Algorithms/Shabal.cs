using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// Provides the Shabal-192 hash implementation.
/// </summary>
public static class Shabal192 { public static byte[] Compute(ReadOnlySpan<byte> data) => ShabalCore.Compute(data, 24); }
/// <summary>
/// Provides the Shabal-224 hash implementation.
/// </summary>
public static class Shabal224 { public static byte[] Compute(ReadOnlySpan<byte> data) => ShabalCore.Compute(data, 28); }
/// <summary>
/// Provides the Shabal-256 hash implementation.
/// </summary>
public static class Shabal256 { public static byte[] Compute(ReadOnlySpan<byte> data) => ShabalCore.Compute(data, 32); }
/// <summary>
/// Provides the Shabal-384 hash implementation.
/// </summary>
public static class Shabal384 { public static byte[] Compute(ReadOnlySpan<byte> data) => ShabalCore.Compute(data, 48); }
/// <summary>
/// Provides the Shabal-512 hash implementation.
/// </summary>
public static class Shabal512 { public static byte[] Compute(ReadOnlySpan<byte> data) => ShabalCore.Compute(data, 64); }

internal static class ShabalCore {
  private sealed record InitialState(uint[] A,uint[] B,uint[] C);

  private static readonly InitialState Init192 = new(
    [0xFD749ED4U,0xB798E530U,0x33904B6FU,0x46BDA85EU,0x076934B4U,0x454B4058U,0x77F74527U,0xFB4CF465U,0x62931DA9U,0xE778C8DBU,0x22B3998EU,0xAC15CFB9U],
    [0x58BCBAC4U,0xEC47A08EU,0xAEE933B2U,0xDFCBC824U,0xA7944804U,0xBF65BDB0U,0x5A9D4502U,0x599779AFU,0xC5CEA54EU,0x4B6B8150U,0x16E71909U,0x7D632319U,0x930573A0U,0xF34C63D1U,0xCAF914B4U,0xFDD6612CU],
    [0x61550878U,0x89EF2B75U,0xA1660C46U,0x7EF3855BU,0x7297B58CU,0x1BC67793U,0x7FB1C723U,0xB66FC640U,0x1A48B71CU,0xF0976D17U,0x088CE80AU,0xA454EDF3U,0x1C096BF4U,0xAC76224BU,0x5215781CU,0xCD5D2669U]
  );

  private static readonly InitialState Init224 = new(
    [0xA5201467U,0xA9B8D94AU,0xD4CED997U,0x68379D7BU,0xA7FC73BAU,0xF1A2546BU,0x606782BFU,0xE0BCFD0FU,0x2F25374EU,0x069A149FU,0x5E2DFF25U,0xFAECF061U],
    [0xEC9905D8U,0xF21850CFU,0xC0A746C8U,0x21DAD498U,0x35156EEBU,0x088C97F2U,0x26303E40U,0x8A2D4FB5U,0xFEEE44B6U,0x8A1E9573U,0x7B81111AU,0xCBC139F0U,0xA3513861U,0x1D2C362EU,0x918C580EU,0xB58E1B9CU],
    [0xE4B573A1U,0x4C1A0880U,0x1E907C51U,0x04807EFDU,0x3AD8CDE5U,0x16B21302U,0x02512C53U,0x2204CB18U,0x99405F2DU,0xE5B648A1U,0x70AB1D43U,0xA10C25C2U,0x16F1AC05U,0x38BBEB56U,0x9B01DC60U,0xB1096D83U]
  );

  private static readonly InitialState Init256 = new(
    [0x52F84552U,0xE54B7999U,0x2D8EE3ECU,0xB9645191U,0xE0078B86U,0xBB7C44C9U,0xD2B5C1CAU,0xB0D2EB8CU,0x14CE5A45U,0x22AF50DCU,0xEFFDBC6BU,0xEB21B74AU],
    [0xB555C6EEU,0x3E710596U,0xA72A652FU,0x9301515FU,0xDA28C1FAU,0x696FD868U,0x9CB6BF72U,0x0AFE4002U,0xA6E03615U,0x5138C1D4U,0xBE216306U,0xB38B8890U,0x3EA8B96BU,0x3299ACE4U,0x30924DD4U,0x55CB34A5U],
    [0xB405F031U,0xC4233EBAU,0xB3733979U,0xC0DD9D55U,0xC51C28AEU,0xA327B8E1U,0x56C56167U,0xED614433U,0x88B59D60U,0x60E2CEBAU,0x758B4B8BU,0x83E82A7FU,0xBC968828U,0xE6E00BF7U,0xBA839E55U,0x9B491C60U]
  );

  private static readonly InitialState Init384 = new(
    [0xC8FCA331U,0xE55C504EU,0x003EBF26U,0xBB6B8D83U,0x7B0448C1U,0x41B82789U,0x0A7C9601U,0x8D659CFFU,0xB6E2673EU,0xCA54C77BU,0x1460FD7EU,0x3FCB8F2DU],
    [0x527291FCU,0x2A16455FU,0x78E627E5U,0x944F169FU,0x1CA6F016U,0xA854EA25U,0x8DB98ABEU,0xF2C62641U,0x301117DCU,0xCF5C4309U,0x93711A25U,0xF9F671B8U,0xB01D2116U,0x333F4B89U,0xB285D165U,0x86829B36U],
    [0xF764B11AU,0x76172146U,0xCEF6934DU,0xC6D28399U,0xFE095F61U,0x5E6018B4U,0x5048ECF5U,0x51353261U,0x6E6E36DCU,0x63130DADU,0xA9C69BD6U,0x1E90EA0CU,0x7C35073BU,0x28D95E6DU,0xAA340E0DU,0xCB3DEE70U]
  );

  private static readonly InitialState Init512 = new(
    [0x20728DFDU,0x46C0BD53U,0xE782B699U,0x55304632U,0x71B4EF90U,0x0EA9E82CU,0xDBB930F1U,0xFAD06B8BU,0xBE0CAE40U,0x8BD14410U,0x76D2ADACU,0x28ACAB7FU],
    [0xC1099CB7U,0x07B385F3U,0xE7442C26U,0xCC8AD640U,0xEB6F56C7U,0x1EA81AA9U,0x73B9D314U,0x1DE85D08U,0x48910A5AU,0x893B22DBU,0xC5A0DF44U,0xBBC4324EU,0x72D2F240U,0x75941D99U,0x6D8BDE82U,0xA1A7502BU],
    [0xD9BF68D1U,0x58BAD750U,0x560228CBU,0x8134F359U,0xB5D469D8U,0x941A8CC2U,0x418B2A6EU,0x04052780U,0x7F07D787U,0x5194358FU,0x3C60D665U,0xBE97D79AU,0x950C3434U,0xAED9A06DU,0x2537DC8DU,0x7CDB5969U]
  );

  public static byte[] Compute(ReadOnlySpan<byte> data,int outputBytes) {
    var initial=outputBytes switch { 24=>Init192,28=>Init224,32=>Init256,48=>Init384,64=>Init512,_=>throw new ArgumentOutOfRangeException(nameof(outputBytes)) };
    var a=initial.A.ToArray(); var b=initial.B.ToArray(); var c=initial.C.ToArray();
    ulong w=1;
    Span<uint> message=stackalloc uint[16];
    var offset=0;
    while (offset+64<=data.Length) {
      Decode(data.Slice(offset,64),message);
      Process(a,ref b,ref c,message,ref w,true);
      offset+=64;
    }

    Span<byte> final=stackalloc byte[64]; final.Clear(); data[offset..].CopyTo(final); final[data.Length-offset]=0x80;
    Decode(final,message);
    AddMessage(b,message); XorCounter(a,w); Permute(a,b,c,message);
    for (var i=0;i<3;++i) { (b,c)=(c,b); XorCounter(a,w); Permute(a,b,c,message); }

    var result=new byte[outputBytes];
    for (var i=0;i<outputBytes/4;++i)
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i*4,4),b[i]);
    return result;
  }

  private static void Process(uint[] a,ref uint[] b,ref uint[] c,ReadOnlySpan<uint> m,ref ulong w,bool increment) {
    AddMessage(b,m); XorCounter(a,w); Permute(a,b,c,m); SubtractMessage(c,m); (b,c)=(c,b); if(increment)++w;
  }

  private static void Decode(ReadOnlySpan<byte> block,Span<uint> m) { for(var i=0;i<16;++i)m[i]=BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i*4,4)); }
  private static void AddMessage(uint[] b,ReadOnlySpan<uint> m) { for(var i=0;i<16;++i)b[i]=unchecked(b[i]+m[i]); }
  private static void SubtractMessage(uint[] c,ReadOnlySpan<uint> m) { for(var i=0;i<16;++i)c[i]=unchecked(c[i]-m[i]); }
  private static void XorCounter(uint[] a,ulong w) { a[0]^=(uint)w; a[1]^=(uint)(w>>32); }

  private static void Permute(uint[] a,uint[] b,uint[] c,ReadOnlySpan<uint> m) {
    for(var i=0;i<16;++i)b[i]=BitOperations.RotateLeft(b[i],17);
    for(var pass=0;pass<3;++pass) {
      var shift=pass*4;
      for(var i=0;i<16;++i) {
        var xa0=(i+shift)%12;
        var xa1=(xa0+11)%12;
        PermElt(a,b,c,xa0,xa1,i,(i+13)&15,(i+9)&15,(i+6)&15,(8-i)&15,m[i]);
      }
    }
    for(var i=0;i<12;++i)
      a[i]=unchecked(a[i]+c[(i+11)&15]+c[(i+15)&15]+c[(i+3)&15]);
  }

  private static void PermElt(uint[] a,uint[] b,uint[] c,int xa0,int xa1,int xb0,int xb1,int xb2,int xb3,int xc0,uint message) {
    var mixed=unchecked(((a[xa0]^(BitOperations.RotateLeft(a[xa1],15)*5U)^c[xc0])*3U))^b[xb1]^(b[xb2]&~b[xb3])^message;
    a[xa0]=mixed;
    b[xb0]=~(BitOperations.RotateLeft(b[xb0],1)^mixed);
  }
}
