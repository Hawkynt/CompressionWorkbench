using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Korean LSH-224 hash function.</summary>
public static class Lsh224 {
  public static byte[] Compute(ReadOnlySpan<byte> data) => Lsh256Core.Compute(data, Lsh256Core.Iv224, 28);
}

/// <summary>Korean LSH-256 hash function.</summary>
public static class Lsh256 {
  public static byte[] Compute(ReadOnlySpan<byte> data) => Lsh256Core.Compute(data, Lsh256Core.Iv256, 32);
}

internal static class Lsh256Core {
  internal static readonly uint[] Iv224 = [
    0x068608D3U,0x62D8F7A7U,0xD76652ABU,0x4C600A43U,
    0xBDC40AA8U,0x1ECA0B68U,0xDA1A89BEU,0x3147D354U,
    0x707EB4F9U,0xF65B3862U,0x6B0B2ABEU,0x56B8EC0AU,
    0xCF237286U,0xEE0D1727U,0x33636595U,0x8BB8D05FU
  ];

  internal static readonly uint[] Iv256 = [
    0x46A10F1FU,0xFDDCE486U,0xB41443A8U,0x198E6B9DU,
    0x3304388DU,0xB0F5A3C7U,0xB36061C4U,0x7ADBD553U,
    0x105D5378U,0x2F74DE54U,0x5C2F2D95U,0xF2553FBEU,
    0x8051357AU,0x138668C8U,0x47AA4484U,0xE01AFB41U
  ];

  private static readonly int[] Gamma = [0,8,16,24,24,16,8,0];

