using System.Buffers.Binary;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// Small-state Hamsi core shared by Hamsi-224 and Hamsi-256.
/// </summary>
/// <remarks>
/// This is an implementation detail of <see cref="HamsiFamily"/>. Hamsi has two standardized
/// state widths, so the family dispatches by output-size range while each state width keeps one
/// compression implementation. It does not duplicate the core per digest size.
/// </remarks>
internal static class HamsiSmall {
  private static readonly uint[] Iv224 = [
    0xc3967a67U,0xc3bc6c20U,0x4bc3bcc3U,0xa7c3bc6bU,
    0x2c204b61U,0x74686f6cU,0x69656b65U,0x20556e69U
  ];

  private static readonly uint[] Iv256 = [
    0x76657273U,0x69746569U,0x74204c65U,0x7576656eU,
    0x2c204465U,0x70617274U,0x656d656eU,0x7420456cU
  ];

  private static readonly uint[] AlphaNormal = [
    0xff00f0f0U,0xccccaaaaU,0xf0f0ccccU,0xff00aaaaU,0xccccaaaaU,0xf0f0ff00U,0xaaaaccccU,0xf0f0ff00U,
    0xf0f0ccccU,0xaaaaff00U,0xccccff00U,0xaaaaf0f0U,0xaaaaf0f0U,0xff00ccccU,0xccccf0f0U,0xff00aaaaU,
    0xccccaaaaU,0xff00f0f0U,0xff00aaaaU,0xf0f0ccccU,0xf0f0ff00U,0xccccaaaaU,0xf0f0ff00U,0xaaaaccccU,
    0xaaaaff00U,0xf0f0ccccU,0xaaaaf0f0U,0xccccff00U,0xff00ccccU,0xaaaaf0f0U,0xff00aaaaU,0xccccf0f0U
  ];

  private static readonly uint[] AlphaFinal = [
    0xcaf9639cU,0x0ff0f9c0U,0x639c0ff0U,0xcaf9f9c0U,0x0ff0f9c0U,0x639ccaf9U,0xf9c00ff0U,0x639ccaf9U,
    0x639c0ff0U,0xf9c0caf9U,0x0ff0caf9U,0xf9c0639cU,0xf9c0639cU,0xcaf90ff0U,0x0ff0639cU,0xcaf9f9c0U,
    0x0ff0f9c0U,0xcaf9639cU,0xcaf9f9c0U,0x639c0ff0U,0x639ccaf9U,0x0ff0f9c0U,0x639ccaf9U,0xf9c00ff0U,
    0xf9c0caf9U,0x639c0ff0U,0xf9c0639cU,0x0ff0caf9U,0xcaf90ff0U,0xf9c0639cU,0xcaf9f9c0U,0x0ff0639cU
  ];

