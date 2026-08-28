using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>HAVAL variable-pass, variable-output cryptographic hash.</summary>
public static class Haval {
  private static readonly uint[] Initial = [
    0x243F6A88U,0x85A308D3U,0x13198A2EU,0x03707344U,
    0xA4093822U,0x299F31D0U,0x082EFA98U,0xEC4E6C89U
  ];

  private static readonly byte[][] WordPermutations = [
    [0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31],
    [5,14,26,18,11,28,7,16,0,23,20,22,1,10,4,8,30,3,21,9,17,24,29,6,19,12,15,13,2,25,31,27],
    [19,9,4,20,28,17,8,22,29,14,25,12,24,30,16,26,31,15,7,3,1,0,18,27,13,6,21,10,23,11,5,2],
    [24,4,0,14,2,7,28,23,26,6,30,20,18,25,19,3,22,11,31,21,8,27,12,9,1,29,5,15,17,10,16,13],
    [27,3,21,26,17,11,20,29,19,0,12,7,13,8,31,10,5,9,14,30,18,6,28,24,2,23,16,22,4,1,25,15]
  ];

  private static readonly uint[][] Constants = [
    new uint[32],
    [0x452821E6U,0x38D01377U,0xBE5466CFU,0x34E90C6CU,0xC0AC29B7U,0xC97C50DDU,0x3F84D5B5U,0xB5470917U,
     0x9216D5D9U,0x8979FB1BU,0xD1310BA6U,0x98DFB5ACU,0x2FFD72DBU,0xD01ADFB7U,0xB8E1AFEDU,0x6A267E96U,
     0xBA7C9045U,0xF12C7F99U,0x24A19947U,0xB3916CF7U,0x0801F2E2U,0x858EFC16U,0x636920D8U,0x71574E69U,
     0xA458FEA3U,0xF4933D7EU,0x0D95748FU,0x728EB658U,0x718BCD58U,0x82154AEEU,0x7B54A41DU,0xC25A59B5U],
    [0x9C30D539U,0x2AF26013U,0xC5D1B023U,0x286085F0U,0xCA417918U,0xB8DB38EFU,0x8E79DCB0U,0x603A180EU,
     0x6C9E0E8BU,0xB01E8A3EU,0xD71577C1U,0xBD314B27U,0x78AF2FDAU,0x55605C60U,0xE65525F3U,0xAA55AB94U,
     0x57489862U,0x63E81440U,0x55CA396AU,0x2AAB10B6U,0xB4CC5C34U,0x1141E8CEU,0xA15486AFU,0x7C72E993U,
     0xB3EE1411U,0x636FBC2AU,0x2BA9C55DU,0x741831F6U,0xCE5C3E16U,0x9B87931EU,0xAFD6BA33U,0x6C24CF5CU],
    [0x7A325381U,0x28958677U,0x3B8F4898U,0x6B4BB9AFU,0xC4BFE81BU,0x66282193U,0x61D809CCU,0xFB21A991U,
     0x487CAC60U,0x5DEC8032U,0xEF845D5DU,0xE98575B1U,0xDC262302U,0xEB651B88U,0x23893E81U,0xD396ACC5U,
     0x0F6D6FF3U,0x83F44239U,0x2E0B4482U,0xA4842004U,0x69C8F04AU,0x9E1F9B5EU,0x21C66842U,0xF6E96C9AU,
     0x670C9C61U,0xABD388F0U,0x6A51A0D2U,0xD8542F68U,0x960FA728U,0xAB5133A3U,0x6EEF0B6CU,0x137A3BE4U],
    [0xBA3BF050U,0x7EFB2A98U,0xA1F1651DU,0x39AF0176U,0x66CA593EU,0x82430E88U,0x8CEE8619U,0x456F9FB4U,
     0x7D84A5C3U,0x3B8B5EBEU,0xE06F75D8U,0x85C12073U,0x401A449FU,0x56C16AA6U,0x4ED3AA62U,0x363F7706U,
     0x1BFEDF72U,0x429B023DU,0x37D0D724U,0xD00A1248U,0xDB0FEAD3U,0x49F1C09BU,0x075372C9U,0x80991B7BU,
     0x25D479D8U,0xF6E8DEF7U,0xE3FE501AU,0xB6794C3BU,0x976CE0BDU,0x04C006BAU,0xC1A94FB6U,0x409F60C4U]
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, int passes = 5, int outputBits = 256) {
    if (passes is < 3 or > 5)
      throw new ArgumentOutOfRangeException(nameof(passes), "HAVAL uses 3, 4, or 5 passes.");
    if (outputBits is not (128 or 160 or 192 or 224 or 256))
      throw new ArgumentOutOfRangeException(nameof(outputBits));

    var state = Initial.ToArray();
    var offset = 0;
    while (offset + 128 <= data.Length) {
      ProcessBlock(state, data.Slice(offset, 128), passes);
      offset += 128;
    }

    var remaining = data.Length - offset;
    var finalBytes = remaining + 1 + 10;
    var paddedLength = ((finalBytes + 127) / 128) * 128;
    var padded = new byte[paddedLength];
    data[offset..].CopyTo(padded);
    padded[remaining] = 0x01;
    var footer = paddedLength - 10;
    padded[footer] = (byte)(1 | (passes << 3));
    padded[footer + 1] = (byte)((outputBits / 32) << 3);
    BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(footer + 2, 8), unchecked((ulong)data.Length * 8UL));