  private static readonly uint[] StepConstants = [
    0x917CAF90U,0x6C1B10A2U,0x6F352943U,0xCF778243U,0x2CEB7472U,0x29E96FF2U,0x8A9BA428U,0x2EEB2642U,
    0x0E2C4021U,0x872BB30EU,0xA45E6CB2U,0x46F9C612U,0x185FE69EU,0x1359621BU,0x263FCCB2U,0x1A116870U,
    0x3A6C612FU,0xB2DEC195U,0x02CB1F56U,0x40BFD858U,0x784684B6U,0x6CBB7D2EU,0x660C7ED8U,0x2B79D88AU,
    0xA6CD9069U,0x91A05747U,0xCDEA7558U,0x00983098U,0xBECB3B2EU,0x2838AB9AU,0x728B573EU,0xA55262B5U,
    0x745DFA0FU,0x31F79ED8U,0xB85FCE25U,0x98C8C898U,0x8A0669ECU,0x60E445C2U,0xFDE295B0U,0xF7B5185AU,
    0xD2580983U,0x29967709U,0x182DF3DDU,0x61916130U,0x90705676U,0x452A0822U,0xE07846ADU,0xACCD7351U,
    0x2A618D55U,0xC00D8032U,0x4621D0F5U,0xF2F29191U,0x00C6CD06U,0x6F322A67U,0x58BEF48DU,0x7A40C4FDU,
    0x8BEEE27FU,0xCD8DB2F2U,0x67F2C63BU,0xE5842383U,0xC793D306U,0xA15C91D6U,0x17B381E5U,0xBB05C277U,
    0x7AD1620AU,0x5B40A5BFU,0x5AB901A2U,0x69A7A768U,0x5B66D9CDU,0xFDEE6877U,0xCB3566FCU,0xC0C83A32U,
    0x4C336C84U,0x9BE6651AU,0x13BAA3FCU,0x114F0FD1U,0xC240A728U,0xEC56E074U,0x009C63C7U,0x89026CF2U,
    0x7F9FF0D0U,0x824B7FB5U,0xCE5EA00FU,0x605EE0E2U,0x02E7CFEAU,0x43375560U,0x9D002AC7U,0x8B6F5F7BU,
    0x1F90C14FU,0xCDCB3537U,0x2CFEAFDDU,0xBF3FC342U,0xEAB7B9ECU,0x7A8CB5A3U,0x9D2AF264U,0xFACEDB06U,
    0xB052106EU,0x99006D04U,0x2BAE8D09U,0xFF030601U,0xA271A6D6U,0x0742591DU,0xC81D5701U,0xC9A9E200U,
    0x02627F1EU,0x996D719DU,0xDA3B9634U,0x02090800U,0x14187D78U,0x499B7624U,0xE57458C9U,0x738BE2C9U,
    0x64E19D20U,0x06DF0F36U,0x15D1CB0EU,0x0B110802U,0x2C95F58CU,0xE5119A6DU,0x59CD22AEU,0xFF6EAC3CU,
    0x467EBD84U,0xE5EE453CU,0xE79CD923U,0x1C190A0DU,0xC28B81B8U,0xF6AC0852U,0x26EFD107U,0x6E1AE93BU,
    0xC53C41CAU,0xD4338221U,0x8475FD0AU,0x35231729U,0x4E0D3A7AU,0xA2B45B48U,0x16C0D82DU,0x890424A9U,
    0x017E0C8FU,0x07B5A3F5U,0xFA73078EU,0x583A405EU,0x5B47B4C8U,0x570FA3EAU,0xD7990543U,0x8D28CE32U,
    0x7F8A9B90U,0xBD5998FCU,0x6D7A9688U,0x927A9EB6U,0xA2FC7D23U,0x66B38E41U,0x709E491AU,0xB5F700BFU,
    0x0A262C0FU,0x16F295B9U,0xE8111EF5U,0x0D195548U,0x9F79A0C5U,0x1A41CFA7U,0x0EE7638AU,0xACF7C074U,
    0x30523B19U,0x09884ECFU,0xF93014DDU,0x266E9D55U,0x191A6664U,0x5C1176C1U,0xF64AED98U,0xA4B83520U,
    0x828D5449U,0x91D71DD8U,0x2944F2D6U,0x950BF27BU,0x3380CA7DU,0x6D88381DU,0x4138868EU,0x5CED55C4U,
    0x0FE19DCBU,0x68F4F669U,0x6E37C8FFU,0xA0FE6E10U,0xB44B47B0U,0xF5C0558AU,0x79BF14CFU,0x4A431A20U,
    0xF17F68DAU,0x5DEB5FD1U,0xA600C86DU,0x9F6C7EB0U,0xFF92F864U,0xB615E07FU,0x38D3E448U,0x8D5D3A6AU,
    0x70E843CBU,0x494B312EU,0xA6C93613U,0x0BEB2F4FU,0x928B5D63U,0xCBF66035U,0x0CB82C80U,0xEA97A4F7U,
    0x592C0F3BU,0x947C5F77U,0x6FFF49B9U,0xF71A7E5AU,0x1DE8C0F5U,0xC2569600U,0xC4E4AC8CU,0x823C9CE1U
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, uint[] initial, int outputBytes) {
    var left = initial[..8].ToArray();
    var right = initial[8..].ToArray();
    var offset = 0;
    while (offset + 128 <= data.Length) {
      Compress(left, right, data.Slice(offset, 128));
      offset += 128;
    }

    var final = new byte[128];
    data[offset..].CopyTo(final);
    final[data.Length - offset] = 0x80;
    Compress(left, right, final);

    var result = new byte[outputBytes];
    for (var i = 0; i < 8; ++i)
      left[i] ^= right[i];
    for (var i = 0; i < outputBytes / 4; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4, 4), left[i]);
    return result;
  }

  private static void Compress(uint[] left, uint[] right, ReadOnlySpan<byte> block) {
    var sub = new uint[32];
    for (var i = 0; i < 8; ++i) {
      sub[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
      sub[8 + i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(32 + i * 4, 4));
      sub[16 + i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(64 + i * 4, 4));
      sub[24 + i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(96 + i * 4, 4));
    }

    Add(left, right, sub, 0);
    Mix(left, right, 29, 1, 0);
    Permute(left, right);
    Add(left, right, sub, 16);
    Mix(left, right, 5, 17, 8);
    Permute(left, right);

    for (var step = 1; step < 13; ++step) {
      ExpandEven(sub);
      Add(left, right, sub, 0);
      Mix(left, right, 29, 1, step * 16);
      Permute(left, right);

      ExpandOdd(sub);
      Add(left, right, sub, 16);
      Mix(left, right, 5, 17, step * 16 + 8);
      Permute(left, right);
    }

    ExpandEven(sub);
    Add(left, right, sub, 0);
  }

  private static void Add(uint[] left, uint[] right, uint[] sub, int baseOffset) {
    for (var i = 0; i < 8; ++i) {
      left[i] ^= sub[baseOffset + i];
      right[i] ^= sub[baseOffset + 8 + i];
    }
  }

  private static void Mix(uint[] left, uint[] right, int alpha, int beta, int constantOffset) {
    for (var i = 0; i < 8; ++i) {
      left[i] = BitOperations.RotateLeft(unchecked(left[i] + right[i]), alpha) ^ StepConstants[constantOffset + i];
      right[i] = BitOperations.RotateLeft(unchecked(right[i] + left[i]), beta);
      left[i] = unchecked(left[i] + right[i]);
      if (i is > 0 and < 7)
        right[i] = BitOperations.RotateLeft(right[i], Gamma[i]);
    }
  }

  private static void ExpandEven(uint[] s) {
    ExpandQuarter(s, 0, 16);
    ExpandQuarter(s, 8, 24);
  }

  private static void ExpandOdd(uint[] s) {
    ExpandQuarter(s, 16, 0);
    ExpandQuarter(s, 24, 8);
  }

  private static void ExpandQuarter(uint[] s, int target, int other) {
    var a0=s[target]; var a1=s[target+1]; var a2=s[target+2]; var a3=s[target+3];
    var a4=s[target+4]; var a5=s[target+5]; var a6=s[target+6]; var a7=s[target+7];
    s[target] = unchecked(s[other] + a3);
    s[target+3] = unchecked(s[other+3] + a1);
    s[target+1] = unchecked(s[other+1] + a2);
    s[target+2] = unchecked(s[other+2] + a0);
    s[target+4] = unchecked(s[other+4] + a7);
    s[target+7] = unchecked(s[other+7] + a6);
    s[target+6] = unchecked(s[other+6] + a5);
    s[target+5] = unchecked(s[other+5] + a4);
  }

  private static void Permute(uint[] l, uint[] r) {
    var t=l[0];
    l[0]=l[6]; l[6]=r[6]; r[6]=r[2]; r[2]=l[1];
    l[1]=l[4]; l[4]=r[4]; r[4]=r[0]; r[0]=l[2];
    l[2]=l[5]; l[5]=r[7]; r[7]=r[1]; r[1]=t;
    t=l[3]; l[3]=l[7]; l[7]=r[5]; r[5]=r[3]; r[3]=t;
  }
}