  // sphlib SPH_HAMSI_EXPAND_SMALL table. One row per input bit.
  private static readonly uint[][] T256 = [
    [0x74951000U,0x5a2b467eU,0x88fd1d2bU,0x1ee68292U,0xcba90000U,0x90273769U,0xbbdcf407U,0xd0f4af61U],
    [0xcba90000U,0x90273769U,0xbbdcf407U,0xd0f4af61U,0xbf3c1000U,0xca0c7117U,0x3321e92cU,0xce122df3U],
    [0xe92a2000U,0xb4578cfcU,0x11fa3a57U,0x3dc90524U,0x97530000U,0x204f6ed3U,0x77b9e80fU,0xa1ec5ec1U],
    [0x97530000U,0x204f6ed3U,0x77b9e80fU,0xa1ec5ec1U,0x7e792000U,0x9418e22fU,0x6643d258U,0x9c255be5U],
    [0x121b4000U,0x5b17d9e8U,0x8dfacfabU,0xce36cc72U,0xe6570000U,0x4bb33a25U,0x848598baU,0x1041003eU],
    [0xe6570000U,0x4bb33a25U,0x848598baU,0x1041003eU,0xf44c4000U,0x10a4e3cdU,0x097f5711U,0xde77cc4cU],
    [0xe4788000U,0x859673c1U,0xb5fb2452U,0x29cc5edfU,0x045f0000U,0x9c4a93c9U,0x62fc79d0U,0x731ebdc2U],
    [0x045f0000U,0x9c4a93c9U,0x62fc79d0U,0x731ebdc2U,0xe0278000U,0x19dce008U,0xd7075d82U,0x5ad2e31dU],
    [0xb7a40100U,0x8a1f31d8U,0x8589d8abU,0xe6c46464U,0x734c0000U,0x956fa7d6U,0xa29d1297U,0x6ee56854U],
    [0x734c0000U,0x956fa7d6U,0xa29d1297U,0x6ee56854U,0xc4e80100U,0x1f70960eU,0x2714ca3cU,0x88210c30U],
    [0xa7b80200U,0x1f128433U,0x60e5f9f2U,0x9e147576U,0xee260000U,0x124b683eU,0x80c2d68fU,0x3bf3ab2cU],
    [0xee260000U,0x124b683eU,0x80c2d68fU,0x3bf3ab2cU,0x499e0200U,0x0d59ec0dU,0xe0272f7dU,0xa5e7de5aU],
    [0x8f3e0400U,0x0d9dc877U,0x6fc548e1U,0x898d2cd6U,0x14bd0000U,0x2fba37ffU,0x6a72e5bbU,0x247febe6U],
    [0x14bd0000U,0x2fba37ffU,0x6a72e5bbU,0x247febe6U,0x9b830400U,0x2227ff88U,0x05b7ad5aU,0xadf2c730U],
    [0xde320800U,0x288350feU,0x71852ac7U,0xa6bf9f96U,0xe18b0000U,0x5459887dU,0xbf1283d3U,0x1b666a73U],
    [0xe18b0000U,0x5459887dU,0xbf1283d3U,0x1b666a73U,0x3fb90800U,0x7cdad883U,0xce97a914U,0xbdd9f5e5U],
    [0x515c0010U,0x40f372fbU,0xfce72602U,0x71575061U,0x2e390000U,0x64dd6689U,0x3cd406fcU,0xb1f490bcU],
    [0x2e390000U,0x64dd6689U,0x3cd406fcU,0xb1f490bcU,0x7f650010U,0x242e1472U,0xc03320feU,0xc0a3c0ddU],
    [0xa2b80020U,0x81e7e5f6U,0xf9ce4c04U,0xe2afa0c0U,0x5c720000U,0xc9bacd12U,0x79a90df9U,0x63e92178U],
    [0x5c720000U,0xc9bacd12U,0x79a90df9U,0x63e92178U,0xfeca0020U,0x485d28e4U,0x806741fdU,0x814681b8U],
    [0x4dce0040U,0x3b5bec7eU,0x36656ba8U,0x23633a05U,0x78ab0000U,0xa0cd5a34U,0x5d5ca0f7U,0x727784cbU],
    [0x78ab0000U,0xa0cd5a34U,0x5d5ca0f7U,0x727784cbU,0x35650040U,0x9b96b64aU,0x6b39cb5fU,0x5114beceU],
    [0x5bd20080U,0x450f18ecU,0xc2c46c55U,0xf362b233U,0x39a60000U,0x4ab753ebU,0xd14e094bU,0xb772b42bU],
    [0x39a60000U,0x4ab753ebU,0xd14e094bU,0xb772b42bU,0x62740080U,0x0fb84b07U,0x138a651eU,0x44100618U],
    [0xc04e0001U,0x33b9c010U,0xae0ebb05U,0xb5a4c63bU,0xc8f10000U,0x0b2de782U,0x6bf648a4U,0x539cbdbfU],
    [0xc8f10000U,0x0b2de782U,0x6bf648a4U,0x539cbdbfU,0x08bf0001U,0x38942792U,0xc5f8f3a1U,0xe6387b84U],
    [0x88230002U,0x5fe7a7b3U,0x99e585aaU,0x8d75f7f1U,0x51ac0000U,0x25e30f14U,0x79e22a4cU,0x1298bd46U],
    [0x51ac0000U,0x25e30f14U,0x79e22a4cU,0x1298bd46U,0xd98f0002U,0x7a04a8a7U,0xe007afe6U,0x9fed4ab7U],
    [0xd0080004U,0x8c768f77U,0x9dc5b050U,0xaf4a29daU,0x6ba90000U,0x40ebf9aaU,0x98321c3dU,0x76acc733U],
    [0x6ba90000U,0x40ebf9aaU,0x98321c3dU,0x76acc733U,0xbba10004U,0xcc9d76ddU,0x05f7ac6dU,0xd9e6eee9U],
    [0xa8ae0008U,0x2079397dU,0xfe739301U,0xb8a92831U,0x171c0000U,0xb26e3344U,0x9e6a837eU,0x58f8485fU],
    [0x171c0000U,0xb26e3344U,0x9e6a837eU,0x58f8485fU,0xbfb20008U,0x92170a39U,0x6019107fU,0xe051606eU]
  ];