    for (var blockOffset = 0; blockOffset < padded.Length; blockOffset += 128)
      ProcessBlock(state, padded.AsSpan(blockOffset, 128), passes);

    return Tailor(state, outputBits);
  }

  public static byte[] Compute128(ReadOnlySpan<byte> data, int passes = 3) => Compute(data, passes, 128);
  public static byte[] Compute160(ReadOnlySpan<byte> data, int passes = 4) => Compute(data, passes, 160);
  public static byte[] Compute192(ReadOnlySpan<byte> data, int passes = 4) => Compute(data, passes, 192);
  public static byte[] Compute224(ReadOnlySpan<byte> data, int passes = 4) => Compute(data, passes, 224);
  public static byte[] Compute256(ReadOnlySpan<byte> data, int passes = 5) => Compute(data, passes, 256);

  private static void ProcessBlock(uint[] state, ReadOnlySpan<byte> block, int passes) {
    Span<uint> words = stackalloc uint[32];
    for (var i = 0; i < 32; ++i)
      words[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));

    Span<uint> s = stackalloc uint[8];
    state.CopyTo(s);

    for (var pass = 0; pass < passes; ++pass) {
      var permutation = WordPermutations[pass];
      var constants = Constants[pass];
      for (var i = 0; i < 32; ++i) {
        var target = 7 - (i & 7);
        var x6 = s[(target + 7) & 7];
        var x5 = s[(target + 6) & 7];
        var x4 = s[(target + 5) & 7];
        var x3 = s[(target + 4) & 7];
        var x2 = s[(target + 3) & 7];
        var x1 = s[(target + 2) & 7];
        var x0 = s[(target + 1) & 7];
        var temp = Phi(passes, pass + 1, x6,x5,x4,x3,x2,x1,x0);
        s[target] = unchecked(BitOperations.RotateRight(temp, 7) + BitOperations.RotateRight(s[target], 11) + words[permutation[i]] + constants[i]);
      }
    }

    for (var i = 0; i < 8; ++i)
      state[i] = unchecked(state[i] + s[i]);
  }

  private static uint Phi(int passes, int pass, uint x6,uint x5,uint x4,uint x3,uint x2,uint x1,uint x0) => (passes, pass) switch {
    (3,1) => F1(x1,x0,x3,x5,x6,x2,x4),
    (3,2) => F2(x4,x2,x1,x0,x5,x3,x6),
    (3,3) => F3(x6,x1,x2,x3,x4,x5,x0),
    (4,1) => F1(x2,x6,x1,x4,x5,x3,x0),
    (4,2) => F2(x3,x5,x2,x0,x1,x6,x4),
    (4,3) => F3(x1,x4,x3,x6,x0,x2,x5),
    (4,4) => F4(x6,x4,x0,x5,x2,x1,x3),
    (5,1) => F1(x3,x4,x1,x0,x5,x2,x6),
    (5,2) => F2(x6,x2,x1,x0,x3,x4,x5),
    (5,3) => F3(x2,x6,x0,x4,x3,x1,x5),
    (5,4) => F4(x1,x5,x3,x2,x0,x4,x6),
    (5,5) => F5(x2,x5,x0,x6,x4,x3,x1),
    _ => throw new InvalidOperationException()
  };

  private static uint F1(uint x6,uint x5,uint x4,uint x3,uint x2,uint x1,uint x0) => (x1 & (x0 ^ x4)) ^ (x2 & x5) ^ (x3 & x6) ^ x0;
  private static uint F2(uint x6,uint x5,uint x4,uint x3,uint x2,uint x1,uint x0) => (x2 & ((x1 & ~x3) ^ (x4 & x5) ^ x6 ^ x0)) ^ (x4 & (x1 ^ x5)) ^ (x3 & x5) ^ x0;
  private static uint F3(uint x6,uint x5,uint x4,uint x3,uint x2,uint x1,uint x0) => (x3 & ((x1 & x2) ^ x6 ^ x0)) ^ (x1 & x4) ^ (x2 & x5) ^ x0;
  private static uint F4(uint x6,uint x5,uint x4,uint x3,uint x2,uint x1,uint x0) => (x3 & ((x1 & x2) ^ (x4 | x6) ^ x5)) ^ (x4 & ((~x2 & x5) ^ x1 ^ x6 ^ x0)) ^ (x2 & x6) ^ x0;
  private static uint F5(uint x6,uint x5,uint x4,uint x3,uint x2,uint x1,uint x0) => (x0 & ~((x1 & x2 & x3) ^ x5)) ^ (x1 & x4) ^ (x2 & x5) ^ (x3 & x6);

  private static byte[] Tailor(uint[] s, int bits) {
    Span<uint> words = stackalloc uint[8];
    s.CopyTo(words);
    var count = bits / 32;

    if (bits == 128) {
      words[0]=unchecked(s[0]+Mix128(s[7],s[4],s[5],s[6],24));
      words[1]=unchecked(s[1]+Mix128(s[6],s[7],s[4],s[5],16));
      words[2]=unchecked(s[2]+Mix128(s[5],s[6],s[7],s[4],8));
      words[3]=unchecked(s[3]+Mix128(s[4],s[5],s[6],s[7],0));
    } else if (bits == 160) {
      words[0]=unchecked(s[0]+Mix160_0(s[5],s[6],s[7])); words[1]=unchecked(s[1]+Mix160_1(s[5],s[6],s[7]));
      words[2]=unchecked(s[2]+Mix160_2(s[5],s[6],s[7])); words[3]=unchecked(s[3]+Mix160_3(s[5],s[6],s[7]));
      words[4]=unchecked(s[4]+Mix160_4(s[5],s[6],s[7]));
    } else if (bits == 192) {
      words[0]=unchecked(s[0]+Mix192_0(s[6],s[7])); words[1]=unchecked(s[1]+Mix192_1(s[6],s[7]));
      words[2]=unchecked(s[2]+Mix192_2(s[6],s[7])); words[3]=unchecked(s[3]+Mix192_3(s[6],s[7]));
      words[4]=unchecked(s[4]+Mix192_4(s[6],s[7])); words[5]=unchecked(s[5]+Mix192_5(s[6],s[7]));
    } else if (bits == 224) {
      words[0]=unchecked(s[0]+((s[7]>>27)&0x1FU)); words[1]=unchecked(s[1]+((s[7]>>22)&0x1FU));
      words[2]=unchecked(s[2]+((s[7]>>18)&0x0FU)); words[3]=unchecked(s[3]+((s[7]>>13)&0x1FU));
      words[4]=unchecked(s[4]+((s[7]>>9)&0x0FU)); words[5]=unchecked(s[5]+((s[7]>>4)&0x1FU));
      words[6]=unchecked(s[6]+(s[7]&0x0FU));
    }

    var result = new byte[bits / 8];
    for (var i=0;i<count;++i)
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i*4,4), words[i]);
    return result;
  }

  private static uint Mix128(uint a0,uint a1,uint a2,uint a3,int rotation) {
    var value=(a0&0x000000FFU)|(a1&0x0000FF00U)|(a2&0x00FF0000U)|(a3&0xFF000000U);
    return rotation==0?value:BitOperations.RotateLeft(value,rotation);
  }
  private static uint Mix160_0(uint x5,uint x6,uint x7)=>BitOperations.RotateLeft((x5&0x01F80000U)|(x6&0xFE000000U)|(x7&0x0000003FU),13);
  private static uint Mix160_1(uint x5,uint x6,uint x7)=>BitOperations.RotateLeft((x5&0xFE000000U)|(x6&0x0000003FU)|(x7&0x00000FC0U),7);
  private static uint Mix160_2(uint x5,uint x6,uint x7)=>(x5&0x0000003FU)|(x6&0x00000FC0U)|(x7&0x0007F000U);
  private static uint Mix160_3(uint x5,uint x6,uint x7)=>BitOperations.RotateLeft((x5&0x00000FC0U)|(x6&0x0007F000U)|(x7&0x01F80000U),6);
  private static uint Mix160_4(uint x5,uint x6,uint x7)=>BitOperations.RotateLeft((x5&0x0007F000U)|(x6&0x01F80000U)|(x7&0xFE000000U),12);
  private static uint Mix192_0(uint x6,uint x7)=>BitOperations.RotateLeft((x6&0xFC000000U)|(x7&0x0000001FU),6);
  private static uint Mix192_1(uint x6,uint x7)=>(x6&0x0000001FU)|(x7&0x000003E0U);
  private static uint Mix192_2(uint x6,uint x7)=>BitOperations.RotateLeft((x6&0x000003E0U)|(x7&0x0000FC00U),5);
  private static uint Mix192_3(uint x6,uint x7)=>BitOperations.RotateLeft((x6&0x0000FC00U)|(x7&0x001F0000U),10);
  private static uint Mix192_4(uint x6,uint x7)=>BitOperations.RotateLeft((x6&0x001F0000U)|(x7&0x03E00000U),16);
  private static uint Mix192_5(uint x6,uint x7)=>BitOperations.RotateLeft((x6&0x03E00000U)|(x7&0xFC000000U),21);
}