  internal static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits) {
    if (hashSizeBits is not (224 or 256))
      throw new ArgumentOutOfRangeException(nameof(hashSizeBits));

    var h = (uint[])(hashSizeBits == 224 ? Iv224.Clone() : Iv256.Clone());
    var offset = 0;
    while (offset + 4 <= data.Length) {
      Compress(h, data.Slice(offset, 4), 3, AlphaNormal);
      offset += 4;
    }

    Span<byte> finalBlock = stackalloc byte[4];
    finalBlock.Clear();
    data[offset..].CopyTo(finalBlock);
    finalBlock[data.Length - offset] = 0x80;
    Compress(h, finalBlock, 3, AlphaNormal);

    var bitLength = checked((ulong)data.Length * 8UL);
    Span<byte> countBlock = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(countBlock, (uint)(bitLength >> 32));
    Compress(h, countBlock, 3, AlphaNormal);
    BinaryPrimitives.WriteUInt32BigEndian(countBlock, (uint)bitLength);
    Compress(h, countBlock, 6, AlphaFinal);

    var result = new byte[hashSizeBits / 8];
    for (var i = 0; i < result.Length / 4; ++i)
      BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4), h[i]);
    return result;
  }

  private static void Compress(uint[] h, ReadOnlySpan<byte> block, int rounds, uint[] alpha) {
    Span<uint> m = stackalloc uint[8];
    m.Clear();
    for (var byteIndex = 0; byteIndex < 4; ++byteIndex) {
      var value = block[byteIndex];
      for (var bit = 0; bit < 8; ++bit) {
        if ((value & (1 << bit)) == 0)
          continue;
        var row = T256[byteIndex * 8 + bit];
        for (var word = 0; word < 8; ++word)
          m[word] ^= row[word];
      }
    }

    Span<uint> s = stackalloc uint[16];
    s[0]=m[0];s[1]=m[1];s[2]=h[0];s[3]=h[1];
    s[4]=h[2];s[5]=h[3];s[6]=m[2];s[7]=m[3];
    s[8]=m[4];s[9]=m[5];s[10]=h[4];s[11]=h[5];
    s[12]=h[6];s[13]=h[7];s[14]=m[6];s[15]=m[7];

    for (var round = 0; round < rounds; ++round) {
      s[0]^=alpha[0];s[1]^=alpha[1]^(uint)round;s[2]^=alpha[2];s[3]^=alpha[3];
      s[4]^=alpha[8];s[5]^=alpha[9];s[6]^=alpha[10];s[7]^=alpha[11];
      s[8]^=alpha[16];s[9]^=alpha[17];s[10]^=alpha[18];s[11]^=alpha[19];
      s[12]^=alpha[24];s[13]^=alpha[25];s[14]^=alpha[26];s[15]^=alpha[27];

      SBox(s,0,4,8,12);SBox(s,1,5,9,13);SBox(s,2,6,10,14);SBox(s,3,7,11,15);
      Linear(s,0,5,10,15);Linear(s,1,6,11,12);Linear(s,2,7,8,13);Linear(s,3,4,9,14);
    }

    h[0]^=s[0];h[1]^=s[1];h[2]^=s[2];h[3]^=s[3];
    h[4]^=s[8];h[5]^=s[9];h[6]^=s[10];h[7]^=s[11];
  }

  private static void SBox(Span<uint> s, int ia, int ib, int ic, int id) {
    var a=s[ia];var b=s[ib];var c=s[ic];var d=s[id];var t=a;
    a=(a&c)^d;c^=b^a;d=(d|t)^b;t^=c;b=d;d=(d|t)^a;a&=b;t^=a;b^=d;b^=t;a=c;c=b;b=d;d=~t;
    s[ia]=a;s[ib]=b;s[ic]=c;s[id]=d;
  }

  private static void Linear(Span<uint> s, int ia, int ib, int ic, int id) {
    s[ia]=RotateLeft(s[ia],13);s[ic]=RotateLeft(s[ic],3);s[ib]^=s[ia]^s[ic];s[id]^=s[ic]^(s[ia]<<3);
    s[ib]=RotateLeft(s[ib],1);s[id]=RotateLeft(s[id],7);s[ia]^=s[ib]^s[id];s[ic]^=s[id]^(s[ib]<<7);
    s[ia]=RotateLeft(s[ia],5);s[ic]=RotateLeft(s[ic],22);
  }

  private static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));
}
